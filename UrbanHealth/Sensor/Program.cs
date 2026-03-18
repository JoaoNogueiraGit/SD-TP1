using System.Net.Sockets;
using Shared;

class Program {
    static async Task Main(string[] args) {
        Console.WriteLine("A iniciar o Sensor Falso...");
        await Task.Delay(2000); // Dá tempo para tu leres a consola

        using var client = new TcpClient();
        try {
            // Liga ao Gateway local
            await client.ConnectAsync("127.0.0.1", 5000);
            Console.WriteLine("[SENSOR] Ligado ao Gateway!");

            // 1. TESTAR A AUTENTICAÇÃO (CONN)
            var connMsg = new Message { CMD = "CONN", SID = "S101" };
            await Message.SendMessageAsync(client, connMsg);
            Console.WriteLine("[SENSOR] Enviei CONN. À espera de resposta...");

            var response = await Message.ReceiveMessageAsync(client);
            Console.WriteLine($"[SENSOR] Gateway respondeu: {response.Data["TYPE"]}");

            // 2. TESTAR O ENVIO DE DADOS (DATA)
            if (response.Data["TYPE"] == "ACK") {
                await Task.Delay(1000); // Finge que está a ler um termómetro
                var dataMsg = new Message { CMD = "DATA", SID = "S101" };
                dataMsg.Data["TYPE"] = "TEMP";
                dataMsg.Data["VALUE"] = "24.5";

                await Message.SendMessageAsync(client, dataMsg);
                Console.WriteLine("[SENSOR] Enviei DATA (24.5 graus).");
            }

            // Mantém a consola aberta uns segundos para veres o Gateway a trabalhar
            await Task.Delay(5000);

        } catch (Exception ex) {
            Console.WriteLine($"[ERRO DE TESTE] {ex.Message}");
        }
    }
}