using System.Net;
using System.Net.Sockets;
using Shared;

class Gateway {

    private static TcpListener _listener;
    private const int Port = 5000;

    static async Task Main(string[] args) {

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

                    if (msg == null) break; // sensor disconnected

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
        // Ex for CONN

        if (msg.CMD == "CONN") {

            // TODO: Validade sensor in CSV
            var ack = new Message { CMD = "MSG", SID = msg.SID };
            ack.Data["TYPE"] = "ACK";
            ack.Data["REF_CMD"] = "CONN";
            await Message.SendMessageAsync(client, ack);
        }
    }
}