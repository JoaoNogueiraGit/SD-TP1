using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Server;
using Shared; // Your common library

class Program {
    //private static StorageManager _storage = new StorageManager();
    private static DataBaseManager _dbManager = new DataBaseManager();

    // Registry of active gateways (GID -> Last Sync)
    private static ConcurrentDictionary<string, DateTime> _activeGateways = new();
    static async Task Main(string[] args) {
        int port = 5001;
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        Console.WriteLine($"[SERVER] Listening on port {port}");

        // starts cleaning routine of dead gateways
        _ = Task.Run(MonitorGatewaysAsync);

        while (true) {
            // Waits for a Gateway to connect
            var client = await listener.AcceptTcpClientAsync();
            Console.WriteLine("[SERVER] New connection detected (Gateway)!");

            _ = Task.Run(() => HandleGatewayAsync(client));
        }
    }

    private static async Task MonitorGatewaysAsync() {

        while (true) {

            await Task.Delay(15000); // every 15 sec

            foreach (var gw in _activeGateways) {

                // if more than 30 secs passed since last HB
                if ((DateTime.Now - gw.Value).TotalSeconds > 30) {
                    Console.WriteLine($"[TIMEOUT] Gateway {gw.Key} lost connection. ");
                    _activeGateways.TryRemove(gw.Key, out _);

                    // we can implement some kind of external "alert" here
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

                    // Visual treatment for the two commands we expect from the Gateway
                    if (msg.CMD == "STS") {
                        Console.WriteLine($"   -> Gateway Status: {msg.Data["STATUS"]}");
                    }
                    else if (msg.CMD == "FWD") {
                        Console.WriteLine($"   -> Forwarded Data from Sensor: {msg.SID}");
                        Console.WriteLine($"   -> Injected Zone: {msg.Data["ZONE"]}");
                        Console.WriteLine($"   -> Reading: {msg.Data["TYPE"]} = {msg.Data["VALUE"]}");

                        //await _storage.Savemessage(msg);
                        await _dbManager.SaveReadingsAsync(msg);
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
}