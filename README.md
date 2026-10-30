# pgtest

`pgtest` is an ASP.NET Core performance-test API for generating synthetic market-tick data, compressing it as a Zstandard (`.zst`) CSV, and bulk-loading that data into PostgreSQL with Npgsql binary `COPY`.

## What the feature does

The intended end-to-end workflow is:

1. `POST /test/createcsv` generates one or more CSV partitions in `csv_data/`.
2. The partitions are merged, with only one header row.
3. The merged CSV is compressed to `csv_data/all_tick_data_merged.csv.zst` and the uncompressed/intermediate files are removed.
4. `POST /test/insertCsvToPg` streams the `.zst` file through a bounded producer/consumer channel and writes rows to `external.tick_data` with PostgreSQL binary `COPY`.

Each generated row has this shape:

| CSV column | Generated value | PostgreSQL-compatible type |
|---|---|---|
| `Symbol` | Random symbol from `Database/tickers.csv` | `text` |
| `Provider` | `Fenics`, `Bloomberg`, or `Reuters` | `text` |
| `Bid` | Random value from 0 to 2,000, rounded to 5 decimals | `double precision` |
| `Ask` | Random value from 0 to 2,000, rounded to 5 decimals | `double precision` |
| `LastPrice` | Random value from 0 to 2,000, rounded to 5 decimals | `double precision`, nullable |
| `Volume` | Random integer from 0 to 99,999,999 | `double precision`, nullable in the current importer |
| `TickTimestamp` | UTC timestamp from the preceding five days | `timestamp with time zone` |
| `LastUpdatedBy` | Random 8-character alphanumeric user name | `text` |
| `LastUpdatedOnUtc` | Current UTC timestamp | `timestamp with time zone` |

The ticker source contains 1,000 data rows. Only its first column (`ticker`) is consumed.

## Requirements

- .NET SDK 10.0
- PostgreSQL reachable by the API process (only required for import)
- Enough free disk space for all uncompressed partitions plus the merged CSV during generation
- Enough memory for the import channel, which can hold up to 200,000 CSV line strings

Confirm the SDK:

```powershell
dotnet --version
```

## Quick start

From the repository root:

```powershell
dotnet restore
dotnet build
dotnet run --launch-profile http
```

The HTTP launch profile listens on `http://localhost:5006` and sets the environment to `Development`. Keep that terminal open while making requests from a second terminal.

Development-only API discovery is available at:

- Scalar UI: <http://localhost:5006/scalar/v1>
- OpenAPI JSON: <http://localhost:5006/openapi/v1.json>

The app also has an HTTPS profile:

```powershell
dotnet run --launch-profile https
```

That profile listens on `https://localhost:7241` and `http://localhost:5006`. Trust the local development certificate if necessary:

```powershell
dotnet dev-certs https --trust
```

## Configuration

The application reads the PostgreSQL connection from `ConnectionStrings:DefaultConnection`. The checked-in development value is:

```text
Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres
```

Prefer overriding it for the current shell rather than committing credentials:

```powershell
$env:ConnectionStrings__DefaultConnection = 'Host=localhost;Port=5432;Database=postgres;Username=pgtest;Password=replace-me'
dotnet run --launch-profile http
```

ASP.NET Core maps the double underscore in `ConnectionStrings__DefaultConnection` to the configuration colon. Remove the override afterward if desired:

```powershell
Remove-Item Env:ConnectionStrings__DefaultConnection
```

All file paths used by the generator are relative to the process working directory. Run the service from the repository root so it can find `Database/tickers.csv` and so output lands in `./csv_data`.

## Prepare PostgreSQL

The importer assumes that the `external` schema and `external.tick_data` table already exist; it does not create either one. A compatible test table is:

```sql
CREATE SCHEMA IF NOT EXISTS external;

CREATE TABLE IF NOT EXISTS external.tick_data (
    symbol                  text                     NOT NULL,
    provider                text                     NOT NULL,
    bid                     double precision         NOT NULL,
    ask                     double precision         NOT NULL,
    last_price              double precision,
    volume                  double precision,
    tick_timestamp          timestamp with time zone NOT NULL,
    last_updated_by         text                     NOT NULL,
    last_updated_on_utc     timestamp with time zone NOT NULL
);
```

