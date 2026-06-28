using nietras.SeparatedValues;
using Npgsql;
using Npgsql.Internal;
using NpgsqlTypes;
using pgtest.Interface;
using pgtest.Model;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using ZstdSharp;
using ZstdSharp.Unsafe;
using Range = System.Range;

namespace pgtest.Database
{
    public class Datastore : IDataStore
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<Datastore> _logger;
        private const string _outputFolder = "csv_data";
        private const string _mergedFileName = "all_tick_data_merged.csv";
        private const int _flushThresholdBytes = 8 * 1024 * 1024; // 8MB

        public Datastore(IConfiguration configuration, ILogger<Datastore> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }


        public async Task InsertIntoPg3(string pathOfzstdFile)
        {
            var sp = Stopwatch.StartNew();

            var connectionString = _configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("DefaultConnection is missing from configuration.");

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            // ─────────────────────────────────────────────────────────────
            // SETUP — truncate, unlog, disable autovacuum, drop indexes.
            // Order matters: TRUNCATE first so SET UNLOGGED is instant on an
            // empty table (no full table rewrite). CommandTimeout = 0 because
            // these DDL operations can run long.
            // ─────────────────────────────────────────────────────────────
            _logger.LogInformation("Preparing table for bulk load...");
            await using var cmd = new NpgsqlCommand(@"
                DROP INDEX IF EXISTS external.idx_tick_timestamp;
                DROP INDEX IF EXISTS external.idx_tick_symbol;
                DROP INDEX IF EXISTS external.idx_tick_symbol_ts;
                DROP INDEX IF EXISTS external.idx_tick_provider;
            ", conn) { CommandTimeout = 0 };
            await cmd.ExecuteNonQueryAsync();
            _logger.LogInformation("Ready to load.");

            const string copy =
                "COPY external.tick_data (symbol, provider, bid, ask, last_price, volume, tick_timestamp, last_updated_by, last_updated_on_utc) FROM STDIN (FORMAT BINARY)";

            // 30k belt: big enough to keep the consumer fed, small enough that
            // objects die young in Gen 0 (100k caused Gen 2 GC pauses).
            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(200_000)
            {
                SingleWriter = true,
                SingleReader = true,
            });

            // ─────────────────────────────────────────────────────────────
            // PRODUCER — read .zst, decompress, push raw CSV lines onto the belt.
            // No parsing here: the line string is the only allocation per row.
            // ─────────────────────────────────────────────────────────────
            var producer = Task.Run(async () =>
            {
                try
                {
                    await using var fsIn = File.OpenRead(pathOfzstdFile);
                    await using var decompressionStream = new DecompressionStream(fsIn);
                    using var reader = new StreamReader(decompressionStream);

                    await reader.ReadLineAsync(); // skip header

                    var producerCount = 0;
                    var producerSw = Stopwatch.StartNew();

                    string? line;
                    while ((line = await reader.ReadLineAsync()) is not null)
                    {
                        if (!channel.Writer.TryWrite(line))
                            await channel.Writer.WriteAsync(line);

                        producerCount++;

                        if (producerCount % 500_000 == 0)
                        {
                            var rate = producerCount / producerSw.Elapsed.TotalSeconds;
                            _logger.LogInformation(
                                "Producer: {Count:N0} rows — {Rate:N0} rows/sec",
                                producerCount, (int)rate);
                        }
                    }

                    _logger.LogInformation("Producer done — {Count:N0} rows total", producerCount);
                    channel.Writer.Complete();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Producer failed");
                    channel.Writer.Complete(ex);
                }
            });

            // ─────────────────────────────────────────────────────────────
            // CONSUMER — pull lines off the belt, parse with Span, write to PG.
            // Span values are extracted to locals BEFORE any await (Span can't
            // cross an await). Field Write() calls are sync (just buffer writes);
            // StartRowAsync / CompleteAsync are async (they touch the network).
            // ─────────────────────────────────────────────────────────────
            var consumer = Task.Run(async () =>
            {
                try
                {
                    await using var consumerConn = new NpgsqlConnection(connectionString);
                    await consumerConn.OpenAsync();

                    await using var sessionCmd = new NpgsqlCommand(@"
                        SET synchronous_commit = off;
                        SET work_mem = '256MB';
                    ", consumerConn) { CommandTimeout = 0 };
                    await sessionCmd.ExecuteNonQueryAsync();

                    await using var writer = await consumerConn.BeginBinaryImportAsync(copy);

                    var count = 0L;
                    var consumerSw = Stopwatch.StartNew();
                    var ranges = new Range[10]; // reused every row — one allocation total

                    while (await channel.Reader.WaitToReadAsync())
                    {
                        while (channel.Reader.TryRead(out var line))
                        {
                            ReadOnlySpan<char> lineSpan = line.AsSpan();
                            int partsCount = lineSpan.Split(ranges, ',');
                            if (partsCount < 9) continue;

                            try
                            {
                                // extract ALL values from the span FIRST
                                var symbol = lineSpan[ranges[0]].ToString();
                                var provider = lineSpan[ranges[1]].ToString();
                                var bid = double.Parse(lineSpan[ranges[2]]);
                                var ask = double.Parse(lineSpan[ranges[3]]);
                                var lastPrice = ranges[4].Start.Value == ranges[4].End.Value
                                    ? (double?)null : double.Parse(lineSpan[ranges[4]]);
                                var volume = ranges[5].Start.Value == ranges[5].End.Value
                                    ? (double?)null : double.Parse(lineSpan[ranges[5]]);
                                var tickTimestamp = DateTime.ParseExact(
                                    lineSpan[ranges[6]], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                                var lastUpdatedBy = lineSpan[ranges[7]].ToString();
                                var lastUpdatedOnUtc = DateTime.ParseExact(
                                    lineSpan[ranges[8]], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

                                writer.StartRow();
                                writer.Write(symbol, NpgsqlDbType.Text);
                                writer.Write(provider, NpgsqlDbType.Text);
                                writer.Write(bid, NpgsqlDbType.Double);
                                writer.Write(ask, NpgsqlDbType.Double);

                                if (lastPrice.HasValue) writer.Write(lastPrice.Value, NpgsqlDbType.Double);
                                else writer.WriteNull();

                                if (volume.HasValue) writer.Write(volume.Value, NpgsqlDbType.Double);
                                else writer.WriteNull();

                                writer.Write(tickTimestamp);
                                writer.Write(lastUpdatedBy, NpgsqlDbType.Text);
                                writer.Write(lastUpdatedOnUtc);

                                count++;

                                if (count % 500_000 == 0)
                                {
                                    var rate = count / consumerSw.Elapsed.TotalSeconds;
                                    _logger.LogInformation("Consumer: {Count:N0} rows — {Rate:N0} rows/sec",
                                        count, (int)rate);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Skipping malformed row");
                            }
                        }
                    }

                    var totalRows = await writer.CompleteAsync();
                    _logger.LogInformation("Consumer done — {Count:N0} rows committed", totalRows);
                    return totalRows;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Consumer failed");
                    throw;
                }
            });

            // ─────────────────────────────────────────────────────────────
            // COORDINATION + RESTORE — try/finally guarantees the table is
            // restored and indexes rebuilt even if the load throws partway.
            // ─────────────────────────────────────────────────────────────
            try
            {
                await Task.WhenAll(producer, consumer);
                var totalRows = (long)await consumer;
                _logger.LogInformation(
                    "Decompress + insert complete — {Rows} rows in {Elapsed}",
                    totalRows, sp.Elapsed);
            }
            finally
            {
                _logger.LogInformation("Restoring table state and rebuilding indexes...");
                var indexSw = Stopwatch.StartNew();

                try
                {
                    // 2. Parallel index rebuild — each on its own connection, each with
                    //    its own maintenance_work_mem, no timeout.
                    var indexSqlStatements = new[]
                    {
                        "CREATE INDEX IF NOT EXISTS idx_tick_timestamp ON external.tick_data USING btree (tick_timestamp DESC)",
                        "CREATE INDEX IF NOT EXISTS idx_tick_symbol ON external.tick_data USING btree (symbol)",
                        "CREATE INDEX IF NOT EXISTS idx_tick_symbol_ts ON external.tick_data USING btree (symbol, tick_timestamp DESC)",
                        "CREATE INDEX IF NOT EXISTS idx_tick_provider ON external.tick_data USING btree (provider)"
                    };

                    var indexTasks = indexSqlStatements.Select(async sql =>
                    {
                        await using var indexConn = new NpgsqlConnection(connectionString);
                        await indexConn.OpenAsync();
                        // SET + CREATE in one round-trip
                        await using var indexCmd = new NpgsqlCommand(@"
                            SET maintenance_work_mem = '2GB';
                        ", indexConn) { CommandTimeout = 0 };
                        await indexCmd.ExecuteNonQueryAsync();
                    }).ToArray();

                    await Task.WhenAll(indexTasks);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed during table restore or index rebuild.");
                }
                finally
                {
                    indexSw.Stop();
                    _logger.LogInformation("Rebuild phase done in {Elapsed}", indexSw.Elapsed);
                    sp.Stop();
                }
            }
        }


        public async Task CreateCsvAsync(int count, int fileCount = 1)
        {
            Directory.CreateDirectory(_outputFolder);
            var tickers = LoadTickers();

            // usually its a good idea to match the no. of physical cores rather than logical cores
            var cpuThreadsToAllocate = Environment.ProcessorCount / 2;

            await Parallel.ForEachAsync(
                Enumerable.Range(0, fileCount),
                new ParallelOptions { MaxDegreeOfParallelism = cpuThreadsToAllocate },
                async (fileIndex, _) =>
                {
                    await GenerateSingleFileAsync(tickers, fileIndex, fileCount, count);
                });

            await MergeFilesAsync(_outputFolder);
            CompressMergedFile(_outputFolder);
        }

        private async Task GenerateSingleFileAsync(string[] tickers, int fileIndex, int fileCount, int count)
        {
            var fileName = fileCount == 1 ? "tick_data.csv" : $"tick_data_{fileIndex + 1}.csv";
            var filePath = Path.Combine(_outputFolder, fileName);
            var sp = Stopwatch.StartNew();

            var fileStream = new FileStream(
                filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 8 * 1024 * 1024,
                FileOptions.SequentialScan);

            // write header line as UTF8 bytes directly to stream
            await fileStream.WriteAsync(
                "Symbol,Provider,Bid,Ask,LastPrice,Volume,TickTimestamp,LastUpdatedBy,LastUpdatedOnUtc\n"u8.ToArray());

            using (var zsb = Cysharp.Text.ZString.CreateUtf8StringBuilder())
            {
                for (int i = 0; i < count; i++)
                {
                    var tick = FastTickGenerator.Generate(tickers);

                    zsb.Append(tick.Symbol); zsb.Append(',');
                    zsb.Append(tick.Provider); zsb.Append(',');
                    zsb.Append(tick.Bid); zsb.Append(',');
                    zsb.Append(tick.Ask); zsb.Append(',');
                    zsb.Append(tick.LastPrice); zsb.Append(',');
                    zsb.Append(tick.Volume); zsb.Append(',');
                    zsb.Append(tick.TickTimestamp.ToString("O")); zsb.Append(',');
                    zsb.Append(tick.LastUpdatedBy); zsb.Append(',');
                    zsb.AppendLine(tick.LastUpdatedOnUtc.ToString("O"));

                    if (zsb.Length >= _flushThresholdBytes)
                    {
                        // writes UTF8 bytes directly to stream — no intermediate string
                        await zsb.WriteToAsync(fileStream);
                        zsb.Clear();
                    }

                    if (zsb.Length > 0)
                    {
                        await zsb.WriteToAsync(fileStream);
                        zsb.Clear();
                    }
                }
            }

            sp.Stop();
            _logger.LogInformation("Generated {FilePath} ({RowCount} rows) in {ElapsedMilliseconds}ms",
                filePath, count, sp.ElapsedMilliseconds);
        }

        private async Task MergeFilesAsync(string splitFilesFolder)
        {
            _logger.LogInformation("Starting merge...");
            var sp = Stopwatch.StartNew();

            var csvFiles = Directory.GetFiles(splitFilesFolder, "tick_data_*.csv")
                                    .OrderBy(f => f)
                                    .ToArray();

            if (csvFiles.Length == 0)
            {
                _logger.LogWarning("No CSV files found in {OutputFolder}", splitFilesFolder);
                return;
            }

            var mergedFilePath = Path.Combine(splitFilesFolder, _mergedFileName);

            await using var writeStream = new FileStream(
                mergedFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: _flushThresholdBytes,
                FileOptions.SequentialScan);

            bool headerWritten = false;
            var sp1 = Stopwatch.StartNew();

            foreach (var csvFile in csvFiles)
            {
                await using var readStream = new FileStream(
                    csvFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: _flushThresholdBytes,
                    FileOptions.SequentialScan);

                if (!headerWritten)
                {
                    // copy entire file including header
                    await readStream.CopyToAsync(writeStream, 8 * 1024 * 1024);
                    headerWritten = true;
                }
                else
                {
                    // skip the header line — find end of first line
                    var headerSkipBuf = new byte[512];
                    int bytesRead = 0;
                    int newlinePos = -1;

                    while (newlinePos == -1)
                    {
                        int read = await readStream.ReadAsync(headerSkipBuf.AsMemory(bytesRead, 1));
                        if (read == 0) break;
                        if (headerSkipBuf[bytesRead] == '\n')
                            newlinePos = bytesRead;
                        bytesRead++;
                    }

                    // copy the rest of the file (no header)
                    await readStream.CopyToAsync(writeStream, 8 * 1024 * 1024);
                }
            }

            sp1.Stop();
            _logger.LogInformation(
                "Merged {FileCount} files in {ElapsedMilliseconds}ms",
                csvFiles.Length, sp1.ElapsedMilliseconds);
            sp1.Restart();

            // delete split files
            foreach (var csvFile in csvFiles)
            {
                File.Delete(csvFile);
                _logger.LogInformation("Deleted {CsvFile}", csvFile);
            }

            sp1.Stop();
            _logger.LogInformation(
                "Deleted {FileCount} files in {ElapsedMilliseconds}ms",
                csvFiles.Length, sp1.ElapsedMilliseconds);

            sp.Stop();
            _logger.LogInformation(
                "MergeFilesAsync complete in {ElapsedMilliseconds}ms total",
                sp.ElapsedMilliseconds);
        }

        // using zstandard into a .zst file
        private void CompressMergedFile(string splitFilesFolder)
        {
            _logger.LogInformation("Compressing merged file...");
            var sp = Stopwatch.StartNew();

            var mergedFilePath = Path.Combine(splitFilesFolder, "all_tick_data_merged.csv");
            var zstFilePath = Path.Combine(splitFilesFolder, "all_tick_data_merged.csv.zst");

            if (!File.Exists(mergedFilePath))
            {
                _logger.LogWarning("Merged file not found at {MergedFilePath}", mergedFilePath);
                return;
            }

            if (File.Exists(zstFilePath))
            {
                File.Delete(zstFilePath);
                _logger.LogInformation("Deleted existing zst file");
            }

            using (var fsIn = File.OpenRead(mergedFilePath))                  // read CSV
            using (var fsOut = File.Create(zstFilePath))                             // write .zst
            using (var compressionStream = new CompressionStream(fsOut, level: 6))  // zstd squisher
            {
                compressionStream.SetParameter(
                    ZSTD_cParameter.ZSTD_c_nbWorkers,
                    Environment.ProcessorCount / 2);   // use half of available cores (physical cores)
                fsIn.CopyTo(compressionStream);   // chunked streaming, same as before
            }

            File.Delete(mergedFilePath);
            sp.Stop();
            _logger.LogInformation(
                "Compression complete — saved to {ZstFilePath} in {ElapsedMilliseconds}ms",
                zstFilePath, sp.ElapsedMilliseconds);
        }

        private string[] LoadTickers()
        {
            var sp = Stopwatch.StartNew();
            var tickersPath = Path.Combine(Directory.GetCurrentDirectory(), "Database", "tickers.csv");

            if (!File.Exists(tickersPath))
            {
                _logger.LogError("tickers.csv not found at {TickersPath}", tickersPath);
                throw new FileNotFoundException("tickers.csv not found", tickersPath);
            }

            var tickers = File.ReadAllLines(tickersPath)
                .Skip(1)                                         // skip header
                .Select(line => line.Split(',')[0])     // grab ticker column
                .Where(t => !string.IsNullOrWhiteSpace(t)) // skip empty lines
                .ToArray();

            sp.Stop();
            _logger.LogInformation(
                "Loaded {TickerCount} tickers from {TickersPath} in {ElapsedMilliseconds}ms",
                tickers.Length, tickersPath, sp.ElapsedMilliseconds);

            return tickers;
        }
    }
}