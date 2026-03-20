using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Shared {
    public class Message {
        // Base header
        public string CMD { get; set; }
        public string SID { get; set; }
        public string GID { get; set; }
        public string Timestamp { get; set; }

        // Dictionary for variable fields
        // Ex: "VALUE", "ZONE", "TYPE", "DATA_TYPES", etc...
        public Dictionary<string, string> Data { get; set; } = new Dictionary<string, string>();

        // For video (binary)
        public byte[] BinaryData { get; set; }

        public Message() {
            Timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        }

        // Transforms object to string
        public override string ToString() {
            var sb = new StringBuilder();
            sb.AppendLine($"CMD:{CMD}");
            sb.AppendLine($"SID:{SID}");
            if (!string.IsNullOrEmpty(GID)) sb.AppendLine($"GID:{GID}");
            sb.AppendLine($"TIMESTAMP:{Timestamp}");

            foreach (var entry in Data) {
                sb.AppendLine($"{entry.Key}:{entry.Value}");
            }

            sb.Append("<EOF>"); // terminator of header
            return sb.ToString();
        }

        // Static factory method to create a message from a raw string
        public static Message Parse(string rawMessage) {
            var msg = new Message();

            string cleanMessage = rawMessage.Replace("<EOF>", "").Trim();
            // Remove o marcador de fim e separa por linhas
            var lines = cleanMessage.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines) {
                var parts = line.Split(':', 2);
                if (parts.Length < 2) continue;

                var key = parts[0].Trim();
                var value = parts[1].Trim();

                switch (key) {
                    case "CMD": msg.CMD = value; break;
                    case "SID": msg.SID = value; break;
                    case "GID": msg.GID = value; break;
                    case "TIMESTAMP": msg.Timestamp = value; break;
                    default:
                        msg.Data[key] = value;
                        break;
                }
            }
            return msg;
        }

        public static async Task SendMessageAsync(TcpClient client, Message msg) {

            var data = Encoding.UTF8.GetBytes(msg.ToString());
            await client.GetStream().WriteAsync(data, 0, data.Length);
        }

        public static async Task<Message> ReceiveMessageAsync(TcpClient client) {

            var stream = client.GetStream();
            var buffer = new byte[1024];
            var messageBuilder = new StringBuilder();

            try {

                while (!messageBuilder.ToString().Contains("<EOF>")) {

                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);

                    // if bytesRead is 0, then client disconnected
                    if (bytesRead == 0) return null;

                    var part = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    messageBuilder.Append(part);

                }

                // when out of the loop, we have the complete message
                string fullRawMessage = messageBuilder.ToString();

                return Message.Parse(fullRawMessage);

            } catch (Exception ex) {

                Console.WriteLine($"Error receiveing message: {ex.Message}");
                return null;
            }
        }

        // Pack the message for UDP (Text Header + Binary Payload)
        public byte[] ToUdpBytes() {
            string header = this.ToString(); // Already includes <EOF>
            byte[] headerBytes = Encoding.UTF8.GetBytes(header);

            if (BinaryData == null) return headerBytes;

            byte[] fullPacket = new byte[headerBytes.Length + BinaryData.Length];
            Buffer.BlockCopy(headerBytes, 0, fullPacket, 0, headerBytes.Length);
            Buffer.BlockCopy(BinaryData, 0, fullPacket, headerBytes.Length, BinaryData.Length);
            return fullPacket;
        }

        // Unpack UDP bytes back into a Message object
        public static Message FromUdpBytes(byte[] packet) {
            string fullData = Encoding.UTF8.GetString(packet);
            int eofIndex = fullData.IndexOf("<EOF>");

            if (eofIndex == -1) return null;

            // Extract header
            string headerText = fullData.Substring(0, eofIndex + 5);
            Message msg = Message.Parse(headerText);

            // Extract binary data if there's anything after <EOF>
            int headerSize = Encoding.UTF8.GetByteCount(headerText);
            if (packet.Length > headerSize) {
                msg.BinaryData = new byte[packet.Length - headerSize];
                Buffer.BlockCopy(packet, headerSize, msg.BinaryData, 0, msg.BinaryData.Length);
            }
            return msg;
        }
    }
}