For an isolated benchmark database, create the indexes the loader expects to manage:

```sql
CREATE INDEX IF NOT EXISTS idx_tick_timestamp
    ON external.tick_data (tick_timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_tick_symbol
    ON external.tick_data (symbol);
CREATE INDEX IF NOT EXISTS idx_tick_symbol_ts
    ON external.tick_data (symbol, tick_timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_tick_provider
    ON external.tick_data (provider);
```

The database role needs, at minimum, permission to connect, set the session parameters used by the app, insert with `COPY`, and drop/create the listed indexes. Use a disposable database or schema owned by a dedicated benchmark role.

## Generate compressed CSV data

The endpoint accepts query-string parameters; it does not accept a JSON body.

```text
POST /test/createcsv?countOfRows=<rows-per-file>&countOfFiles=<number-of-files>
```

PowerShell example:

```powershell
Invoke-WebRequest `
  -Method Post `
  -Uri 'http://localhost:5006/test/createcsv?countOfRows=100000&countOfFiles=4'
```

curl example (use `curl.exe` in Windows PowerShell to avoid its legacy alias):

```powershell
curl.exe -X POST "http://localhost:5006/test/createcsv?countOfRows=100000&countOfFiles=4"
```

`countOfRows` is the number of rows **in each file**, not the overall total:

```text
total generated rows = countOfRows × countOfFiles
```

For example, `100000 × 4` generates 400,000 rows. File generation runs in parallel with a maximum degree of `Environment.ProcessorCount / 2`.

On a successful run, the final artifact should be:

```text
csv_data/all_tick_data_merged.csv.zst
```

The numbered partition CSVs and merged uncompressed CSV are intended to be deleted after compression. Existing files with matching names are overwritten. Do not run two generation requests concurrently because they share the same output names.

