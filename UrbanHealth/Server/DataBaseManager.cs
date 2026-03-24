using Microsoft.Data.Sqlite;
using Shared;
using System.Data;

namespace Server
{
    public class DataBaseManager
    {
        private readonly string _connectionString = "Data Source=onehealth_urbano.db";

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
            var semaphore = new SemaphoreSlim(1, 3);
            await semaphore.WaitAsync();

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
                semaphore.Release();
            }
        }
    }
}
