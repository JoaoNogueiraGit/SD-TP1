using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Shared;

class StressTest
{
    private const string GatewayIP = "127.0.0.1";
    private const int GatewayPort = 5000;
    private const int NumberOfSensors = 10; // Quantos sensores simular ao mesmo tempo

    static async Task Main(string[] args)
    {
        Console.WriteLine($"[TEST] A iniciar teste de concorrência com {NumberOfSensors} sensores...");

        List<Task> sensorTasks = new List<Task>();

        for (int i = 1; i <= NumberOfSensors; i++)
        {
            string sid = $"S10{i}"; // Simula S101, S102, S103...
            sensorTasks.Add(Task.Run(() => SimulateSensor(sid)));
        }

        // Espera que todos os sensores terminem
        await Task.WhenAll(sensorTasks);
        Console.WriteLine("\n[TEST] Teste concluído. Verifica os logs no Gateway e Servidor.");
    }

    private static async Task SimulateSensor(string sid)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(GatewayIP, GatewayPort);

            // Enviar CONN
            var conn = new Message { CMD = "CONN", SID = sid };
            await Message.SendMessageAsync(client, conn);
            var res = await Message.ReceiveMessageAsync(client);

            if (res?.Data["TYPE"] == "ACK")
            {
                // Enviar 5 rajadas de dados rapidamente para forçar concorrência de escrita
                for (int j = 0; j < 5; j++)
                {
                    var data = new Message { CMD = "DATA", SID = sid };
                    data.Data["TYPE"] = "TEMP";
                    data.Data["VALUE"] = new Random().Next(15, 30).ToString();

                    await Message.SendMessageAsync(client, data);
                    Console.WriteLine($"[Sensor {sid}] Enviou valor {data.Data["VALUE"]}");

                    await Task.Delay(100); // Pequeno delay para não entupir o buffer local
                }
            }
            else
            {
                Console.WriteLine($"[Sensor {sid}] Erro na autenticação (Pode não estar no CSV).");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sensor {sid}] Falhou: {ex.Message}");
        }
    }
}