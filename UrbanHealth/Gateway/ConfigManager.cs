using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Gateway {
    public class ConfigManager {

        private readonly string _filePath;

        private Dictionary<string, (string Zone, string State, string DataTypes, string LastSync)> _sensores = new();

        private readonly object _fileLock = new object();

        public ConfigManager() {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            string projectRoot = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\sensores.csv"));


            if (File.Exists("sensores.csv")) {
                _filePath = "sensores.csv";
            }

            else if (File.Exists(Path.Combine(baseDir, "sensores.csv"))) {
                _filePath = Path.Combine(baseDir, "sensores.csv");
            }

            else {
                _filePath = projectRoot;
            }
        }
        public void LoadConfig() {

            lock (_fileLock) {
                _sensores.Clear();
                if (!File.Exists(_filePath)) {
                    Console.WriteLine("[ERROR] File sensores.csv not found!");
                    return;
                }

                var lines = File.ReadAllLines(_filePath);
                foreach (var line in lines) {

                    var parts = line.Split(";");
                    if (parts.Length == 5) {
                        _sensores[parts[0]] = (parts[1], parts[2], parts[3], parts[4]);
                    }
                }
                Console.WriteLine($"[CONFIG] {_sensores.Count} sensors loaded successfully.");

            }
   
        }

        public void UpdateSensorDataTypes(string sid, string dataTypes) {
            lock (_fileLock) {
                if (_sensores.TryGetValue(sid, out var info)) {
                    // Se o sensor existe, atualiza as capacidades, mete ONLINE e renova o tempo
                    string state = "online";
                    string lastSync = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

                    _sensores[sid] = (info.Zone, state, dataTypes, lastSync);
                    Console.WriteLine($"[CONFIG] Sensor {sid} connected. DataTypes updated to: {dataTypes}");

                    SaveConfig();
                }
                else {

                    Console.WriteLine($"[WARNING] Connection attempt from {sid}, but it's not registered. Ignored.");
                }
            }
        }

        public void UpdateLastSync(string sid) {

            lock (_fileLock) {
                if (_sensores.TryGetValue(sid, out var info)) {

                    _sensores[sid] = (info.Zone, info.State, info.DataTypes, DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
                    return;
                }

                Console.WriteLine($"[ERROR] Sensor {sid} not found.");
                return;
            }
        }

        public void UpdateSensorState(string sid, string newState) {
            
            lock(_fileLock) {
                if (_sensores.TryGetValue(sid, out var info)) {
                    // Atualiza o estado e também o LastSync
                    _sensores[sid] = (info.Zone, newState, info.DataTypes, DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
                }
            }
        }

        public void SaveConfig() {

            lock (_fileLock) {
                try {
                    List<string> lines = new List<string>();

                    foreach (var sensor in _sensores) {

                        string sid = sensor.Key;
                        var info = sensor.Value;

                        // Assemble line in original format: SID;ZONE;STATE;TYPES;LAST_SYNC
                        string line = $"{sid};{info.Zone};{info.State};{info.DataTypes};{info.LastSync}";
                        lines.Add(line);
                    }

                    // Write in file
                    File.WriteAllLines(_filePath, lines);
                    Console.WriteLine("[CONFIG] File sensores.csv updated with success.");

                } catch (Exception ex) {
                    Console.WriteLine($"[ERROR] Failure to save config: {ex.Message}");
                }
            }

        }

        public (bool Exists, string Zone, string State, string DataTypes, string LastSync) ValidateSensor(string sid) {

            if(_sensores.TryGetValue(sid, out var info)) {
                return (true, info.Zone,  info.State, info.DataTypes, info.LastSync);
            }
            return (false, "", "", "", "");
        }
    }
}
