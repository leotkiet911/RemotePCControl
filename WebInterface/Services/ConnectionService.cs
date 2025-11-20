using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using RemotePCControl.WebInterface.Hubs;
using System.Net.Sockets;
using System.Text;

namespace RemotePCControl.WebInterface.Services
{
    public class ConnectionService
    {
        private readonly IHubContext<ControlHub> _hubContext;
        private readonly ServerConnectionOptions _options;
        private readonly Dictionary<string, TcpClient> _connections = new Dictionary<string, TcpClient>();
        private readonly object _lock = new object();

        public ConnectionService(
            IHubContext<ControlHub> hubContext,
            IOptions<ServerConnectionOptions> options)
        {
            _hubContext = hubContext;
            _options = options.Value;

            if (string.IsNullOrWhiteSpace(_options.Host))
            {
                _options.Host = "127.0.0.1";
            }

            if (_options.Port <= 0 || _options.Port > 65535)
            {
                _options.Port = 8888;
            }
        }

        public async Task ProcessCommand(string connectionId, string command)
        {
            TcpClient? tcpClient;

            lock (_lock)
            {
                if (!_connections.TryGetValue(connectionId, out tcpClient))
                {
                    tcpClient = new TcpClient();
                    _connections[connectionId] = tcpClient;
                }
            }

            if (!tcpClient.Connected)
            {
                try
                {
                    await tcpClient.ConnectAsync(_options.Host, _options.Port);
                    Console.WriteLine($"[WEB] Connected to server {_options.Host}:{_options.Port} for client {connectionId}");

                    _ = Task.Run(() => ListenToServer(connectionId, tcpClient));
                }
                catch (Exception ex)
                {
                    await _hubContext.Clients.Client(connectionId)
                        .SendAsync("ReceiveResponse", $"ERROR|Cannot connect to server: {ex.Message}");
                    return;
                }
            }

            try
            {
                NetworkStream stream = tcpClient.GetStream();
                byte[] data = Encoding.UTF8.GetBytes(command);
                byte[] lengthPrefix = BitConverter.GetBytes(data.Length);

                await stream.WriteAsync(lengthPrefix, 0, lengthPrefix.Length);
                await stream.WriteAsync(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("ReceiveResponse", $"ERROR|Send failed: {ex.Message}");
            }
        }

        private async Task ListenToServer(string connectionId, TcpClient tcpClient)
        {
            NetworkStream stream = tcpClient.GetStream();
            byte[] lengthBuffer = new byte[4];

            try
            {
                while (tcpClient.Connected)
                {
                    int bytesRead = 0;
                    while (bytesRead < 4)
                    {
                        int read = await stream.ReadAsync(lengthBuffer, bytesRead, 4 - bytesRead);
                        if (read == 0) throw new Exception("Server disconnected");
                        bytesRead += read;
                    }

                    int messageLength = BitConverter.ToInt32(lengthBuffer, 0);

                    byte[] messageBuffer = new byte[messageLength];
                    bytesRead = 0;
                    while (bytesRead < messageLength)
                    {
                        int read = await stream.ReadAsync(messageBuffer, bytesRead, messageLength - bytesRead);
                        if (read == 0) throw new Exception("Server disconnected");
                        bytesRead += read;
                    }

                    string message = Encoding.UTF8.GetString(messageBuffer);

                    if (!message.Contains("WEBCAM_FRAME"))
                    {
                        Console.WriteLine($"[WEB→BROWSER] {message.Substring(0, Math.Min(100, message.Length))}");
                    }

                    await _hubContext.Clients.Client(connectionId)
                        .SendAsync("ReceiveResponse", message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Listen from server: {ex.Message}");
            }
            finally
            {
                CloseConnection(connectionId);
            }
        }

        public void CloseConnection(string connectionId)
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(connectionId, out var tcpClient))
                {
                    tcpClient?.Close();
                    _connections.Remove(connectionId);
                    Console.WriteLine($"[WEB] Connection closed: {connectionId}");
                }
            }
        }
    }
}