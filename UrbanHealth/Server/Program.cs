using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Server;
using Shared; // Your common library

class Program {
    //private static StorageManager _storage = new StorageManager();
    private static DataBaseManager _dbManager = new DataBaseManager();
    static async Task Main(string[] args) {
        int port = 5001;
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        Console.WriteLine($"[CENTRAL SERVER] Listening on port {port}");

        while (true) {
            // Waits for a Gateway to connect
            var client = await listener.AcceptTcpClientAsync();
            Console.WriteLine("[SERVER] New connection detected (Gateway)!");

            _ = Task.Run(() => HandleGatewayAsync(client));
        }
    }

    private static async Task HandleGatewayAsync(TcpClient client) {
        using (client) {
            try {
                while (true) {
                    var msg = await Message.ReceiveMessageAsync(client);

                    if (msg == null) {
                        Console.WriteLine("[SERVER] The Gateway disconnected.");
                        break;
                    }

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
                }
            } catch (Exception ex) {
                Console.WriteLine($"[SERVER ERROR] {ex.Message}");
            }
        }
    }
}