> [!IMPORTANT]
> CSV generation is currently blocked on Windows by an undisposed output stream, and the one-file case has a separate filename-pattern defect. The commands above describe the intended API but will not complete reliably until the items in [Known issues and safety notes](#known-issues-and-safety-notes) are fixed.

## Import a `.zst` file into PostgreSQL

The import endpoint also accepts its argument in the query string:

```text
POST /test/insertCsvToPg?pathOfzstdFile=<path>
```

Use an absolute path to avoid ambiguity. PowerShell example:

```powershell
$zstPath = (Resolve-Path '.\csv_data\all_tick_data_merged.csv.zst').Path
$encodedPath = [Uri]::EscapeDataString($zstPath)

Invoke-WebRequest `
  -Method Post `
  -Uri "http://localhost:5006/test/insertCsvToPg?pathOfzstdFile=$encodedPath"
```

curl example:

```powershell
$zstPath = (Resolve-Path '.\csv_data\all_tick_data_merged.csv.zst').Path
curl.exe -X POST --get `
  --data-urlencode "pathOfzstdFile=$zstPath" `
  'http://localhost:5006/test/insertCsvToPg'
```

The request remains open for the entire load. Internally, the importer:

1. Opens the configured PostgreSQL connection.
2. Drops the four expected indexes.
3. Opens a second connection and sets `synchronous_commit = off` and `work_mem = '256MB'` for that session.
4. Decompresses the Zstandard stream and discards its first line as the CSV header.
5. Sends CSV lines through a bounded single-producer/single-consumer channel.
6. Parses each line and writes it using binary `COPY`.
7. Logs producer and consumer rates every 500,000 rows.
8. Attempts an index-rebuild phase.

A successful endpoint returns HTTP `200 OK` with an empty body. A missing file, invalid Zstandard stream, connection error, or unrecoverable `COPY` error returns HTTP 500. Individual malformed data rows are logged and skipped.

### Input-file contract

The importer expects exactly the generated column order shown above and an initial header line. Timestamps must use the round-trip `O`/ISO 8601 representation. Fields must not contain commas or CSV quoting because the importer splits each line directly on commas and does not implement general CSV escaping.

Although the producer reads a `.zst` stream, the endpoint does not enforce a filename extension; the file contents must be Zstandard-compressed.

## Verify a load

Run these checks in PostgreSQL:

```sql
SELECT count(*) AS loaded_rows
FROM external.tick_data;

SELECT symbol, provider, bid, ask, tick_timestamp
FROM external.tick_data
ORDER BY tick_timestamp DESC
LIMIT 10;

SELECT indexname, indexdef
FROM pg_indexes
WHERE schemaname = 'external'
  AND tablename = 'tick_data'
ORDER BY indexname;
```

Until the index-rebuild defect is fixed, recreate the four indexes manually with the SQL in [Prepare PostgreSQL](#prepare-postgresql) after every import.

## Logs and progress

Serilog writes the same application output to:

- the console;
- daily rolling files named `logs/app-YYYYMMDD.log`.

Useful messages include ticker count, per-file generation time, merge/compression time, producer and consumer throughput, committed row count, index phase time, and HTTP duration/status. File logs use local wall-clock timestamps.

For a long-running operation:

```powershell
Get-Content '.\logs\app-*.log' -Wait
```

## Endpoint reference

| Method and route | Query parameters | Success | Side effects |
|---|---|---|---|
| `POST /test/createcsv` | `countOfRows` (required integer), `countOfFiles` (required integer) | `200`, empty body | Creates/overwrites files below `csv_data/`; intended to delete intermediates |
| `POST /test/insertCsvToPg` | `pathOfzstdFile` (required string) | `200`, empty body | Drops indexes and appends rows to `external.tick_data` |

## Troubleshooting

### `tickers.csv not found`

Run the app from the repository root. The expected path is `<working-directory>/Database/tickers.csv`.

### HTTP 500 with “file ... is being used by another process”

This is confirmed current behavior on Windows during the merge step. Stop the app to release the handles, remove partial files if necessary, and fix/dispose the generation streams before retrying.

### `DefaultConnection is missing from configuration`

Set `ConnectionStrings__DefaultConnection` or add `ConnectionStrings:DefaultConnection` to an appropriate local configuration source.

### PostgreSQL authentication failed

The server may be reachable while the checked-in credentials are invalid. Test the actual host, database, role, and password with `psql`, then provide the working connection string through the environment variable. The default `postgres/postgres` combination is not guaranteed to exist.

### `relation "external.tick_data" does not exist`

Create the schema and table using [Prepare PostgreSQL](#prepare-postgresql), and confirm the connection string points to the database where they were created.

### Import appears to hang

The endpoint is synchronous from the caller's perspective and can take a long time. Watch the console or rolling log for producer/consumer progress. Check disk throughput, database activity, locks, and available memory. The database commands have no timeout.

### No final `.zst` file appears

Check the logs for a generation or merge exception. Also confirm `countOfFiles` is at least 2 under the current filename behavior and that the process can write to `csv_data/`. See the first two known issues.

### HTTP redirects or HTTPS warnings

`UseHttpsRedirection` is enabled. With only the HTTP profile running, ASP.NET Core may log that it cannot determine an HTTPS port and continue serving HTTP. Use the HTTPS profile and trusted development certificate if HTTPS is required.

## Project layout

```text
Controllers/TestController.cs           HTTP endpoints
Database/Datastore.cs                   generation, merge, compression, and import pipeline
Database/tickers.csv                    source universe of ticker symbols
Interface/IDataStore.cs                 datastore contract
Middleware/RequestLoggingMiddleware.cs  request timing and status logs
Model/FastTickGenerator.cs              synthetic row generator
Model/TickData.cs                       tick record shape
Program.cs                              dependency injection, CORS, logging, OpenAPI, middleware
Properties/launchSettings.json          local HTTP/HTTPS profiles
appsettings.json                        logging and default connection string
```
