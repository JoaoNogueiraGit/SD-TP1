using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
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

                        Console.WriteLine($"\n   -> [BATCHING] Gateway {msg.GID} sent batch of data from Sensor: {msg.SID}");
                        Console.WriteLine($"   -> Zone: {msg.Data.GetValueOrDefault("ZONE", "N/A")} | Type: {msg.Data["TYPE"]}");
                        Console.WriteLine($"   -> Total of readings: {msg.Data["BATCH_COUNT"]}");

                        // Extract and convert JSON back to list
                        string rawPayloadJson = msg.Data["RAW_PAYLOAD"];
                        var rawReadings = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(rawPayloadJson);

                        if (rawReadings != null) {
                            foreach (var reading in rawReadings) {
                                // Clone message
                                var individualMsg = new Message {
                                    CMD = "FWD",
                                    SID = msg.SID,
                                    GID = msg.GID,
                                    Timestamp = reading["Timestamp"] // Restore original timestamp
                                };
                                individualMsg.Data["TYPE"] = msg.Data["TYPE"];
                                individualMsg.Data["ZONE"] = msg.Data["ZONE"];
                                individualMsg.Data["VALUE"] = reading["Value"];

                                await _dbManager.SaveReadingsAsync(individualMsg);
                            }
                        }
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

            // Se estivermos no Docker, usamos *, se estivermos no Windows local, usamos localhost
            if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true") {
                listener.Prefixes.Add("http://*:8081/");
            }
            else {
                listener.Prefixes.Add("http://localhost:8081/");
            }

            listener.Start();
            Console.WriteLine($"[WEB] Dashboard Ready: http://localhost:8081/");

            while (true) {
                var context = await listener.GetContextAsync();
                _ = Task.Run(() => HandleWebRequestAsync(context));
            }
        } catch (Exception ex) {
            Console.WriteLine($"[WEB ERROR] {ex.Message}");
        }
    }

    private static async Task HandleWebRequestAsync(HttpListenerContext context) {
        var req = context.Request;
        var res = context.Response;

        try {
            res.AppendHeader("Access-Control-Allow-Origin", "*");
            string path = req.Url.AbsolutePath;

            if (path == "/" || path == "/index.html") {
                string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "index.html");
                byte[] content = File.Exists(filePath) ? await File.ReadAllBytesAsync(filePath) : System.Text.Encoding.UTF8.GetBytes("<h1>index.html not found!</h1>");
                res.ContentType = "text/html";
                await res.OutputStream.WriteAsync(content, 0, content.Length);
            }
            else if (path == "/api/status") {
                var status = new {
                    gatewaysOnline = _activeGateways.Keys.ToList(),
                    activeSensors = _activeSensors.Select(s => new { Sensor = s.Key, Gateway = s.Value }).ToList(),
                    readings = JsonSerializer.Deserialize<JsonElement>(await _dbManager.GetRecentReadingsJsonAsync(50))
                };
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(status);
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(json, 0, json.Length);
            }
            else if (path == "/api/zones") {
                string json = await _dbManager.GetAvailableZonesJsonAsync();
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(json);
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            else if (path == "/api/types") {
                string json = await _dbManager.GetAvailableTypesJsonAsync();
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(json);
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            else if (path.StartsWith("/api/sensors")) {
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                string json = await _dbManager.GetAvailableSensorsJsonAsync(query["zone"], query["type"]);
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(json);
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            else if (path.StartsWith("/api/stats")) {
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                string json = await _dbManager.GetStatisticsJsonAsync(query["zone"], query["type"], query["sensor"]);
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(json);
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            else if (path.StartsWith("/stream/")) {
                string sid = path.Split('/').Last();
                string html = $"<html><body style='background:#000;color:#0f0;text-align:center;'><h2>Stream: {sid}</h2><img id='f' src='/image/{sid}' style='max-width:800px;'/><script>setInterval(()=>document.getElementById('f').src='/image/{sid}?'+Date.now(),200);</script></body></html>";
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(html);
                res.ContentType = "text/html";
                await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            else if (path.StartsWith("/image/")) {
                string sid = path.Split('/').Last();
                if (_latestFrames.TryGetValue(sid, out byte[] img)) {
                    res.ContentType = "image/jpeg";
                    await res.OutputStream.WriteAsync(img, 0, img.Length);
                }
                else { res.StatusCode = 404; }
            }
            else { res.StatusCode = 404; }
        } catch (Exception ex) {
            Console.WriteLine($"[WEB ERROR] Request: {ex.Message}");
        } finally {
            res.Close();
        }
    }
}
