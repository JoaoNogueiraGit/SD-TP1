using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Server;
using Shared; // Your common library

class Program {

    private static UdpClient _udpListener;
    private const int UdpPort = 5003;
    private static ConcurrentDictionary<string, byte[][]> _videoBuffer = new();
    private static ConcurrentDictionary<string, byte[]> _latestFrames = new();
    private static ConcurrentDictionary<string, DateTime> _videoBufferTimestamps = new();


    //private static StorageManager _storage = new StorageManager();
    private static DataBaseManager _dbManager = new DataBaseManager();

    // Registry of active gateways (GID -> Last Sync)
    private static ConcurrentDictionary<string, DateTime> _activeGateways = new();

    private static ConcurrentDictionary<string, string> _activeSensors = new();

    static async Task Main(string[] args) {
        int port = 5001;
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        Console.WriteLine($"[SERVER] Listening on port {port}");

        // starts cleaning routine of dead gateways
        _ = Task.Run(MonitorGatewaysAsync);


        // Turn on UDP receptor and Web Server Dashboard
        _udpListener = new UdpClient(UdpPort);
        _ = Task.Run(ListenForVideoUdpAsync);
        _ = Task.Run(StartWebDashboardAsync);

        while (true) {
            // Waits for a Gateway to connect
            var client = await listener.AcceptTcpClientAsync();
            Console.WriteLine("[SERVER] New connection detected (Gateway)!");

            _ = Task.Run(() => HandleGatewayAsync(client));
        }
    }

    private static async Task MonitorGatewaysAsync() {

        while (true) {

            await Task.Delay(2000); // every 2 sec

            foreach (var gw in _activeGateways) {

                // if more than 30 secs passed since last HB
                if ((DateTime.Now - gw.Value).TotalSeconds > 30) {
                    Console.WriteLine($"[TIMEOUT] Gateway {gw.Key} lost connection. ");
                    _activeGateways.TryRemove(gw.Key, out _);

                    // we can implement some kind of external "alert" here
                }
            }

            // Garbage collector for UDP Video
            foreach (var frameTime in _videoBufferTimestamps) {
                if ((DateTime.Now - frameTime.Value).TotalSeconds > 1.5) {
                    _videoBuffer.TryRemove(frameTime.Key, out _);
                    _videoBufferTimestamps.TryRemove(frameTime.Key, out _);
                }
            }
        }
    }

