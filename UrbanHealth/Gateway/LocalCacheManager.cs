using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Gateway
{
    public class LocalCacheManager
    {
        private readonly string _connectionString = "Data Source=gateway_cache.db";

        public LocalCacheManager()
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();

                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS OfflineReadings (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        SensorId TEXT,
                        DataType TEXT,
                        Value REAL,
                        Timestamp TEXT
                    )";
                command.ExecuteNonQuery();
            }
        }

        public void SaveReading(string sid, string type, double value, DateTime ts)
        {
            try
            {
                Console.WriteLine($"[CACHE-DEBUG] Tentando guardar leitura: SID={sid}, Type={type}, Value={value}, TS={ts:o}");

                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    Console.WriteLine($"[CACHE-DEBUG] Conexão aberta para: {_connectionString}");

                    var command = connection.CreateCommand();
                    command.CommandText = "INSERT INTO OfflineReadings (SensorId, DataType, Value, Timestamp) VALUES ($sid, $type, $val, $ts)";
                    command.Parameters.AddWithValue("$sid", sid);
                    command.Parameters.AddWithValue("$type", type);
                    command.Parameters.AddWithValue("$val", value);
                    command.Parameters.AddWithValue("$ts", ts.ToString("o"));

                    int rowsAffected = command.ExecuteNonQuery();
                    Console.WriteLine($"[CACHE-DEBUG] INSERT executado com sucesso. Linhas afetadas: {rowsAffected}");
                }

                Console.WriteLine($"[CACHE-SUCCESS] Leitura de {sid} guardada em SQLite com sucesso!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CACHE-ERROR] Erro ao guardar leitura em SQLite: {ex.GetType().Name}");
                Console.WriteLine($"[CACHE-ERROR] Mensagem: {ex.Message}");
                Console.WriteLine($"[CACHE-ERROR] StackTrace: {ex.StackTrace}");
                throw; // Re-throw para que o chamador saiba que falhou
            }
        }

        public List<(int Id, string Sid, string Type, double Value, DateTime Ts)> GetPendingReadings()
        {
            var list = new List<(int, string, string, double, DateTime)>();
            try
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    Console.WriteLine($"[CACHE-DEBUG] Lendo pendentes do SQLite...");

                    var command = connection.CreateCommand();
                    command.CommandText = "SELECT * FROM OfflineReadings LIMIT 100";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            list.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3), DateTime.Parse(reader.GetString(4))));
                        }
                    }

                    Console.WriteLine($"[CACHE-DEBUG] Total de registos pendentes encontrados: {list.Count}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CACHE-ERROR] Erro ao ler pendentes: {ex.Message}");
                Console.WriteLine($"[CACHE-ERROR] StackTrace: {ex.StackTrace}");
            }
            return list;
        }

        public void DeleteReadings(IEnumerable<int> ids)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    foreach (var id in ids)
                    {
                        var command = connection.CreateCommand();
                        command.CommandText = "DELETE FROM OfflineReadings WHERE Id = $id";
                        command.Parameters.AddWithValue("$id", id);
                        command.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }
            }
        }
    }
}
