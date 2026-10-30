# High-Speed Market Data Pipeline POC

Generate millions of realistic market ticks, compress them, and stream them into PostgreSQL through one lightweight API.

> **The idea:** prove that large market-data workloads can move from generation to database quickly, with low memory pressure and visible throughput.

## At a glance

| | |
|---|---|
| **Input** | Synthetic market ticks based on 1,000 symbols |
| **Output** | Zstandard-compressed CSV (`.zst`) |
| **Destination** | PostgreSQL |
| **API** | ASP.NET Core on .NET 10 |
| **Load method** | PostgreSQL binary `COPY` |
| **Progress** | Live row count and rows/second in the console |

## What it does

1. **Creates realistic tick data**
   - Symbol, provider, bid, ask, last price and volume
   - UTC market and audit timestamps
   - Three sample providers: Bloomberg, Reuters and Fenics

2. **Generates files in parallel**
   - Splits work across multiple files
   - Uses multiple CPU cores
   - Merges the files into one dataset

3. **Compresses the result**
   - Uses Zstandard for fast, efficient compression
   - Produces one portable `.csv.zst` file
   - Removes temporary uncompressed files

4. **Streams directly into PostgreSQL**
   - Decompresses while reading
   - Does not need a second decompressed import file
   - Uses PostgreSQL binary `COPY` instead of individual `INSERT` statements
   - Runs file reading and database writing as a producer/consumer pipeline

```text
Generate in parallel -> Merge -> Zstandard -> Stream/decompress -> Binary COPY -> PostgreSQL
```

## How fast is it?

The POC is designed for **multi-million-row throughput**. Its fast path combines:

- parallel data generation;
- 8 MB sequential file buffers;
- multi-worker Zstandard compression;
- streaming decompression;
- a bounded 200,000-row pipeline between disk and PostgreSQL;
- PostgreSQL binary `COPY`;
- reduced commit overhead during the load.

The API prints the real producer and database-consumer rate every 500,000 rows:

```text
Producer: 1,000,000 rows - <measured rate> rows/sec
Consumer: 1,000,000 rows - <measured rate> rows/sec
Consumer done — 4,000,000 rows committed
Decompress + insert complete — 4000000 rows in <elapsed time>
```

The values in angle brackets are placeholders, not claimed results. Actual speed depends on CPU, storage, PostgreSQL configuration, indexes, network latency and row count. This repository does not yet contain a verified end-to-end reference benchmark.

For a useful client demo, generate **4 million rows**:

```text
1,000,000 rows per file x 4 files = 4,000,000 rows
```

Then show the live `rows/sec` output and final elapsed time from the same machine. This makes the performance claim measurable and repeatable instead of relying on a theoretical number.

## Run it locally

### 1. Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- PostgreSQL
- PowerShell

Check .NET:

```powershell
dotnet --version
```

### 2. Create the PostgreSQL table

Run this against a disposable local database:

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

### 3. Configure the connection

From PowerShell in the project directory:

```powershell
$env:ConnectionStrings__DefaultConnection = 'Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=your-password'
```

### 4. Start the API

```powershell
dotnet restore
dotnet run --launch-profile http
```

The API starts at:

```text
http://localhost:5006
```

Interactive API documentation is available at:

```text
http://localhost:5006/scalar/v1
```

### 5. Generate 4 million rows

Open a second PowerShell terminal in the project directory:

```powershell
Invoke-WebRequest `
  -Method Post `
  -Uri 'http://localhost:5006/test/createcsv?countOfRows=1000000&countOfFiles=4'
```

`countOfRows` means rows **per file**:

```text
total rows = countOfRows x countOfFiles
```

Expected output:

```text
csv_data/all_tick_data_merged.csv.zst
```

### 6. Load the file into PostgreSQL

```powershell
$file = (Resolve-Path '.\csv_data\all_tick_data_merged.csv.zst').Path
$encodedFile = [Uri]::EscapeDataString($file)

Invoke-WebRequest `
  -Method Post `
  -Uri "http://localhost:5006/test/insertCsvToPg?pathOfzstdFile=$encodedFile"
```

Keep the API terminal visible. It reports live production rate, consumption rate, committed rows and total elapsed time.

### 7. Confirm the result

```sql
SELECT count(*) AS loaded_rows
FROM external.tick_data;
```

For the example above, the expected result is:

```text
4,000,000
```

## Two API calls

| Action | Endpoint |
|---|---|
| Generate and compress data | `POST /test/createcsv?countOfRows=1000000&countOfFiles=4` |
| Stream it into PostgreSQL | `POST /test/insertCsvToPg?pathOfzstdFile=<absolute-path>` |

On successful completion, both calls return `200 OK`. The request stays open while the operation runs.

## Why this POC matters

- **Simple:** two API calls from generated data to loaded database.
- **Fast by design:** parallel generation, streaming and binary ingestion.
- **Memory-aware:** data moves through a bounded pipeline instead of loading the full dataset into RAM.
- **Storage-aware:** Zstandard reduces the size of data at rest and in transit.
- **Observable:** throughput and elapsed time are visible during the run.
- **Scalable foundation:** generation, compression and ingestion can be tuned independently for the target environment.

## Before a client demo

This is intentionally a POC. The current branch has two implementation items that must be corrected before using the commands above in a Windows demo:

1. The generated file stream must be disposed before the merge begins; otherwise Windows returns a file-lock error.
2. The index-rebuild loop must execute its `CREATE INDEX` statements. It currently drops the expected indexes but does not recreate them.

Use only a disposable PostgreSQL schema. The import appends rows and temporarily drops indexes; it does not replace existing data. Do not expose the API publicly because the POC has no authentication.

## Tech stack

- .NET 10 / ASP.NET Core
- PostgreSQL / Npgsql binary `COPY`
- Zstandard compression
- ZString buffered CSV generation
- Bounded asynchronous channels
- Serilog performance logging

Logs are written to the console and to `logs/app-YYYYMMDD.log`.
