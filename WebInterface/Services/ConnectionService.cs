using Microsoft.AspNetCore.SignalR;
using RemotePCControl.WebInterface.Hubs;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace RemotePCControl.WebInterface.Services
{
    public class ConnectionService : IDisposable
    {
        // Dùng IHubContext để Hub có thể gửi tin nhắn BẤT KỲ LÚC NÀO
        private readonly IHubContext<ControlHub> _hubContext;

        // Dictionary để lưu trữ kết nối TCP của từng client (ConnectionId)
        private readonly ConcurrentDictionary<string, TcpClient> _connections = new();
        private readonly ConcurrentDictionary<string, NetworkStream> _streams = new();

        // Địa chỉ Server Console
        private const string SERVER_IP = "127.0.0.1";
        private const int SERVER_PORT = 8888; // Port của Server.cs

        public ConnectionService(IHubContext<ControlHub> hubContext)
        {
            _hubContext = hubContext;
            Console.WriteLine("[ConnectionService] Đã khởi tạo.");
        }

        // Khi client web (JS) gọi lệnh
        public async Task ProcessCommand(string connectionId, string message)
        {
            string[] parts = message.Split('|');
            string command = parts[0];

            // Nếu là lệnh LOGIN, chúng ta phải tạo kết nối TCP mới
            if (command == "LOGIN")
            {
                await CreateConnection(connectionId, message);
            }
            // Nếu là các lệnh khác, gửi qua kết nối TCP đã có
            else if (_streams.TryGetValue(connectionId, out var stream))
            {
                await SendTcpMessage(stream, message);
            }
        }

        // Tạo kết nối TCP mới đến Server Console
        private async Task CreateConnection(string connectionId, string loginMessage)
        {
            try
            {
                TcpClient client = new TcpClient();
                await client.ConnectAsync(SERVER_IP, SERVER_PORT);

                NetworkStream stream = client.GetStream();

                // Thêm vào Dictionary
                _connections[connectionId] = client;
                _streams[connectionId] = stream;

                // Gửi lệnh LOGIN (mà Server.cs của bạn đang mong đợi)
                await SendTcpMessage(stream, loginMessage);

                // Bắt đầu một Task riêng để lắng nghe phản hồi từ Server Console
                _ = ListenForResponses(connectionId, stream);

                Console.WriteLine($"[ConnectionService] Đã tạo kết nối TCP cho {connectionId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConnectionService] Lỗi khi tạo kết nối: {ex.Message}");
                // Gửi lỗi về JS
                await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveResponse", $"ERROR|Kết nối tới Server Console (port 8888) thất bại: {ex.Message}");
            }
        }

        // Vòng lặp lắng nghe phản hồi từ Server Console
        private async Task ListenForResponses(string connectionId, NetworkStream stream)
        {
            byte[] lengthBuffer = new byte[4]; // Buffer cho 4-byte độ dài
            try
            {
                while (true)
                {
                    // 1. Đọc 4-byte độ dài
                    int bytesRead = 0;
                    while (bytesRead < 4)
                    {
                        // Dùng ReadAsync thay vì Read
                        int read = await stream.ReadAsync(lengthBuffer, bytesRead, 4 - bytesRead);
                        if (read == 0) throw new Exception("Server disconnected");
                        bytesRead += read;
                    }
                    int messageLength = BitConverter.ToInt32(lengthBuffer, 0);

                    // 2. Đọc chính xác độ dài tin nhắn
                    byte[] messageBuffer = new byte[messageLength];
                    bytesRead = 0;
                    while (bytesRead < messageLength)
                    {
                        // Dùng ReadAsync thay vì Read
                        int read = await stream.ReadAsync(messageBuffer, bytesRead, messageLength - bytesRead);
                        if (read == 0) throw new Exception("Server disconnected");
                        bytesRead += read;
                    }

                    string response = Encoding.UTF8.GetString(messageBuffer, 0, messageLength);

                    // Lấy được phản hồi! Gửi nó về cho client JS
                    await _hubContext.Clients.Client(connectionId).SendAsync("ReceiveResponse", response);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConnectionService] Lỗi khi nghe: {ex.Message}");
            }
            finally
            {
                // Dọn dẹp khi ngắt kết nối
                CloseConnection(connectionId);
            }
        }

        // Gửi tin nhắn qua TCP
        private async Task SendTcpMessage(NetworkStream stream, string message)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(message);
                byte[] lengthPrefix = BitConverter.GetBytes(data.Length); // Lấy 4-byte độ dài

                // 1. Gửi độ dài
                await stream.WriteAsync(lengthPrefix, 0, lengthPrefix.Length);
                // 2. Gửi dữ liệu
                await stream.WriteAsync(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConnectionService] Lỗi khi gửi: {ex.Message}");
            }
        }

        // Đóng và dọn dẹp kết nối khi client JS ngắt kết nối
        public void CloseConnection(string connectionId)
        {
            if (_streams.TryRemove(connectionId, out var stream))
            {
                stream.Close();
            }
            if (_connections.TryRemove(connectionId, out var client))
            {
                client.Close();
            }
            Console.WriteLine($"[ConnectionService] Đã đóng kết nối TCP cho {connectionId}");
        }

        public void Dispose()
        {
            foreach (var id in _connections.Keys)
            {
                CloseConnection(id);
            }
        }
    }
}