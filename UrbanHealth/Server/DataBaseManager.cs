using Microsoft.Data.Sqlite;
using Shared;
using System.Data;
using System.Text.Json;
using System.Collections.Generic;

namespace Server
{
    public class DataBaseManager
    {
        private readonly string _connectionString = "Data Source=onehealth_urbano.db";

        private static readonly SemaphoreSlim _dbLock = new SemaphoreSlim(1, 1);

        public DataBaseManager()
        {
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var tableCmd = connection.CreateCommand();
            tableCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Readings (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp DATETIME NOT NULL,
                    GID TEXT NOT NULL,
                    SID TEXT NOT NULL,
                    Zone TEXT,
                    DataType TEXT NOT NULL,
                    Value REAL NOT NULL
                );";
            tableCmd.ExecuteNonQuery();
            Console.WriteLine("[DB] Database SQLite initialized successfully.");
        }

        public async Task SaveReadingsAsync(Message msg)
        {
            await _dbLock.WaitAsync();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = @"
                    INSERT INTO Readings (Timestamp, GID, SID, Zone, DataType, Value) 
                    VALUES ($ts, $gid, $sid, $zone, $type, $val)";

                // Parametros para evitar SQL injection ou erros de formatação
                insertCmd.Parameters.AddWithValue("$ts", msg.Timestamp);
                insertCmd.Parameters.AddWithValue("$gid", msg.GID);
                insertCmd.Parameters.AddWithValue("$sid", msg.SID);
                insertCmd.Parameters.AddWithValue("$zone", msg.Data.GetValueOrDefault("ZONE", "N/A"));
                insertCmd.Parameters.AddWithValue("$type", msg.Data.GetValueOrDefault("TYPE", "UNKNOWN"));

                // Converter valor para double
                if (double.TryParse(msg.Data.GetValueOrDefault("VALUE", "0"), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
                {
                    insertCmd.Parameters.AddWithValue("$val", val);
                }
                else
                {
                    insertCmd.Parameters.AddWithValue("$val", 0.0);
                }

                await insertCmd.ExecuteNonQueryAsync();
                Console.WriteLine($"[DB] Reading from {msg.SID} ({msg.Data["TYPE"]}) stored in DB.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB ERROR] Reading not stored: {ex.Message}");
            }
            finally
            {
                _dbLock.Release();
            }
        }
      
        public async Task<string> GetRecentReadingsJsonAsync(int limit = 10) {
            var readings = new List<Dictionary<string, string>>();

            await _dbLock.WaitAsync();
            try {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var selectCmd = connection.CreateCommand();

                selectCmd.CommandText = @"
                    SELECT Timestamp, SID, Zone, DataType, Value, GID
                    FROM Readings 
                    ORDER BY Id DESC 
                    LIMIT $limit";
                selectCmd.Parameters.AddWithValue("$limit", limit);

                using var reader = await selectCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) {
                    var row = new Dictionary<string, string>
                    {
                        { "Time", reader.GetString(0) },
                        { "Sensor", reader.GetString(1) },
                        { "Zone", reader.IsDBNull(2) ? "N/A" : reader.GetString(2) },
                        { "Type", reader.GetString(3) },
                        { "Value", reader.GetDouble(4).ToString("0.0") }
                    };
                    readings.Add(row);
                }
            } catch (Exception ex) {
                Console.WriteLine($"[DB ERROR] Failed to read data: {ex.Message}");
            } finally {
                _dbLock.Release();
            }

            // Transform C# List in a JSON string
            return JsonSerializer.Serialize(readings);
        }
    }
}
