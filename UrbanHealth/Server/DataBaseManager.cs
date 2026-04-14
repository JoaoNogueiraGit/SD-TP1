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

        public async Task<string> GetStatisticsJsonAsync(string zone = null, string dataType = null, string sensor = null) {
            var stats = new Dictionary<string, object>();
            var readings = new List<double>();
            var timestamps = new List<string>();

            try {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var selectCmd = connection.CreateCommand();

                string whereClause = "";
                if (!string.IsNullOrEmpty(zone) && zone != "ALL") {
                    whereClause += $" WHERE Zone = '{zone.Replace("'", "''")}'";
                }
                if (!string.IsNullOrEmpty(dataType) && dataType != "ALL") {
                    if (whereClause.Length > 0) whereClause += " AND";
                    else whereClause += " WHERE";
                    whereClause += $" Datatype = '{dataType.Replace("'", "''")}'";
                }
                if (!string.IsNullOrEmpty(sensor) && sensor != "ALL") {
                    if (whereClause.Length > 0) whereClause += " AND";
                    else whereClause += " WHERE";
                    whereClause += $" SID = '{sensor.Replace("'", "''")}'";
                }

                selectCmd.CommandText = @$"
                    SELECT Timestamp, Value 
                    FROM Readings 
                    {whereClause}
                    ORDER BY Timestamp DESC
                    LIMIT 1000";

                using var reader = await selectCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) {
                    timestamps.Add(reader.GetString(0));
                    readings.Add(reader.GetDouble(1));
                }

                if (readings.Count == 0) {
                    stats["error"] = "No data available for the selected filters";
                    return JsonSerializer.Serialize(stats);
                }

                // Calculate statistics
                double mean = readings.Average();
                double variance = readings.Sum(x => Math.Pow(x - mean, 2)) / readings.Count;
                double stdDev = Math.Sqrt(variance);
                double min = readings.Min();
                double max = readings.Max();
                double median = GetMedian(readings);

                stats["count"] = readings.Count;
                stats["mean"] = Math.Round(mean, 2);
                stats["median"] = Math.Round(median, 2);
                stats["stdDev"] = Math.Round(stdDev, 2);
                stats["min"] = Math.Round(min, 2);
                stats["max"] = Math.Round(max, 2);
                stats["zone"] = zone ?? "ALL";
                stats["dataType"] = dataType ?? "ALL";
                stats["values"] = readings.Select(v => Math.Round(v, 2)).ToList();
                stats["timestamps"] = timestamps;
            } catch (Exception ex) {
                Console.WriteLine($"[DB ERROR] Failed to calculate statistics: {ex.Message}");
                stats["error"] = $"Error calculating statistics: {ex.Message}";
            }

            return JsonSerializer.Serialize(stats);
        }

        public async Task<string> GetAvailableZonesJsonAsync() {
            var zones = new HashSet<string>();

            try {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var selectCmd = connection.CreateCommand();
                selectCmd.CommandText = "SELECT DISTINCT Zone FROM Readings ORDER BY Zone";

                using var reader = await selectCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) {
                    zones.Add(reader.GetString(0));
                }
            } catch (Exception ex) {
                Console.WriteLine($"[DB ERROR] Failed to get zones: {ex.Message}");
            }

            return JsonSerializer.Serialize(zones.OrderBy(z => z).ToList());
        }

        public async Task<string> GetAvailableTypesJsonAsync() {
            var types = new HashSet<string>();

            try {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var selectCmd = connection.CreateCommand();
                selectCmd.CommandText = "SELECT DISTINCT Datatype FROM Readings ORDER BY Datatype";

                using var reader = await selectCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) {
                    types.Add(reader.GetString(0));
                }
            } catch (Exception ex) {
                Console.WriteLine($"[DB ERROR] Failed to get types: {ex.Message}");
            }

            return JsonSerializer.Serialize(types.OrderBy(t => t).ToList());
        }

        public async Task<string> GetAvailableSensorsJsonAsync(string zone = null, string dataType = null)
        {
            var sensors = new HashSet<string>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var selectCmd = connection.CreateCommand();

                string whereClause = "";
                if (!string.IsNullOrEmpty(zone) && zone != "ALL")
                {
                    whereClause += $" WHERE Zone = '{zone.Replace("'", "''")}'";
                }
                if (!string.IsNullOrEmpty(dataType) && dataType != "ALL")
                {
                    if (whereClause.Length > 0) whereClause += " AND";
                    else whereClause += " WHERE";
                    whereClause += $" Datatype = '{dataType.Replace("'", "''")}'";
                }

                selectCmd.CommandText = $@"
                    SELECT DISTINCT SID 
                    FROM Readings 
                    {whereClause}
                    ORDER BY SID";

                using var reader = await selectCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    sensors.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB ERROR] Failed to get sensors: {ex.Message}");
            }

            return JsonSerializer.Serialize(sensors.OrderBy(s => s).ToList());
        }

        private double GetMedian(List<double> values) {
            var sorted = values.OrderBy(x => x).ToList();
            int count = sorted.Count;
            if (count % 2 == 0) {
                return (sorted[count / 2 - 1] + sorted[count / 2]) / 2.0;
            } else {
                return sorted[count / 2];
            }
        }
    }
}