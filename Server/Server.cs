using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Linq;

namespace RemotePCControl
{
    public class Server
    {
        private TcpListener listener;
        private List<ConnectedClient> clients = new List<ConnectedClient>();
        private Dictionary<string, ClientSession> sessions = new Dictionary<string, ClientSession>();
        private readonly object lockObj = new object();
        private bool isRunning = false;

        public void Start(int port = 8888)
        {
            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                isRunning = true;

                Console.WriteLine($"[SERVER] Started on port {port}");
                Console.WriteLine($"[SERVER] Waiting for connections...");

                Thread acceptThread = new Thread(AcceptClients);
                acceptThread.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to start server: {ex.Message}");
            }
        }

        private void AcceptClients()
        {
            while (isRunning)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    Thread clientThread = new Thread(() => HandleClient(client));
                    clientThread.Start();
                }
                catch (Exception ex)
                {
                    if (isRunning)
                        Console.WriteLine($"[ERROR] Accept client failed: {ex.Message}");
                }
            }
        }

        private void HandleClient(TcpClient tcpClient)
        {
            ConnectedClient client = new ConnectedClient(tcpClient);
            lock (lockObj) { clients.Add(client); }
            Console.WriteLine($"[SERVER] New client connected from {client.RemoteEndPoint}");

            try
            {
                NetworkStream stream = tcpClient.GetStream();
                byte[] lengthBuffer = new byte[4]; // Buffer cho 4-byte độ dài

                while (tcpClient.Connected)
                {
                    // 1. Đọc 4-byte độ dài
                    int bytesRead = 0;
                    while (bytesRead < 4)
                    {
                        int read = stream.Read(lengthBuffer, bytesRead, 4 - bytesRead);
                        if (read == 0) throw new Exception("Client disconnected");
                        bytesRead += read;
                    }

                    int messageLength = BitConverter.ToInt32(lengthBuffer, 0);

                    // 2. Đọc chính xác độ dài tin nhắn
                    byte[] messageBuffer = new byte[messageLength];
                    bytesRead = 0;
                    while (bytesRead < messageLength)
                    {
                        int read = stream.Read(messageBuffer, bytesRead, messageLength - bytesRead);
                        if (read == 0) throw new Exception("Client disconnected");
                        bytesRead += read;
                    }

                    string message = Encoding.UTF8.GetString(messageBuffer, 0, messageLength);

                    // Chỉ in 100 ký tự đầu tiên để tránh làm treo console
                    Console.WriteLine($"[RECEIVED] {message.Substring(0, Math.Min(message.Length, 100))}");

                    ProcessMessage(client, message, stream);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Client handler: {ex.Message}");
            }
            finally
            {
                lock (lockObj)
                {
                    clients.Remove(client);
                    // Remove session if this was controlled PC
                    var sessionToRemove = sessions.FirstOrDefault(s => s.Value.ControlledClient == client);
                    if (!sessionToRemove.Equals(default(KeyValuePair<string, ClientSession>)))
                    {
                        sessions.Remove(sessionToRemove.Key);
                        Console.WriteLine($"[SESSION] Removed session for {sessionToRemove.Key}");
                    }
                }
                tcpClient.Close();
                Console.WriteLine($"[SERVER] Client disconnected");
            }
        }

        private void ProcessMessage(ConnectedClient client, string message, NetworkStream stream)
        {
            try
            {
                string[] parts = message.Split('|');
                string command = parts[0];

                switch (command)
                {
                    case "REGISTER_CONTROLLED":
                        // Format: REGISTER_CONTROLLED|IP|PASSWORD
                        if (parts.Length >= 3)
                        {
                            string ip = parts[1];
                            string password = parts[2];

                            lock (lockObj)
                            {
                                sessions[ip] = new ClientSession
                                {
                                    Password = password,
                                    ControlledClient = client
                                };
                            }

                            SendResponse(stream, "REGISTERED|SUCCESS");
                            Console.WriteLine($"[SESSION] Registered controlled PC: {ip} with password: {password}");
                        }
                        break;

                    case "LOGIN":
                        // Format: LOGIN|IP|PASSWORD
                        if (parts.Length >= 3)
                        {
                            string ip = parts[1];
                            string password = parts[2];

                            lock (lockObj)
                            {
                                if (sessions.ContainsKey(ip) && sessions[ip].Password == password)
                                {
                                    sessions[ip].ControllerClient = client;
                                    SendResponse(stream, "LOGIN|SUCCESS");
                                    Console.WriteLine($"[SESSION] Controller logged in to {ip}");
                                }
                                else
                                {
                                    SendResponse(stream, "LOGIN|FAILED");
                                    Console.WriteLine($"[SESSION] Login failed for {ip}");
                                }
                            }
                        }
                        break;

                    case "COMMAND":
                        // Format: COMMAND|TARGET_IP|ACTUAL_COMMAND|PARAMS
                        if (parts.Length >= 3)
                        {
                            string targetIp = parts[1];
                            string actualCommand = parts[2];
                            string parameters = parts.Length > 3 ? string.Join("|", parts.Skip(3)) : "";

                            lock (lockObj)
                            {
                                if (sessions.ContainsKey(targetIp) && sessions[targetIp].ControlledClient != null)
                                {
                                    var targetClient = sessions[targetIp].ControlledClient;
                                    ForwardCommand(targetClient, actualCommand, parameters);
                                    Console.WriteLine($"[FORWARD] Command {actualCommand} forwarded to {targetIp}");
                                }
                            }
                        }
                        break;

                    case "RESPONSE":
                        // Format: RESPONSE|SOURCE_IP|DATA
                        if (parts.Length >= 3)
                        {
                            string sourceIp = parts[1];
                            string data = string.Join("|", parts.Skip(2));

                            lock (lockObj)
                            {
                                if (sessions.ContainsKey(sourceIp) && sessions[sourceIp].ControllerClient != null)
                                {
                                    var controllerClient = sessions[sourceIp].ControllerClient;
                                    SendResponse(controllerClient.TcpClient.GetStream(), $"RESPONSE|{data}");
                                    Console.WriteLine($"[FORWARD] Response forwarded from {sourceIp}");
                                }
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Process message: {ex.Message}");
            }
        }

        private void ForwardCommand(ConnectedClient targetClient, string command, string parameters)
        {
            try
            {
                NetworkStream stream = targetClient.TcpClient.GetStream();
                string message = $"EXECUTE|{command}|{parameters}";

                byte[] data = Encoding.UTF8.GetBytes(message);
                byte[] lengthPrefix = BitConverter.GetBytes(data.Length); // Lấy 4-byte độ dài

                // 1. Gửi độ dài
                stream.Write(lengthPrefix, 0, lengthPrefix.Length);
                // 2. Gửi dữ liệu
                stream.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Forward command: {ex.Message}");
            }
        }

        private void SendResponse(NetworkStream stream, string response)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(response);
                byte[] lengthPrefix = BitConverter.GetBytes(data.Length); // Lấy 4-byte độ dài

                // 1. Gửi độ dài
                stream.Write(lengthPrefix, 0, lengthPrefix.Length);
                // 2. Gửi dữ liệu
                stream.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Send response: {ex.Message}");
            }
        }
        public void Stop()
        {
            isRunning = false;
            listener?.Stop();

            lock (lockObj)
            {
                foreach (var client in clients)
                {
                    client.TcpClient.Close();
                }
                clients.Clear();
                sessions.Clear();
            }
        }
    }

    public class ConnectedClient
    {
        public TcpClient TcpClient { get; set; }
        public string RemoteEndPoint { get; set; }

        public ConnectedClient(TcpClient client)
        {
            TcpClient = client;
            RemoteEndPoint = client.Client.RemoteEndPoint.ToString();
        }
    }

    public class ClientSession
    {
        public string Password { get; set; }
        public ConnectedClient ControlledClient { get; set; }
        public ConnectedClient ControllerClient { get; set; }
    }

    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== REMOTE PC CONTROL SERVER ===");
            Console.WriteLine("HCMUS - Socket Programming Project");
            Console.WriteLine("================================\n");

            Server server = new Server();
            server.Start(8888);

            Console.WriteLine("\nPress any key to stop the server...");
            Console.ReadKey();

            server.Stop();
            Console.WriteLine("\nServer stopped.");
        }
    }
}