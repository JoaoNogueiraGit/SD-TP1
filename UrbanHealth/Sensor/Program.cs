using System;
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
        Console.WriteLine(" -> DISCONN (to gracefully shutdown)");
        Console.WriteLine("==================================================\n");

        // start parallel routines (will only send if authenticated)
        _ = Task.Run(HeartbeatRoutineAsync);
        // _ = Task.Run(DataGenerationRoutineAsync);
        _ = Task.Run(ConnectToGatewayLoopAsync);


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
                }
                else {
                    Console.WriteLine("[SENSOR] Shutting down (was not connected).");
                }
                break; // Exits the while loop and closes the app
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
                Console.WriteLine("[ERROR] Unknown command. Use DATA or DISCONN.");
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
            await Task.Delay(5000);
        }
    }

    private static async Task ListenToGatewayAsync(TcpClient gateway) {

        try { 
            while (true) {

                var msg = await Message.ReceiveMessageAsync(gateway);
                if (msg == null) break; // Gateway closed the connection

                string msgType = msg.Data.ContainsKey("TYPE") ? msg.Data["TYPE"] : "N/A";
                Console.WriteLine($"[GATEWAY -> SENSOR] Command received: {msg.CMD}; Type: {msgType}");

                if (msg.CMD == "MSG" && msg.Data.ContainsKey("TYPE")) {
                    
                    if (msg.Data["TYPE"] == "ACK" && msg.Data["REF_CMD"] == "CONN") {
                        _isAuthenticated = true;

                        if (msg.Data.ContainsKey("ZONE")) _zone = msg.Data["ZONE"];
                        Console.WriteLine($"[AUTH] Success! Operating in zone: {_zone}");
                    } else if (msg.Data["TYPE"] == "ERR") {
                        Console.WriteLine("[AUTH] Sensor got rejected by gateway.");
                        break;
                    }
                }
            }
        } catch { }
        Console.WriteLine("[SENSOR] Connection to Gateway lost!");
        _isAuthenticated = false;
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
                }
            }
        }
    }

    private static async Task DataGenerationRoutineAsync() {
        Random rnd = new Random();

        while (true) {
            await Task.Delay(7000); // Generate data every 7 seconds

            if (_isAuthenticated && _gatewayClient != null && _gatewayClient.Connected) {
                try {
                    var dataMsg = new Message { CMD = "DATA", SID = SID };
                    dataMsg.Data["TYPE"] = "TEMP";

                    // Generate random temperature between 15.0 and 30.0
                    double temp = 15.0 + (rnd.NextDouble() * 15.0);
                    dataMsg.Data["VALUE"] = temp.ToString("0.0");

                    await Message.SendMessageAsync(_gatewayClient, dataMsg);
                    Console.WriteLine($"[DATA] Sent TEMP: {dataMsg.Data["VALUE"]}°C");
                } catch {
                    _isAuthenticated = false;
                }
            }
        }
    }
}