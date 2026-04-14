using Microsoft.Data.Sqlite;
using Shared;
using System.Data;
using System.Text.Json;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace Server {
    public class DataBaseManager {
        private readonly string _connectionString;

        // Unbounded in-memory channel to receive messages instantly
        private readonly Channel<Message> _messageChannel = Channel.CreateUnbounded<Message>();

        public DataBaseManager() {

            string dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "onehealth_urbano.db";
            _connectionString = $"Data Source={dbPath}";

            InitializeDatabase();

            // Start the background database worker
            _ = Task.Run(ProcessDbQueueAsync);
        }

        private void InitializeDatabase() {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            // Activate WAL mode (Allows concurrent reads and writes)
            var pragmaCmd = connection.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA journal_mode=WAL;";
            pragmaCmd.ExecuteNonQuery();

            var tableCmd = connection.CreateCommand();
            tableCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Readings (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp  DATETIME NOT NULL,
                    GID        TEXT     NOT NULL,
                    SID        TEXT     NOT NULL,
                    Zone       TEXT     NOT NULL,
                    Datatype   TEXT     NOT NULL,
                    Value      REAL     NOT NULL
                );";
            tableCmd.ExecuteNonQuery();

            // Create indexes for faster querying
            var indexCmd = connection.CreateCommand();
            indexCmd.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_timestamp ON Readings(Timestamp DESC);
                CREATE INDEX IF NOT EXISTS idx_zone ON Readings(Zone);
                CREATE INDEX IF NOT EXISTS idx_sensor ON Readings(SID);
            ";
            indexCmd.ExecuteNonQuery();

            Console.WriteLine("[DB] Database SQLite initialized with Single Table, Indexes, WAL mode and Bulk-Insert Channel.");
        }

        // Receives messages and writes them to the channel
        public async Task SaveReadingsAsync(Message msg) {
            // Non-blocking write to the channel
            await _messageChannel.Writer.WriteAsync(msg);
        }

        // Reads from the channel and performs Bulk Inserts
        private async Task ProcessDbQueueAsync() {
            while (true) {
                try {
                    // Wait asynchronously until data is available to read
                    await _messageChannel.Reader.WaitToReadAsync();

                    using var connection = new SqliteConnection(_connectionString);
                    await connection.OpenAsync();

                    // Begin a database transaction for bulk insertion performance
                    using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

                    var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = transaction;
                    insertCmd.CommandText = @"
                        INSERT INTO Readings (Timestamp, GID, SID, Zone, Datatype, Value) 
                        VALUES ($ts, $gid, $sid, $zone, $type, $val)";

                    // Prepare parameters outside the loop to minimize memory allocation
                    var pTs = insertCmd.Parameters.Add("$ts", SqliteType.Text);
                    var pGid = insertCmd.Parameters.Add("$gid", SqliteType.Text);
                    var pSid = insertCmd.Parameters.Add("$sid", SqliteType.Text);
                    var pZone = insertCmd.Parameters.Add("$zone", SqliteType.Text);
                    var pType = insertCmd.Parameters.Add("$type", SqliteType.Text);
                    var pVal = insertCmd.Parameters.Add("$val", SqliteType.Real);

                    int count = 0;

                    // Read messages from the channel until empty or until reaching the batch limit (1000)
                    while (count < 1000 && _messageChannel.Reader.TryRead(out var msg)) {
                        pTs.Value = msg.Timestamp;
                        pGid.Value = msg.GID;
                        pSid.Value = msg.SID;
                        pZone.Value = msg.Data.GetValueOrDefault("ZONE", "UNKNOWN");
                        pType.Value = msg.Data.GetValueOrDefault("TYPE", "UNKNOWN");

                        double val = 0.0;
                        double.TryParse(msg.Data.GetValueOrDefault("VALUE", "0"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out val);
                        pVal.Value = val;

                        await insertCmd.ExecuteNonQueryAsync();
                        count++;
                    }

                    // Commit the transaction to save all batched messages to disk
                    await transaction.CommitAsync();

                } catch (Exception ex) {
                    Console.WriteLine($"[DB FATAL ERROR] Bulk insert failed: {ex.Message}");
                    // Delay on disk failure to prevent console spamming
                    await Task.Delay(2000);
                }
            }
        }

        // Lock-free database reading
        public async Task<string> GetRecentReadingsJsonAsync(int limit = 50) {
            var readings = new List<Dictionary<string, string>>();

            try {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var selectCmd = connection.CreateCommand();
                selectCmd.CommandText = @"
                    SELECT Timestamp, SID, Zone, Datatype, Value, GID 
                    FROM Readings 
                    ORDER BY Timestamp DESC 
                    LIMIT $limit";

                selectCmd.Parameters.AddWithValue("$limit", limit);

                using var reader = await selectCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) {
                    var row = new Dictionary<string, string>
                    {
                        { "Time", reader.GetString(0) },
                        { "Sensor", reader.GetString(1) },
                        { "Zone", reader.GetString(2) },
                        { "Type", reader.GetString(3) },
                        { "Value", reader.GetDouble(4).ToString("0.0") },
                        { "Gateway", reader.GetString(5) }
                    };
                    readings.Add(row);
                }
            } catch (Exception ex) {
                Console.WriteLine($"[DB ERROR] Failed to read data: {ex.Message}");
            }

            return JsonSerializer.Serialize(readings);
        }
    }
}