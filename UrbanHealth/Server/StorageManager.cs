using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Shared;

namespace Server
{
    // Sistema de ficheiros para guardar os dados recebidos dos gateways
    // *Posteriormente pode ser criada uma base de dados relacional ( + pontinhos no trabalho ;) )
    public class StorageManager
    {
        private readonly string _baseDirectory = "DataLogs";

        // Dicionário para garantir um objeto de lock para cada tipo de ficheiro
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

        public StorageManager()
        {
            if (!Directory.Exists(_baseDirectory))
            {
                Directory.CreateDirectory(_baseDirectory);
            }
        }

        public async Task Savemessage(Message msg)
        {
            string dataType = msg.Data.ContainsKey("TYPE") ? msg.Data["TYPE"] : "UNKNOWN";
            string filename = Path.Combine(_baseDirectory, $"dados_{dataType.ToLower()}.csv");  

            var semaphore = _fileLocks.GetOrAdd(filename, _ => new SemaphoreSlim(1, 1));

            await semaphore.WaitAsync();

            try
            {
                string line = $"{msg.Timestamp};{msg.GID};{msg.SID};{msg.Data["ZONE"]};{msg.Data["VALUE"]}{Environment.NewLine}";

                await File.AppendAllTextAsync(filename, line, Encoding.UTF8);

                Console.WriteLine($"[STORAGE] Stored successfully in {filename}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[STORAGE ERROR] Error writing in {filename}: {ex.Message}");
            }
            finally
            {
                semaphore.Release();
            }


        }

    }
}
