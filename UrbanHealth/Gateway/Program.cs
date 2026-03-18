using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Gateway;
using Shared;

class Program {

    private static TcpListener _listener;
    private const int Port = 5000;
    private const string GID = "G101";

    private static ConfigManager _config = new ConfigManager();

    // Registry of who's online right now (SID -> Last Activity)
    private static ConcurrentDictionary<string, DateTime> _activeSensors = new();

    static async Task Main(string[] args) {

        _config.LoadConfig();

        // Starts cleaning routine of inactive sensors
        _ = Task.Run(async () => {

            while (true) {

                await Task.Delay(15000); // Checks every 15 seconds

                bool csvNeedsUpdate = false;

                foreach (var sensor in _activeSensors) {

                    // If more than 30 seconds since last HB
                    if ((DateTime.Now - sensor.Value).TotalSeconds > 30) {

                        Console.WriteLine($"[TIMEOUT] Sensor {sensor.Key} lost connection. Deactivating...");

                        _activeSensors.TryRemove(sensor.Key, out _);

                        _config.UpdateSensorState(sensor.Key, "offline");

                        csvNeedsUpdate = true;
                    }
                }

                if (csvNeedsUpdate) {
                    _config.SaveConfig();
                    Console.WriteLine("[SYSTEM] Config file updated.");
                }
            }
        });

        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();

        Console.WriteLine($"[GATEWAY] Active on port {Port}");

        while(true) {

            // accept TCP connection async
            var client = await _listener.AcceptTcpClientAsync();
            Console.WriteLine($"[GATEWAY] New sensor detected. Initiating handler...");

            _ = Task.Run(() => HandleSensorAsync(client));
        }
    }

    private static async Task HandleSensorAsync(TcpClient client) {
        
        using (client) {

            string sensorId = "Unknown";

            try {

                while (true) {

                    var msg = await Message.ReceiveMessageAsync(client);

                    if (msg == null) {

                        Console.WriteLine($"[DISCONNECT] Sensor {sensorId} closed the connection.");
                        if (sensorId != "Unknown") {
                            _activeSensors.TryRemove(sensorId, out _);
                            _config.UpdateSensorState(sensorId, "offline");
                            _config.SaveConfig();
                        }
                        break;
                    }

                    sensorId = msg.SID;

                    Console.WriteLine($"[RECEIVED] Command: {msg.CMD} by {msg.SID}");

                    await ProcessMessage(client, msg);
                }
            } catch (Exception ex) {
                Console.WriteLine($"[ERRO] Failure in communication with {sensorId}: {ex.Message}");
            }

        }
    }

    private static async Task ProcessMessage(TcpClient client, Message msg) {

        // Implement here the switch case for CONN, DATA, HB, etc...
        
        switch (msg.CMD) {

            case "CONN":
                await HandleConnection(client, msg);
                break;

            case "DATA":
                await HandleData(client, msg);
                break;

            case "HB":
                if (_activeSensors.ContainsKey(msg.SID)) {
                    _activeSensors[msg.SID] = DateTime.Now;
                    _config.UpdateLastSync(msg.SID);
                    Console.WriteLine($"[HB] {msg.SID} is alive.");
                }
                break;
        }
    }

    private static async Task HandleConnection(TcpClient client, Message msg) {

        var (exists, zone, state, allowedTypes, _) = _config.ValidateSensor(msg.SID);

        var response = new Message {
            CMD = "MSG",
            SID = msg.SID,
            GID = GID
        };
        response.Data["REF_CMD"] = "CONN";

        if (exists) {

            if (state == "offline") {
                // change state to online in memory
                _config.UpdateSensorState(msg.SID, "online");

                // save to csv instantly because its important event
                _config.SaveConfig();
            }

            response.Data["TYPE"] = "ACK";

            _activeSensors[msg.SID] = DateTime.Now;
            Console.WriteLine($"[AUTH] Sensor {msg.SID} authorized");
        } else {

            response.Data["TYPE"] = "ERR";
            Console.WriteLine($"[AUTH] Sensor {msg.SID} rejected");
        }

        await Message.SendMessageAsync(client, response);
    }

    private static async Task HandleData(TcpClient client, Message msg) {

        // only accept data from sensors who "CONN" with success
        if (_activeSensors.ContainsKey(msg.SID)) {
            _activeSensors[msg.SID] = DateTime.Now;

            var (_, zone, _, _, _) = _config.ValidateSensor(msg.SID);
            msg.Data["ZONE"] = zone;

            Console.WriteLine($"[DATA] {msg.SID} sent {msg.Data["VALUE"]} ({msg.Data["TYPE"]})");

            // TODO: Call method to FORWARD to Server
        }


    }
}