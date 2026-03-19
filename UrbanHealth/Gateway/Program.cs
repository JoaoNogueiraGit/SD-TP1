using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Gateway;
using Shared;

class Program {
    private static TcpListener _listener;
    private static TcpClient _serverClient; // Added to maintain the connection to the Central Server

    private const int Port = 5000;
    private const string ServerIP = "127.0.0.1"; // Central Server IP
    private const int ServerPort = 5001;         // Central Server Port
    private const string GID = "G101";

    private static ConfigManager _config = new ConfigManager();
    // Registry of who's online right now (SID -> Last Activity)
    private static ConcurrentDictionary<string, DateTime> _activeSensors = new();

    static async Task Main(string[] args) {
        _config.LoadConfig();

        // 1. Launch the Server connection task in the background (will try to connect infinitely)
        _ = Task.Run(ConnectToServerLoopAsync);

        // 2. Starts cleaning routine for inactive sensors
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

        // 3. Start the Gateway to listen for Sensors
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        Console.WriteLine($"[GATEWAY] Active on port {Port}");

        while (true) {
            // Accept TCP connection async
            var client = await _listener.AcceptTcpClientAsync();
            Console.WriteLine($"[GATEWAY] New sensor detected. Initiating handler...");
            _ = Task.Run(() => HandleSensorAsync(client));
        }
    }

    // =========================================================
    // CENTRAL SERVER LOGIC (UPSTREAM)
    // =========================================================

    private static async Task ConnectToServerLoopAsync() {
        while (true) {
            try {
                _serverClient = new TcpClient();
                Console.WriteLine("[GATEWAY] Trying to connect to Central Server...");
                await _serverClient.ConnectAsync(ServerIP, ServerPort);
                Console.WriteLine("[GATEWAY] Connected to Central Server!");

                // Notify the server that we are online
                var sts = new Message { CMD = "STS", GID = GID };
                sts.Data["STATUS"] = "ONLINE";
                await Message.SendMessageAsync(_serverClient, sts);

                // Keep listening to server orders until the connection drops
                await ListenToServerAsync(_serverClient);
            } catch {
                // Failed or connection dropped. Silence the error and try again.
                Console.WriteLine("[GATEWAY] Server offline. Retrying in 5 seconds...");
            }

            // Wait before trying to reconnect
            await Task.Delay(5000);
        }
    }

    private static async Task ListenToServerAsync(TcpClient server) {
        try {
            while (true) {
                var msg = await Message.ReceiveMessageAsync(server);
                if (msg == null) break; // Server closed the connection

                Console.WriteLine($"[SERVER -> GATEWAY] Command received: {msg.CMD}; Type: {msg.Data["TYPE"]};");
                // Here you can process ACKs from the server in the future
            }
        } catch {
            Console.WriteLine("[GATEWAY] Connection to Server lost!");
        }
    }


    // =========================================================
    // SENSOR LOGIC (DOWNSTREAM)
    // =========================================================

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
                Console.WriteLine($"[ERROR] Failure in communication with {sensorId}: {ex.Message}");
            }
        }
    }

    private static async Task ProcessMessage(TcpClient client, Message msg) {
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
            if (state == "offline" || state == "desativado") {
                // Change state to online in memory
                _config.UpdateSensorState(msg.SID, "online");
                // Save to CSV instantly because it's an important event
                _config.SaveConfig();
            }

            response.Data["TYPE"] = "ACK";
            response.Data["ZONE"] = zone; // Inject the zone so the sensor knows

            _activeSensors[msg.SID] = DateTime.Now;
            Console.WriteLine($"[AUTH] Sensor {msg.SID} authorized in {zone}");
        }
        else {
            response.Data["TYPE"] = "ERR";
            Console.WriteLine($"[AUTH] Sensor {msg.SID} rejected");
        }

        await Message.SendMessageAsync(client, response);
    }

    private static async Task HandleData(TcpClient client, Message msg) {
        // Only accept data from sensors who "CONN" with success
        if (_activeSensors.ContainsKey(msg.SID)) {
            _activeSensors[msg.SID] = DateTime.Now;

            var (_, zone, _, _, _) = _config.ValidateSensor(msg.SID);
            msg.Data["ZONE"] = zone; 

            Console.WriteLine($"[DATA] {msg.SID} sent {msg.Data["VALUE"]} ({msg.Data["TYPE"]})");

            // FORWARD TO CENTRAL SERVER
            if (_serverClient != null && _serverClient.Connected) {
                var fwdMsg = new Message { CMD = "FWD", SID = msg.SID, GID = GID };
                // Copy all data from the sensor to the new FWD command
                foreach (var kvp in msg.Data) {
                    fwdMsg.Data[kvp.Key] = kvp.Value;
                }

                await Message.SendMessageAsync(_serverClient, fwdMsg);
                Console.WriteLine($"[FORWARD] Data from {msg.SID} sent to Server.");
            }
            else {
                Console.WriteLine($"[WARNING] Data from {msg.SID} discarded: Server is not connected.");
            }
        }
    }
}