using Microsoft.Data.Sqlite;
using Shared;
using System.Data;
using System.Text.Json;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;

namespace Server {
    public class DataBaseManager {
        private readonly string _connectionString = "Data Source=onehealth_urbano.db";

        private static readonly SemaphoreSlim _dbLock = new SemaphoreSlim(1, 1);

        public DataBaseManager() {
            InitializeDatabase();
        }

        private void InitializeDatabase() {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            // Activate WAL mode (Allows Readings and Writes simultaneously)
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

            // Create INDEXES 
            // Fast search dictionaries, invisible in DB
            var indexCmd = connection.CreateCommand();
            indexCmd.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_timestamp ON Readings(Timestamp DESC);
                CREATE INDEX IF NOT EXISTS idx_zone ON Readings(Zone);
                CREATE INDEX IF NOT EXISTS idx_sensor ON Readings(SID);
            ";
            indexCmd.ExecuteNonQuery();

            Console.WriteLine("[DB] Database SQLite initialized with Single Table, Indexes, and WAL mode.");
        }

        public async Task SaveReadingsAsync(Message msg) {
            await _dbLock.WaitAsync();

            try {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var insertCmd = connection.CreateCommand();

                insertCmd.CommandText = @"
                    INSERT INTO Readings (Timestamp, GID, SID, Zone, Datatype, Value) 
                    VALUES ($ts, $gid, $sid, $zone, $type, $val)";

                insertCmd.Parameters.AddWithValue("$ts", msg.Timestamp);
                insertCmd.Parameters.AddWithValue("$gid", msg.GID);
                insertCmd.Parameters.AddWithValue("$sid", msg.SID);
                insertCmd.Parameters.AddWithValue("$zone", msg.Data.GetValueOrDefault("ZONE", "UNKNOWN"));
                insertCmd.Parameters.AddWithValue("$type", msg.Data.GetValueOrDefault("TYPE", "UNKNOWN"));

                double val = 0.0;
                double.TryParse(msg.Data.GetValueOrDefault("VALUE", "0"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out val);
                insertCmd.Parameters.AddWithValue("$val", val);

                await insertCmd.ExecuteNonQueryAsync();
                // Console.WriteLine($"[DB] Reading from {msg.SID} stored in DB.");
            } catch (Exception ex) {
                Console.WriteLine($"[DB ERROR] Reading not stored: {ex.Message}");
            } finally {
                _dbLock.Release();
            }
        }

        public async Task<string> GetRecentReadingsJsonAsync(int limit = 50) {
            var readings = new List<Dictionary<string, string>>();

            // Nota: Não bloqueamos com _dbLock aqui! O WAL mode permite ler sem bloquear as escritas.
            // Dont use _dbLock, WAL allows read without blocking writes
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