    private static async Task HandleGatewayAsync(TcpClient client) {
        using (client) {
            string gatewayId = "Unknown";
            try {
                while (true) {
                    var msg = await Message.ReceiveMessageAsync(client);

                    if (msg == null) {
                        Console.WriteLine("[SERVER] The Gateway disconnected.");
                        break;
                    }

                    gatewayId = msg.GID;

                    _activeGateways[gatewayId] = DateTime.Now;

                    // Prints the received command with emphasis
                    Console.WriteLine($"\n[RECEIVED] Command: {msg.CMD} | GID: {msg.GID}");

                    
                    if (msg.CMD == "STS") {
                        Console.WriteLine($"   -> Status Message: {msg.Data["STATUS"]} | SID: {msg.SID}");

                        // Only process sensor status messages (not gateway status messages)
                        if (!string.IsNullOrEmpty(msg.SID) && msg.SID != msg.GID) {
                            Console.WriteLine($"[STATUS] Sensor {msg.SID} state: {msg.Data["STATUS"]}");
                            if (msg.Data["STATUS"] == "ONLINE") _activeSensors[msg.SID] = msg.GID;
                            else if (msg.Data["STATUS"] == "OFFLINE") _activeSensors.TryRemove(msg.SID, out _);
                        }
                    }
                    else if (msg.CMD == "FWD") {

                        // _activeSensors[msg.SID] = msg.GID;

                        Console.WriteLine($"   -> Forwarded Data from Sensor: {msg.SID}");
                        Console.WriteLine($"   -> Injected Zone: {msg.Data["ZONE"]}");
                        Console.WriteLine($"   -> Reading: {msg.Data["TYPE"]} = {msg.Data["VALUE"]}");

                        //await _storage.Savemessage(msg);
                        await _dbManager.SaveReadingsAsync(msg);
                    }
                    else if (msg.CMD == "FWD_STRM" && msg.Data.GetValueOrDefault("ACTION", "") == "START") {
                        Console.WriteLine($"\n   -> [STREAM] Gateway {msg.GID} forwarded a video from Sensor {msg.SID}!");

                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"   -> Live: http://localhost:8081/stream/{msg.SID}");
                        Console.ResetColor();
                    }
                    else if (msg.CMD == "DISCONN") {
                        if (msg.SID == "GATEWAY") {
                            Console.WriteLine($"\n   -> [SHUTDOWN] Gateway {msg.GID} has shutdown his activity in a clean way..");
                            if (gatewayId != "Unknown") _activeGateways.TryRemove(gatewayId, out _);
                            break;
                        }
                        else {
                            // Sensor disconnection
                            Console.WriteLine($"   -> [DISCONNECT] Sensor {msg.SID} disconnected from Gateway {msg.GID}");
                            _activeSensors.TryRemove(msg.SID, out _);
                        }
                    }
                    else if (msg.CMD == "HB") {
                        // if you want to see HB on the console just uncomment
                        // Console.WriteLine($"[HB] Gateway {msg.GID} is alive.");
                    }
                }
            } catch (Exception ex) {
                Console.WriteLine($"[ERROR] Failure in communication with Gateway {gatewayId}: {ex.Message}");
                if (gatewayId != "Unknown") _activeGateways.TryRemove(gatewayId, out _);
            }
        }
    }

    private static async Task ListenForVideoUdpAsync() {
        Console.WriteLine($"[SERVER-VIDEO] UDP Listener active on port {UdpPort}...");
        try {
            while (true) {
                var result = await _udpListener.ReceiveAsync();
                var msg = Message.FromUdpBytes(result.Buffer);

                if (msg == null) continue;

                int part = int.Parse(msg.Data["PART"]);
                int total = int.Parse(msg.Data["TOTAL"]);

                if (!_videoBuffer.TryGetValue(msg.SID, out var chunks) || chunks.Length != total) {
                    chunks = new byte[total][];
                    _videoBuffer[msg.SID] = chunks;
                }
                chunks[part - 1] = msg.BinaryData;

                _videoBufferTimestamps[msg.SID] = DateTime.Now;

                // If got all pieces, store in server RAM
                if (chunks.All(c => c != null)) {
                    byte[] fullImage = chunks.SelectMany(c => c).ToArray();
                    _latestFrames[msg.SID] = fullImage;
                    _videoBuffer.TryRemove(msg.SID, out _);
                    _videoBufferTimestamps.TryRemove(msg.SID, out _);
                }
            }
        } catch (Exception ex) {
            Console.WriteLine($"[SERVER-VIDEO ERROR] {ex.Message}");
        }
    }

    private static async Task StartWebDashboardAsync() {
        try {
            var listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:8081/");
            listener.Start();
            Console.WriteLine("[WEB] Web Server Ready at http://localhost:8081/");

            while (true) {
                var context = await listener.GetContextAsync();
                var req = context.Request;
                var res = context.Response;

                try {

                    if (req.Url.AbsolutePath == "/" || req.Url.AbsolutePath == "/index.html") {
                        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "index.html");

                        if (File.Exists(path)) {
                            byte[] htmlBytes = await File.ReadAllBytesAsync(path);
                            res.ContentType = "text/html";
                            res.ContentLength64 = htmlBytes.Length;
                            await res.OutputStream.WriteAsync(htmlBytes, 0, htmlBytes.Length);
                        }
                        else {
                            string erro = "<h1>index.html file not found on server folder!</h1>";
                            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(erro);
                            res.ContentType = "text/html";
                            res.ContentLength64 = buffer.Length;
                            await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                        }
                    }
                    else if (req.Url.AbsolutePath == "/api/status") {
                        // Converte as chaves do dicionário de Gateways num array JSON (ex: ["G101", "G102"])
                        string gwList = string.Join(",", _activeGateways.Keys.Select(k => $"\"{k}\""));

                        string activeSensorsJson = "[" + string.Join(",", _activeSensors.Select(s => $"{{\"Sensor\":\"{s.Key}\", \"Gateway\":\"{s.Value}\"}}")) + "]";

                        string jsonReadings = await _dbManager.GetRecentReadingsJsonAsync(50);

                        // Agora passamos a lista real de Gateways em vez do Count
                        string finalJson = $"{{\"gatewaysOnline\": [{gwList}], \"activeSensors\": {activeSensorsJson},  \"readings\": {jsonReadings}}}";

                        byte[] jsonBytes = System.Text.Encoding.UTF8.GetBytes(finalJson);
                        res.ContentType = "application/json";
                        res.ContentLength64 = jsonBytes.Length;
                        res.AppendHeader("Access-Control-Allow-Origin", "*");
                        await res.OutputStream.WriteAsync(jsonBytes, 0, jsonBytes.Length);
                    }
                    else if (req.Url.AbsolutePath.StartsWith("/stream/")) {
                        string sid = req.Url.AbsolutePath.Split('/').Last();
                        string html = $@"
                            <!DOCTYPE html>
                            <html><body style='background:#0a0a0a;color:#00ff00;text-align:center;'>
                                <h2>[SERVER] Live Feed: {sid}</h2>
                                <img id='feed' src='/image/{sid}' style='max-width:800px;border:2px solid #00ff00;' />
                                <script>setInterval(() => document.getElementById('feed').src = '/image/{sid}?t=' + Date.now(), 200);</script>
                            </body></html>";
                        byte[] buffer = System.Text.Encoding.UTF8.GetBytes(html);
                        res.ContentType = "text/html";
                        res.ContentLength64 = buffer.Length;
                        await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    }
                    else if (req.Url.AbsolutePath.StartsWith("/image/")) {
                        string sid = req.Url.AbsolutePath.Split('/').Last();
                        if (_latestFrames.TryGetValue(sid, out byte[] imgBytes)) {
                            res.ContentType = "image/jpeg";
                            res.ContentLength64 = imgBytes.Length;
                            await res.OutputStream.WriteAsync(imgBytes, 0, imgBytes.Length);
                        }
                        else { res.StatusCode = 404; }
                    }
                    else { res.StatusCode = 404; }
                } finally {
                    res.Close();
                }
            }
        } catch (Exception ex) {
            Console.WriteLine($"[WEB ERROR] {ex.Message}");
        }
    }
}