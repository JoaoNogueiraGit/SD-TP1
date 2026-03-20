using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Gateway;
using Shared;

class Program {
    private static TcpListener _listener;
    private static TcpClient _serverClient; // Added to maintain the connection to the Central Server

    private static UdpClient _udpListener;
    private const int UdpPort = 5002;

    private const int Port = 5000;
    private const string ServerIP = "127.0.0.1"; // Central Server IP
    private const int ServerPort = 5001;         // Central Server Port
    private const string GID = "G101";

    // Store for incomplete frames: Dictionary<SensorID, List<PartBytes>>
    private static ConcurrentDictionary<string, byte[][]> _videoBuffer = new();

    private static ConfigManager _config = new ConfigManager();
    // Registry of who's online right now (SID -> Last Activity)
    private static ConcurrentDictionary<string, DateTime> _activeSensors = new();

    // Registry of who's allowed to stream video right now
    private static ConcurrentDictionary<string, bool> _activeStreams = new();

    static async Task Main(string[] args) {
        _config.LoadConfig();

        // Launch the Server connection task in the background (will try to connect infinitely)
        _ = Task.Run(ConnectToServerLoopAsync);

        // Starts cleaning routine for inactive sensors
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

        // Start the Gateway to listen for Sensors
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        Console.WriteLine($"[GATEWAY] Active on port {Port}");

        // inicia o listener de video udp
        _udpListener = new UdpClient(UdpPort);
        _ = Task.Run(ListenForVideoUdpAsync);

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
        var count = 0;
        while (count < 5) {
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
                count++;
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

                string msgType = msg.Data.ContainsKey("TYPE") ? msg.Data["TYPE"] : "N/A";
                Console.WriteLine($"[SERVER -> GATEWAY] Command received: {msg.CMD}; Type: {msgType}");
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
                        if (sensorId != "Unknown" && _activeSensors.ContainsKey(sensorId)) {
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

            case "DISCONN":
                await HandleDisconnection(client, msg);
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

            case "STRM":
                await HandleStreamControl(client, msg);
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

            if (state.ToLower() == "maintenance") {
                response.Data["TYPE"] = "ERR";
                response.Data["REASON"] = "MAINTENANCE";
                Console.WriteLine($"[AUTH] Sensor {msg.SID} rejected (Under Maintenance)");
            } else {

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

        }
        else {
            response.Data["TYPE"] = "ERR";
            response.Data["REASON"] = "UNKNOWN_SENSOR";
            Console.WriteLine($"[AUTH] Sensor {msg.SID} rejected");
        }

        await Message.SendMessageAsync(client, response);
    }

    private static async Task HandleDisconnection(TcpClient client, Message msg) {
        Console.WriteLine($"\n[DISCONNECT] Disconnection request from sensor {msg.SID}");
                            
        if (_activeSensors.ContainsKey(msg.SID)) {
            // remove from active sensors in memory
            _activeSensors.TryRemove(msg.SID, out _);

            // update satte to offline in csv and saves on disc
            _config.UpdateSensorState(msg.SID, "offline");
            _config.SaveConfig();

            Console.WriteLine($"[SYSTEM] Sensor {msg.SID} closed and saved as offline.");

            // inform server that the sensor got disconnected
            if (_serverClient != null && _serverClient.Connected) {
                var fwdMsg = new Message { CMD = "DISCONN", SID = msg.SID, GID = GID };
                await Message.SendMessageAsync(_serverClient, fwdMsg);
                Console.WriteLine($"[FORWARD] Disconnection warning from {msg.SID} to server");
            }
        }
    }

    private static async Task HandleData(TcpClient client, Message msg) {
        // Only accept data from sensors who "CONN" with success
        if (_activeSensors.ContainsKey(msg.SID)) {
            _activeSensors[msg.SID] = DateTime.Now;

            var (_, zone, _, allowedTypes, _) = _config.ValidateSensor(msg.SID);
            string dataType = msg.Data.ContainsKey("TYPE") ? msg.Data["TYPE"] : "UNKNOWN";

            if (!allowedTypes.Contains(dataType)) {
                Console.WriteLine($"[WARNING] {msg.SID} tried to send unsupported type: {dataType}");
                var err = new Message { CMD = "MSG", SID = msg.SID, GID = GID };
                err.Data["REF_CMD"] = "DATA";
                err.Data["TYPE"] = "ERR";
                err.Data["REASON"] = "UNSUPPORTED_TYPE";
                await Message.SendMessageAsync(client, err);
                return; // Corta a execução aqui
            }

            msg.Data["ZONE"] = zone; 

            Console.WriteLine($"[DATA] {msg.SID} sent {msg.Data["VALUE"]} ({msg.Data["TYPE"]})");

            // Enviar ACK ao sensor
            var ack = new Message { CMD = "MSG", SID = msg.SID, GID = GID };
            ack.Data["REF_CMD"] = "DATA";
            ack.Data["TYPE"] = "ACK";
            await Message.SendMessageAsync(client, ack);

            // Fwd to server
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

    // LÓGICA DE VIDEO (UDP)

    private static async Task HandleStreamControl(TcpClient client, Message msg) {

        if (!_activeSensors.ContainsKey(msg.SID)) return; // only online sensors can ask permission to stream

        string action = msg.Data.ContainsKey("ACTION") ? msg.Data["ACTION"].ToUpper() : "";

        var response = new Message { CMD = "MSG", SID = msg.SID, GID = GID };
        response.Data["REF_CMD"] = "STRM";

        if (action == "START") {
            _activeStreams.TryAdd(msg.SID, true);
            response.Data["TYPE"] = "ACK";
            Console.WriteLine($"[STREAM CONTROL] The sensor {msg.SID} started video streaming");
        } else if (action == "STOP") {
            _activeStreams.TryRemove(msg.SID, out _);

            // clean any garbage that might have stayed in this sensor buffer
            _videoBuffer.TryRemove(msg.SID, out _);

            response.Data["TYPE"] = "ACK";
            Console.WriteLine($"[STREAM CONTROL] The sensor {msg.SID} stopped video streaming");
        } else {
            response.Data["TYPE"] = "ERR";
            response.Data["REASON"] = "INVALID_ACTION";
        }

        await Message.SendMessageAsync(client, response);
    }

    private static async Task ListenForVideoUdpAsync() {
        try {
            
            Console.WriteLine("[GATEWAY-VIDEO] UDP Listener listening on port 5002...");

            while (true) {
                var result = await _udpListener.ReceiveAsync();
                // Console.WriteLine($"\n[DEBUG UDP] -> Recebi pacote UDP de {result.Buffer.Length} bytes!");

                var msg = Message.FromUdpBytes(result.Buffer);

                if (msg == null) {
                    // Console.WriteLine("[DEBUG UDP] -> ERRO: O pacote não é uma Message válida.");
                    continue;
                }

                // Console.WriteLine($"[DEBUG UDP] -> Mensagem convertida: SID={msg.SID}, PARTE={msg.Data["PART"]}/{msg.Data["TOTAL"]}");

                if (!_activeSensors.ContainsKey(msg.SID)) {
                    Console.WriteLine($"[ERROR] The sensor {msg.SID} is not on the sensors list!");
                    continue;
                }

                if (!_activeStreams.ContainsKey(msg.SID)) {
                    Console.WriteLine($"[WARNING] Video package rejected. {msg.SID} didn's start STRM.");
                    continue;
                }

                int part = int.Parse(msg.Data["PART"]);
                int total = int.Parse(msg.Data["TOTAL"]);

                // Se o sensor ainda não tem buffer, OU se a nova imagem tem um tamanho diferente da que estava encravada...
                if (!_videoBuffer.TryGetValue(msg.SID, out var chunks) || chunks.Length != total) {
                    chunks = new byte[total][]; // Cria um novo espaço em branco
                    _videoBuffer[msg.SID] = chunks; // Substitui o frame estragado pelo novo
                }
                chunks[part - 1] = msg.BinaryData;

                // Console.WriteLine($"[DEBUG UDP] -> Guardei a parte {part} no buffer.");

                // Verify if not all parts of the array are null already
                if (chunks.All(c => c != null)) {
                    byte[] fullImage = chunks.SelectMany(c => c).ToArray();
                   
                    string exactPath = Path.GetFullPath($"last_frame_{msg.SID}.jpg");

                    await File.WriteAllBytesAsync(exactPath, fullImage);

                    _videoBuffer.TryRemove(msg.SID, out _); // Limpa para a próxima

                    // Console.WriteLine($"\n[VIDEO] Frame montado com sucesso!");
                    // Console.WriteLine($"[Caminho do Ficheiro] ---> {caminhoExato}\n");
                }
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] {ex.Message}");
        }
    }
}