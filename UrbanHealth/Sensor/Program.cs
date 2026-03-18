using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Shared; 

class Program {
    static async Task Main(string[] args) {
        Console.WriteLine("Starting Mock Sensor...");
        await Task.Delay(2000); // Gives you time to read the console

        using var client = new TcpClient();
        try {
            // Connect to local Gateway
            await client.ConnectAsync("127.0.0.1", 5000);
            Console.WriteLine("[SENSOR] Connected to Gateway!");

            // TEST AUTHENTICATION (CONN)
            var connMsg = new Message { CMD = "CONN", SID = "S101" };
            await Message.SendMessageAsync(client, connMsg);
            Console.WriteLine("[SENSOR] Sent CONN. Waiting for response...");

            var response = await Message.ReceiveMessageAsync(client);
            Console.WriteLine($"[SENSOR] Gateway responded: {response.Data["TYPE"]}");

            // TEST DATA TRANSMISSION (DATA)
            if (response.Data["TYPE"] == "ACK") {
                await Task.Delay(1000); // Pretends to read a thermometer

                var dataMsg = new Message { CMD = "DATA", SID = "S101" };
                dataMsg.Data["TYPE"] = "TEMP";
                dataMsg.Data["VALUE"] = "24.5";

                await Message.SendMessageAsync(client, dataMsg);
                Console.WriteLine("[SENSOR] Sent DATA (24.5 degrees).");
            }

            // Keeps the console open for a few seconds to see the Gateway working
            await Task.Delay(5000);

        } catch (Exception ex) {
            Console.WriteLine($"[TEST ERROR] {ex.Message}");
        }
    }
}