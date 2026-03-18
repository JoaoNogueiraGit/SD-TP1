using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Shared;

class Gateway {

    private static TcpListener _listener;
    private static TcpClient _client;
    private const int Port = 5000;
    private const string ServerIP = "127.0.0.1";
    private const int ServerPort = 5001;

    static async Task Main(string[] args) {

        await ConnectToServerAsync();

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

    // Establish TCP connection between the gateway and the server
    private static async Task ConnectToServerAsync()
    {
        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync(ServerIP, ServerPort);
            Console.WriteLine("[GATEWAY] Connected to the server.");

            // Task to Listen to the Server (SERVER ---MSG---> GATEWAY)
            _ = Task.Run(() => ListenToServerAsync(_client));

            var sts = new Message
            {
                CMD = "STS",
                GID = "G101",
            };
            sts.Data["STATUS"] = "ONLINE";
            await Message.SendMessageAsync(_client, sts);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GATEWAY] Could not estabilish connection with server: {ex.Message}");
        }

    }

    
    private static async Task ListenToServerAsync(TcpClient server)
    {
        while (true)
        {
            var msg = await Message.ReceiveMessageAsync(server);
            if (msg == null) break;

            Console.WriteLine($"[SERVER -> GATEWAY]: Command received: {msg.CMD}");
            await ProcessMessage(server, msg);
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