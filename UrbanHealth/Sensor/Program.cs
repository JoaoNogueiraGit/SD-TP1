using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Principal;
using System.Threading.Tasks;
using Shared;

class Program {

    private static TcpClient _gatewayClient;
    private static int GatewayPort = 5000;
    private static string GatewayIP = "127.0.0.1";
    private static string SID = "S101";

    // State management
    private static bool _isAuthenticated = false;
    private static string _zone = "Unknown";

    private static bool _isStreaming = false;
    private static string _requestedStreamAction = ""; // stores last (START or STOP)

    private static DateTime _lastAlertTime = DateTime.MinValue;

    static async Task Main(string[] args) {

        // Read console args
        if (args.Length >= 2) {
            SID = args[0];
            GatewayIP = args[1];
        }

        if (args.Length == 3) {
            GatewayPort = int.Parse(args[2]);
        }

        Console.WriteLine($"[SYSTEM] Starting Sensor {SID} connecting to {GatewayIP}:{GatewayPort}...");
        Console.WriteLine("==================================================");
        Console.WriteLine(" Interactive Menu. Available commands:");
        Console.WriteLine(" -> DATA <TYPE> <VALUE> (e.g., DATA HUM 65.2)");
        Console.WriteLine(" -> STRM START (to request video transmission)");
        Console.WriteLine(" -> STRM STOP (to end video transmission)");
        Console.WriteLine(" -> DISCONN (to gracefully shutdown)");
        Console.WriteLine("==================================================\n");

        // start parallel routines (will only send if authenticated)
        _ = Task.Run(HeartbeatRoutineAsync);
        _ = Task.Run(ConnectToGatewayLoopAsync);
        _ = Task.Run(VideoStreamRoutineAsync);

        _ = Task.Run(DataGenerationRoutineAsync);



        while (true) {
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) continue;

            // Split the user input by spaces
            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var command = parts[0].ToUpper();

            if (command == "DISCONN") {
                if (_isAuthenticated && _gatewayClient != null && _gatewayClient.Connected) {
                    var byeMsg = new Message { CMD = "DISCONN", SID = SID };
                    await Message.SendMessageAsync(_gatewayClient, byeMsg);
                    Console.WriteLine("[SENSOR] Sent DISCONN. Shutting down...");


                    // wait half second to allow network to send packet before windows kill process
                    await Task.Delay(500);

                    _gatewayClient.Close();
                }
                else {
                    Console.WriteLine("[SENSOR] Shutting down (was not connected).");
                }
                break; // Exits the while loop and closes the app
            }
            else if (command == "STRM") {
                if (!_isAuthenticated || _gatewayClient == null || !_gatewayClient.Connected) {
                    Console.WriteLine("[WARNING] Cannot send stream request: Sensor is not authenticated.");
                    continue;
                }

                if (parts.Length >= 2) {
                    string action = parts[1].ToUpper();
                    
                    if (action == "START" || action == "STOP") {
                        _requestedStreamAction = action;

                        var strmMsg = new Message { CMD = "STRM", SID = SID };
                        strmMsg.Data["ACTION"] = action;

                        await Message.SendMessageAsync(_gatewayClient, strmMsg);
                        Console.WriteLine($"[SENSOR] Sent STRM {action}, waiting for authorization...");

                        //// if its PHOTO, send immediatly 
                        //if (action == "PHOTO") {
                        //    _ = Task.Run(() => SendSinglePhotoAsync("frame.jpg"));
                        //}
                    }
                    else {
                        Console.WriteLine("[ERROR] Invalid action. Use: STRM START, STOP or PHOTO");
                    }
                }
                else {
                    Console.WriteLine("[ERROR] Invalid format. Use: STRM <ACTION>");
                }
            }
            else if (command == "DATA") {
                if (!_isAuthenticated || _gatewayClient == null || !_gatewayClient.Connected) {
                    Console.WriteLine("[WARNING] Cannot send data: Sensor is not authenticated with the Gateway.");
                    continue;
                }

                // Ensure the user typed all 3 parts: DATA + TYPE + VALUE
                if (parts.Length >= 3) {
                    string dataType = parts[1].ToUpper();
                    string dataValue = parts[2];

                    var manualMsg = new Message { CMD = "DATA", SID = SID };
                    manualMsg.Data["TYPE"] = dataType;
                    manualMsg.Data["VALUE"] = dataValue;

                    try {
                        await Message.SendMessageAsync(_gatewayClient, manualMsg);
                        Console.WriteLine($"[MANUAL] Sent {dataType}: {dataValue}");
                    } catch (Exception ex) {
                        Console.WriteLine($"[ERROR] Failed to send message: {ex.Message}");
                        _isAuthenticated = false;
                    }
                }
                else {
                    Console.WriteLine("[ERROR] Invalid format. Use: DATA <TYPE> <VALUE>");
                }
            }
            else {
                Console.WriteLine("[ERROR] Unknown command. Use DATA, STRM or DISCONN.");
            }
        }

    }


    private static async Task ConnectToGatewayLoopAsync() {

        while (true) {

            try {

                _gatewayClient = new TcpClient();
                Console.WriteLine("[SENSOR] Trying to connect to Gateway...");
                await _gatewayClient.ConnectAsync(GatewayIP, GatewayPort);
                Console.WriteLine("[SENSOR] Connected to Gateway!");

                var connMsg = new Message { CMD = "CONN", SID = SID };
                await Message.SendMessageAsync(_gatewayClient, connMsg);
                Console.WriteLine("[SENSOR] Sent CONN, waiting for response...");

                await ListenToGatewayAsync(_gatewayClient);

            } catch {

                Console.WriteLine("[SENSOR] Gateway offline, Retrying in 5 seconds...");
            }

            // if we reach here, the connection dropped
            _isAuthenticated = false;
            _isStreaming = false;
            await Task.Delay(5000);
        }
    }

    private static async Task ListenToGatewayAsync(TcpClient gateway) {

        try {
            while (true) {

                var msg = await Message.ReceiveMessageAsync(gateway);
                if (msg == null) break; // Gateway closed the connection

                string msgType = msg.Data.ContainsKey("TYPE") ? msg.Data["TYPE"] : "N/A";

                // Avoid logging ACKs unless necessary
                if (msgType != "ACK") {
                    Console.WriteLine($"[GATEWAY -> SENSOR] Command received: {msg.CMD}; Type: {msgType}");
                }

                if (msg.CMD == "MSG" && msg.Data.ContainsKey("TYPE")) {

                    if (msg.Data["TYPE"] == "ACK") {
                        if (msg.Data["REF_CMD"] == "CONN") {
                            _isAuthenticated = true;

                            if (msg.Data.ContainsKey("ZONE")) _zone = msg.Data["ZONE"];
                            Console.WriteLine($"[AUTH] Success! Operating in zone: {_zone}");
                        }
                        else if (msg.Data["REF_CMD"] == "STRM") {
                            if (_requestedStreamAction == "START") {
                                _isStreaming = true;
                                Console.WriteLine("[STREAM] Gateway authorized START. Sending bytes...");
                            }
                            else if (_requestedStreamAction == "STOP") {
                                _isStreaming = false;
                                Console.WriteLine("[STREAM] Gateway authorized STOP. Video paused.");
                            }
                        }
                    }
                    else if (msg.Data["TYPE"] == "ERR") {
                        if (msg.Data["REF_CMD"] == "CONN") {
                            Console.WriteLine("[AUTH] Sensor got rejected by gateway.");
                            break;
                        }
                    }
                }
            }
        } catch { }
        Console.WriteLine("[SENSOR] Connection to Gateway lost!");
        _isAuthenticated = false;
        _isStreaming = false;
    }

    private static async Task HeartbeatRoutineAsync() {

        while (true) {
            await Task.Delay(10000); // send HB every 10 seconds

            if (_isAuthenticated && _gatewayClient != null && _gatewayClient.Connected) {
                try {
                    var hbMsg = new Message { CMD = "HB", SID = SID };
                    await Message.SendMessageAsync(_gatewayClient, hbMsg);
                    // Console.WriteLine("[SENSOR] Sent HB"); // Uncomment to see in action
                } catch {
                    _isAuthenticated = false;
                    _isStreaming = false;
                }
            }
        }
    }

    private static async Task DataGenerationRoutineAsync() {
        Random rnd = new Random();

        // Lista dos tipos de dados suportados pelo nosso Sensor
        string[] supportedTypes = { "TEMP", "HUM", "PM2", "CO2", "NOISE", "UV" };

        while (true) {
            await Task.Delay(7000); // Gera um dado a cada 7 segundos

            if (_isAuthenticated && _gatewayClient != null && _gatewayClient.Connected) {
                try {
                    var dataMsg = new Message { CMD = "DATA", SID = SID };

                    // Escolhe um tipo de dados aleatoriamente
                    string selectedType = supportedTypes[rnd.Next(supportedTypes.Length)];
                    dataMsg.Data["TYPE"] = selectedType;

                    double value = 0;
                    bool isAlert = false;

                    // Gera valores realistas baseados no tipo
                    switch (selectedType) {
                        case "TEMP":  // Temperature (15 to 35 °C)
                            value = 15.0 + (rnd.NextDouble() * 20.0);
                            if (value > 33.0) isAlert = true;
                            break;
                        case "HUM":   // Humidity (40 to 80 %)
                            value = 40.0 + (rnd.NextDouble() * 40.0);
                            if (value > 78.0) isAlert = true;
                            break;
                        case "PM2":   // Quality of air - particles (5 to 50 ug/m³)
                            value = 5.0 + (rnd.NextDouble() * 45.0);
                            if (value > 48.0) isAlert = true;
                            break;
                        case "CO2":   // Carbon Dioxide (400 to 1000 ppm)
                            value = 400.0 + (rnd.NextDouble() * 600.0);
                            if (value > 995.0) isAlert = true;
                            break;
                        case "NOISE": // Noise Pollution (40 to 90 dB)
                            value = 40.0 + (rnd.NextDouble() * 50.0);
                            if (value > 88.0) isAlert = true;
                            break;
                        case "UV":    // UV (0 to 10)
                            value = rnd.NextDouble() * 10.0;
                            if (value > 9.0) isAlert = true;
                            break;
                    }

                    dataMsg.Data["VALUE"] = value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

                    await Message.SendMessageAsync(_gatewayClient, dataMsg);
                    Console.WriteLine($"[DATA] {selectedType}: {dataMsg.Data["VALUE"]}");                    

                    if (isAlert) {
                        Console.WriteLine($"[STREAM] High {selectedType} detected! Requesting video...");
                        _lastAlertTime = DateTime.Now;

                        if (!_isStreaming) {
                            _requestedStreamAction = "START";
                            var strmMsg = new Message { CMD = "STRM", SID = SID };
                            strmMsg.Data["ACTION"] = "START";

                            await Message.SendMessageAsync(_gatewayClient, strmMsg);

                        }
                    } else {
                        
                        if (_isStreaming && (DateTime.Now - _lastAlertTime).TotalSeconds > 30) {
                            Console.WriteLine("[STREAM] Environment stabilized for 30s. Stopping video...");
                            _requestedStreamAction = "STOP";
                            var strmMsg = new Message { CMD = "STRM", SID = SID };
                            strmMsg.Data["ACTION"] = "STOP";

                            await Message.SendMessageAsync(_gatewayClient, strmMsg);
                        }
                    }

                } catch {
                    _isAuthenticated = false;
                    _isStreaming = false;
                }
            }
        }
    }

    private static async Task VideoStreamRoutineAsync() {
        using var udpClient = new UdpClient();
        const int chunkSize = 1400; 

        // Pasta onde vais colocar a tua "sequência de vídeo"
        string framesFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Frames");

        // Cria a pasta automaticamente se ela não existir
        if (!Directory.Exists(framesFolder)) {
            Directory.CreateDirectory(framesFolder);
            // Console.WriteLine($"[AVISO DE VÍDEO] Criei a pasta: {framesFolder}");
            // Console.WriteLine("[AVISO DE VÍDEO] Coloca lá algumas imagens .jpg (ex: 1.jpg, 2.jpg) para simular o vídeo.");
        }

        while (true) {
            // Only stream if authenticated AND user requested START
            if (!_isAuthenticated || !_isStreaming) {
                await Task.Delay(1000);
                continue;
            }

            // Get every image in directory
            string[] frames = Directory.Exists(framesFolder)
                ? Directory.GetFiles(framesFolder, "*.jpg")
                : Array.Empty<string>();

            if (frames.Length == 0) {
                await Task.Delay(2000); // Wait if there is no images
                continue;
            }

            // Percorre cada imagem (Frame) para criar a ilusão de movimento
            foreach (var framePath in frames) {
                // Double check status inside frame loop to stop instantly
                if (!_isStreaming || !_isAuthenticated) break;

                await Task.Delay(200); // ~5 FPS (Change to 100 to get 10 fps) 

                try {
                    byte[] imageBytes = await File.ReadAllBytesAsync(framePath);
                    int totalParts = (int)Math.Ceiling((double)imageBytes.Length / chunkSize);

                    for (int i = 0; i < totalParts; i++) {
                        int currentOffset = i * chunkSize;
                        int size = Math.Min(chunkSize, imageBytes.Length - currentOffset);
                        byte[] buffer = new byte[size];
                        Buffer.BlockCopy(imageBytes, currentOffset, buffer, 0, size);

                        var videoMsg = new Message { CMD = "STRM", SID = SID, GID = "G101" };
                        videoMsg.Data["TYPE"] = "DATA";
                        videoMsg.Data["PART"] = (i + 1).ToString();
                        videoMsg.Data["TOTAL"] = totalParts.ToString();
                        videoMsg.BinaryData = buffer;

                        byte[] packet = videoMsg.ToUdpBytes();
                        await udpClient.SendAsync(packet, packet.Length, GatewayIP, 5002);
                    }
                } catch {
                    // Ignore in silence
                }
            }
        }
    }

    //private static async Task SendSinglePhotoAsync(string filePath) {
    //    using var udpClient = new UdpClient();
    //    const int chunkSize = 1400;

    //    if (File.Exists(filePath)) {
    //        byte[] imageBytes = await File.ReadAllBytesAsync(filePath);
    //        int totalParts = (int)Math.Ceiling((double)imageBytes.Length / chunkSize);

    //        for (int i = 0; i < totalParts; i++) {
    //            int currentOffset = i * chunkSize;
    //            int size = Math.Min(chunkSize, imageBytes.Length - currentOffset);
    //            byte[] buffer = new byte[size];
    //            Buffer.BlockCopy(imageBytes, currentOffset, buffer, 0, size);

    //            var msg = new Message { CMD = "STRM", SID = SID, GID = "G101" };
    //            msg.Data["TYPE"] = "PHOTO_PART"; // Special flag so the gateway allows
    //            msg.Data["PART"] = (i + 1).ToString();
    //            msg.Data["TOTAL"] = totalParts.ToString();
    //            msg.BinaryData = buffer;

    //            byte[] packet = msg.ToUdpBytes();
    //            await udpClient.SendAsync(packet, packet.Length, GatewayIP, 5002);
    //        }
    //        Console.WriteLine("[SENSOR] Single photo transmission finished.");
    //    } else {
    //        Console.WriteLine("[ERROR] File frame.jpg not found for PHOTO action.");
    //    }
    //}
}