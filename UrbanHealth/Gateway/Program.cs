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

    private const int ServerUdpPort = 5003;
    private static UdpClient _serverUdpClient = new UdpClient();

    private const int Port = 5000;
    private const string ServerIP = "192.168.126.49"; // Central Server IP
    private const int ServerPort = 5001;         // Central Server Port
    private const string GID = "G101";

    // Store for incomplete frames: Dictionary<SensorID, List<PartBytes>>
    private static ConcurrentDictionary<string, byte[][]> _videoBuffer = new();

    // Registo da última imagem completa na RAM para o Web Server ler
    private static ConcurrentDictionary<string, byte[]> _latestFrames = new();

    private static ConfigManager _config = new ConfigManager();
    // Registry of who's online right now (SID -> Last Activity)
    private static ConcurrentDictionary<string, DateTime> _activeSensors = new();

    // Registry of who's allowed to stream video right now
    private static ConcurrentDictionary<string, bool> _activeStreams = new();

    // Registry of time to clean UDP frames that lost packets
    private static ConcurrentDictionary<string, DateTime> _videoBufferTimestamps = new();

    static async Task Main(string[] args) {
        _config.LoadConfig();

        // Launch the Server connection task in the background (will try to connect infinitely)
        _ = Task.Run(ConnectToServerLoopAsync);

        _ = Task.Run(GatewayHeartbeatRoutineAsync);

        // Starts cleaning routine for inactive sensors (and video buffer)
        _ = Task.Run(async () => {
            while (true) {
                await Task.Delay(2000); // Checks every 15 seconds
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

                // Garbage collector for UDP Video
                foreach (var frameTime in _videoBufferTimestamps) {
                    
                    // If more than 1.5 seconds passed and the image didn't mount yet, discard
                    if ((DateTime.Now - frameTime.Value).TotalSeconds > 1.5) {
                        _videoBuffer.TryRemove(frameTime.Key, out _);
                        _videoBufferTimestamps.TryRemove(frameTime.Key, out _);
                        //Console.WriteLine($"[UDP CLEANUP] Incomplete frame from {frameTime.Key} discarded to free RAM.");
                    }
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

        // starts web server
        _ = Task.Run(StartWebServerAsync);

        Console.WriteLine("==================================================");
        Console.WriteLine(" Gateway Menu. Available commands:");
        Console.WriteLine(" -> DISCONN (to shutdown gateway in a clean way)");
        Console.WriteLine("==================================================\n");

        _ = Task.Run(AcceptSensorsLoopAsync);

        
        while (true) {
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) continue;

            var command = input.Trim().ToUpper();

            if (command == "DISCONN") {
                await PerformGracefulShutdownAsync();
                break; 
            }
            else {
                Console.WriteLine("[ERROR] Unknown command. Use DISCONN.");
            }
        }


    }

    private static async Task PerformGracefulShutdownAsync() {
        Console.WriteLine("\n[SYSTEM] Shutting down...");

        // Notify Server that we are going to shutdown
        if (_serverClient != null && _serverClient.Connected) {
            try {
                var byeMsg = new Message { CMD = "DISCONN", GID = GID, SID = "GATEWAY" };
                await Message.SendMessageAsync(_serverClient, byeMsg);
                Console.WriteLine("[SHUTDOWN] Server notified with success.");

                await Task.Delay(500);

                _serverClient.Close();
            } catch { }
        }

        // Stop listening new sensors and video
        if (_listener != null) _listener.Stop();
        if (_udpListener != null) _udpListener.Close();
      
        // Give 500ms to NIC send last packet before closing program
        await Task.Delay(500);

        Console.WriteLine("[SYSTEM] Gateway shutdown with success...");
        Environment.Exit(0);
    }

    // SERVER LOGIC (UPSTREAM)

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
                // Here we can process ACKs from the server in the future
            }
        } catch {
            Console.WriteLine("[GATEWAY] Connection to Server lost!");
        }
    }

    private static async Task GatewayHeartbeatRoutineAsync() {
        while (true) {
            await Task.Delay(10000); // Beats every 10 secs

            if (_serverClient != null && _serverClient.Connected) {
                try {
                    var hbMsg = new Message { CMD = "HB", GID = GID };
                    await Message.SendMessageAsync(_serverClient, hbMsg);
                } catch {           
                    // If send fails, the reconnection routine will recover the socket
                }
            }
        }
    }


    // SENSOR LOGIC (DOWNSTREAM)

    private static async Task AcceptSensorsLoopAsync() {
        while (true) {
            try {
                var client = await _listener.AcceptTcpClientAsync();
                Console.WriteLine($"[GATEWAY] New sensor detected. Initiating handler...");
                _ = Task.Run(() => HandleSensorAsync(client));
            } catch {                
                // if listener gets closed during shutdown, the error falls here silently and routine ends
                break;
            }
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

   
                    // break out of the loop to not read a dead socket
                    if (msg.CMD == "DISCONN") {
                        break;
                    }
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

                // inform server that the sensor got connected
                if (_serverClient != null && _serverClient.Connected) {
                    var statusMsg = new Message { CMD = "STS", SID = msg.SID, GID = GID };
                    statusMsg.Data["STATUS"] = "ONLINE";
                    await Message.SendMessageAsync(_serverClient, statusMsg);
                    Console.WriteLine($"[FORWARD] Sensor {msg.SID} connected to gateway {msg.GID}.");
                }
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
        } else if (msg.CMD == "DISCONN") {
            if (msg.SID == "GATEWAY") {
                // Special handling for gateway disconnection
                Console.WriteLine("[SYSTEM] Gateway disconnection acknowledged by server.");
            }
            else { 
                _activeSensors.TryRemove(msg.SID, out _);  // Novo!
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
            Console.WriteLine($"[STREAM] The sensor {msg.SID} started video streaming");

            // change output color 
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"    -> Live: http://localhost:8080/stream/{msg.SID}");
            Console.ResetColor();

            // Let server know that stream started
            if (_serverClient != null && _serverClient.Connected) {
                var fwdStrm = new Message { CMD = "FWD_STRM", SID = msg.SID, GID = GID };
                fwdStrm.Data["ACTION"] = "START";
                _ = Message.SendMessageAsync(_serverClient, fwdStrm);
            }

        } else if (action == "STOP") {
            _activeStreams.TryRemove(msg.SID, out _);

            // clean any garbage that might have stayed in this sensor buffer
            _videoBuffer.TryRemove(msg.SID, out _);

            response.Data["TYPE"] = "ACK";
            Console.WriteLine($"[STREAM] The sensor {msg.SID} stopped video streaming");
        } else if (action == "PHOTO") {

            // don't need to add to _activeStreams
            // Just clean the previous buffer to ensure the photo is "fresh"
            _videoBuffer.TryRemove(msg.SID, out _);
            response.Data["TYPE"] = "ACK";
            Console.WriteLine($"[STREAM] The sensor {msg.SID} sent a photo");

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

                if (!_activeStreams.ContainsKey(msg.SID) && msg.Data.GetValueOrDefault("TYPE", "") != "PHOTO_PART") {
                    Console.WriteLine($"[WARNING] Video package rejected. {msg.SID} didn's start STRM.");
                    continue;
                }

                // Forward UDP packet to server  
                try {
                    await _serverUdpClient.SendAsync(result.Buffer, result.Buffer.Length, ServerIP, ServerUdpPort);
                } catch { }

                int part = int.Parse(msg.Data["PART"]);
                int total = int.Parse(msg.Data["TOTAL"]);

                // Se o sensor ainda não tem buffer, OU se a nova imagem tem um tamanho diferente da que estava encravada...
                if (!_videoBuffer.TryGetValue(msg.SID, out var chunks) || chunks.Length != total) {
                    chunks = new byte[total][]; // Cria um novo espaço em branco
                    _videoBuffer[msg.SID] = chunks; // Substitui o frame estragado pelo novo
                }
                chunks[part - 1] = msg.BinaryData;

                // Console.WriteLine($"[DEBUG UDP] -> Guardei a parte {part} no buffer.");

                _videoBufferTimestamps[msg.SID] = DateTime.Now;

                // Verify if not all parts of the array are null already
                if (chunks.All(c => c != null)) {
                    byte[] fullImage = chunks.SelectMany(c => c).ToArray();

                    // Guarda a imagem na RAM para o servidor web aceder sem bloqueios
                    _latestFrames[msg.SID] = fullImage;

                    // Tenta guardar no disco (com try-catch seguro para ignorar conflitos)
                    string exactPath = Path.GetFullPath($"last_frame_{msg.SID}.jpg");
                    try {
                        await File.WriteAllBytesAsync(exactPath, fullImage);
                    } catch (IOException) {
                        // Ignora em silêncio se o Windows estiver com o ficheiro bloqueado
                    }

                    _videoBuffer.TryRemove(msg.SID, out _); // Limpa para a próxima
                    _videoBufferTimestamps.TryRemove(msg.SID, out _);
                }
            }
        } catch (Exception ex) {
            Console.WriteLine($"[ERROR] {ex.Message}");
        }
    }

    // WEB SERVER LOGIC (LIVE FEED)
    private static async Task StartWebServerAsync() {

        try {

            var listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:8080/");
            listener.Start();
            Console.WriteLine("[WEB] Web Server active. Ready to stream");

            while (true) {

                var context = await listener.GetContextAsync();
                var req = context.Request;
                var res = context.Response;

                try {

                    if (req.Url.AbsolutePath.StartsWith("/stream/")) {
                        string sid = req.Url.AbsolutePath.Split('/').Last();
                        string html = $@"
                            <!DOCTYPE html>
                            <html>
                            <body style='background:#1e1e1e;color:white;text-align:center;font-family:Segoe UI,Arial;'>
                                <h2>Live Feed UDP: {sid}</h2>
                                <img id='feed' src='/image/{sid}' style='max-width:800px;border:3px solid #007acc;border-radius:10px;' />
                                <p style='color:#00ff00;font-weight:bold;'>LIVE | 5 FPS</p>
                                <script>
                                    // Pede uma nova imagem ao Gateway a cada 200ms
                                    setInterval(() => document.getElementById('feed').src = '/image/{sid}?t=' + Date.now(), 200);
                                </script>
                            </body>
                            </html>";


                        byte[] buffer = System.Text.Encoding.UTF8.GetBytes(html);
                        res.ContentType = "text/html";
                        res.ContentLength64 = buffer.Length;
                        await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    }
                    else if (req.Url.AbsolutePath.StartsWith("/image/")) {
                        string sid = req.Url.AbsolutePath.Split('/').Last();
                   

                        // Read from RAM
                        if (_latestFrames.TryGetValue(sid, out byte[] imgBytes)) {
                            res.ContentType = "image/jpeg";
                            res.ContentLength64 = imgBytes.Length;
                            await res.OutputStream.WriteAsync(imgBytes, 0, imgBytes.Length);
                        }
                        else {
                            res.StatusCode = 404;
                        }
                    }
                    else {
                        res.StatusCode = 404;
                    }

                } finally {
                    res.Close();
                }
            }

        } catch (Exception ex) {
            Console.WriteLine($"[WEB ERROR] {ex.Message}");
        }
    }
}