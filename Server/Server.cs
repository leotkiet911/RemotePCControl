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
        private Dictionary<string, StreamStats> streamStats = new Dictionary<string, StreamStats>();

        // ==================== UDP STREAMING (NETWORK LAYER) ====================
        // UDP listener used to receive webcam/screen frames and ping packets from ClientControlled
        private UdpClient udpStreamListener;
        private const int UdpStreamPort = 9999;

        // Aggregate packets by frame_id for each client (key = registered client IP)
        private readonly Dictionary<string, Dictionary<int, UdpFrameBuffer>> udpFrameBuffers
            = new Dictionary<string, Dictionary<int, UdpFrameBuffer>>();

        // Store the latest UDP endpoint for each logical client IP so we can send ping packets back
        private readonly Dictionary<string, IPEndPoint> udpClientEndpoints
            = new Dictionary<string, IPEndPoint>();

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
                acceptThread.IsBackground = true;
                acceptThread.Start();

                udpStreamListener = new UdpClient(UdpStreamPort);
                Console.WriteLine($"[SERVER][UDP] Listening for stream packets on UDP port {UdpStreamPort}");

                _ = Task.Run(UdpReceiveLoop);
                _ = Task.Run(UdpPingLoop);

                Thread statsThread = new Thread(DisplayStats);
                statsThread.IsBackground = true;
                statsThread.Start();
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
                    clientThread.IsBackground = true;
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
                byte[] lengthBuffer = new byte[4];

                while (tcpClient.Connected)
                {
                    int bytesRead = 0;
                    while (bytesRead < 4)
                    {
                        int read = stream.Read(lengthBuffer, bytesRead, 4 - bytesRead);
                        if (read == 0) throw new Exception("Client disconnected");
                        bytesRead += read;
                    }

                    int messageLength = BitConverter.ToInt32(lengthBuffer, 0);

                    byte[] messageBuffer = new byte[messageLength];
                    bytesRead = 0;
                    while (bytesRead < messageLength)
                    {
                        int read = stream.Read(messageBuffer, bytesRead, messageLength - bytesRead);
                        if (read == 0) throw new Exception("Client disconnected");
                        bytesRead += read;
                    }

                    string message = Encoding.UTF8.GetString(messageBuffer, 0, messageLength);

                    if (message.StartsWith("RESPONSE") && message.Contains("WEBCAM_FRAME"))
                    {
                        ProcessMessage(client, message, stream, true);
                    }
                    else
                    {
                        Console.WriteLine($"[RECEIVED] {message.Substring(0, Math.Min(message.Length, 100))}");
                        ProcessMessage(client, message, stream, false);
                    }
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
                    var sessionToRemove = sessions.FirstOrDefault(s => s.Value.ControlledClient == client);
                    if (!sessionToRemove.Equals(default(KeyValuePair<string, ClientSession>)))
                    {
                        string ip = sessionToRemove.Key;
                        sessions.Remove(ip);
                        streamStats.Remove(ip);
                        Console.WriteLine($"[SESSION] Removed session for {ip}");
                    }
                }
                tcpClient.Close();
                Console.WriteLine($"[SERVER] Client disconnected");
            }
        }

        private void ProcessMessage(ConnectedClient client, string message, NetworkStream stream, bool isWebcamFrame)
        {
            try
            {
                string[] parts = message.Split('|');
                string command = parts[0];

                switch (command)
                {
                    case "LIST_SESSIONS":
                        lock (lockObj)
                        {
                            var list = sessions.Select(s =>
                            {
                                string status = s.Value.ControllerClient != null ? "IN_USE" : "WAITING";
                                return $"{s.Key}:{s.Value.Password}:{status}";
                            }).ToList();
                            SendResponse(stream, $"SESSIONS|{string.Join("||", list)}");
                        }
                        break;

                    case "REGISTER_CONTROLLED":
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

                                streamStats[ip] = new StreamStats
                                {
                                    IP = ip,
                                    IsStreaming = false
                                };
                            }

                            SendResponse(stream, "REGISTERED|SUCCESS");
                            Console.WriteLine($"[SESSION] Registered controlled PC: {ip} with password: {password}");
                        }
                        break;

                    case "LOGIN":
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
                        if (parts.Length >= 3)
                        {
                            string targetIp = parts[1];
                            string actualCommand = parts[2];
                            string parameters = parts.Length > 3 ? string.Join("|", parts.Skip(3)) : "";

                            lock (lockObj)
                            {
                                if (sessions.ContainsKey(targetIp))
                                {
                                    sessions[targetIp].ControllerClient = client;
                                    Console.WriteLine($"[SERVER] Set ControllerClient for session {targetIp}");
                                }
                                else
                                {
                                    Console.WriteLine($"[SERVER] Warning: Session {targetIp} not found when processing COMMAND");
                                }

                                if (sessions.ContainsKey(targetIp) && sessions[targetIp].ControlledClient != null)
                                {
                                    var targetClient = sessions[targetIp].ControlledClient;
                                    ForwardCommand(targetClient, actualCommand, parameters);

                                    if (actualCommand == "WEBCAM_STREAM_START" && streamStats.ContainsKey(targetIp))
                                    {
                                        streamStats[targetIp].IsStreaming = true;
                                        streamStats[targetIp].StartTime = DateTime.Now;
                                        streamStats[targetIp].FramesForwarded = 0;
                                        Console.WriteLine($"[WEBCAM] Stream started for {targetIp}");
                                    }
                                    else if (actualCommand == "WEBCAM_STREAM_STOP" && streamStats.ContainsKey(targetIp))
                                    {
                                        streamStats[targetIp].IsStreaming = false;
                                        Console.WriteLine($"[WEBCAM] Stream stopped for {targetIp}");
                                    }
                                    else if (actualCommand == "WEBCAM_ON")
                                    {
                                        if (!streamStats.ContainsKey(targetIp))
                                        {
                                            streamStats[targetIp] = new StreamStats();
                                        }
                                        streamStats[targetIp].IsStreaming = true;
                                        streamStats[targetIp].StartTime = DateTime.Now;
                                        Console.WriteLine($"[WEBCAM] Webcam ON for {targetIp}, streaming auto-started");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[FORWARD] Command {actualCommand} forwarded to {targetIp}");
                                    }
                                }
                            }
                        }
                        break;

                    case "RESPONSE":
                        if (parts.Length >= 3)
                        {
                            string sourceIp = parts[1];
                            string responseType = parts[2];

                            lock (lockObj)
                            {
                                if (sessions.ContainsKey(sourceIp) && sessions[sourceIp].ControllerClient != null)
                                {
                                    var controllerClient = sessions[sourceIp].ControllerClient;
                                    string data = string.Join("|", parts.Skip(2));
                                    SendResponse(controllerClient.TcpClient.GetStream(), $"RESPONSE|{data}");

                                    if (responseType == "WEBCAM_FRAME" && streamStats.ContainsKey(sourceIp))
                                    {
                                        streamStats[sourceIp].FramesForwarded++;
                                        streamStats[sourceIp].LastFrameTime = DateTime.Now;
                                    }
                                    else if (!isWebcamFrame)
                                    {
                                        Console.WriteLine($"[FORWARD] Response {responseType} forwarded from {sourceIp}");
                                    }
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
                byte[] lengthPrefix = BitConverter.GetBytes(data.Length);

                stream.Write(lengthPrefix, 0, lengthPrefix.Length);
                stream.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Forward command: {ex.Message}");
            }
        }

        // ==================== UDP STREAMING IMPLEMENTATION ====================

        /// <summary>
        /// UDP receive loop: groups packets by frame_id and handles ping responses.
        /// Packet header (24 bytes) must match ClientControlled:
        /// 0 - 3   : frame_id      (Int32)
        /// 4 - 5   : packet_index  (Int16)
        /// 6 - 7   : total_packets (Int16)
        /// 8 - 15  : timestamp_ms  (Int64, Unix time ms when frame was created)
        /// 16      : packet_type   (byte) 0 = frame data, 1 = PING, 2 = PONG
        /// 17 - 23 : reserved      (7 bytes)
        /// </summary>
        private async Task UdpReceiveLoop()
        {
            if (udpStreamListener == null) return;

            Console.WriteLine("[SERVER][UDP] UdpReceiveLoop started");

            while (isRunning)
            {
                try
                {
                    UdpReceiveResult result = await udpStreamListener.ReceiveAsync();
                    byte[] buffer = result.Buffer;

                    if (buffer.Length < 24)
                        continue;

                    int frameId = BitConverter.ToInt32(buffer, 0);
                    short packetIndex = BitConverter.ToInt16(buffer, 4);
                    short totalPackets = BitConverter.ToInt16(buffer, 6);
                    long timestampMs = BitConverter.ToInt64(buffer, 8);
                    byte packetType = buffer[16];

                    string remoteIp = result.RemoteEndPoint.Address.ToString();

                    if (packetType == 0)
                    {
                        HandleUdpFramePacket(remoteIp, result.RemoteEndPoint, frameId, packetIndex, totalPackets, timestampMs, buffer, 24);
                    }
                    else if (packetType == 2)
                    {
                        HandleUdpPong(remoteIp, frameId, timestampMs);
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR][UDP] ReceiveLoop: {ex.Message}");
                    await Task.Delay(50);
                }
            }

            Console.WriteLine("[SERVER][UDP] UdpReceiveLoop stopped");
        }

        /// <summary>
        /// Handle a packet that carries frame data (packet_type = 0).
        /// Group packets by frame_id; when a frame has all packets, assemble JPEG
        /// and forward it to the controller over TCP/SignalR as WEBCAM_FRAME.
        /// </summary>
        private void HandleUdpFramePacket(string remoteIp, IPEndPoint remoteEndPoint, int frameId, short packetIndex, short totalPackets, long timestampMs, byte[] buffer, int payloadOffset)
        {
            if (packetIndex < 0 || packetIndex >= totalPackets || totalPackets <= 0)
                return;

            string? logicalIp = null;

            lock (lockObj)
            {
                foreach (var session in sessions)
                {
                    if (session.Value.ControlledClient != null &&
                        session.Value.ControlledClient.RemoteEndPoint != null)
                    {
                        string tcpClientIp = session.Value.ControlledClient.RemoteEndPoint.Split(':')[0];
                        if (tcpClientIp == remoteIp)
                        {
                            logicalIp = session.Key;
                            break;
                        }
                    }
                }

                if (logicalIp == null && sessions.ContainsKey(remoteIp))
                {
                    logicalIp = remoteIp;
                }

                if (logicalIp == null || !sessions.ContainsKey(logicalIp))
                {
                    if (frameId % 100 == 0)
                    {
                        var availableIps = sessions.Keys.ToList();
                        var controlledClientIps = sessions.Values
                            .Where(s => s.ControlledClient != null && s.ControlledClient.RemoteEndPoint != null)
                            .Select(s => s.ControlledClient.RemoteEndPoint.Split(':')[0])
                            .ToList();

                        Console.WriteLine($"[UDP] Frame dropped: No session found for UDP remote IP {remoteIp}.");
                        Console.WriteLine($"[UDP] Available session keys: {string.Join(", ", availableIps)}");
                        Console.WriteLine($"[UDP] ControlledClient IPs: {string.Join(", ", controlledClientIps)}");
                    }
                    return;
                }

                udpClientEndpoints[logicalIp] = remoteEndPoint;

                if (!udpFrameBuffers.TryGetValue(logicalIp, out var framesForClient))
                {
                    framesForClient = new Dictionary<int, UdpFrameBuffer>();
                    udpFrameBuffers[logicalIp] = framesForClient;
                }

                if (!framesForClient.TryGetValue(frameId, out var frameBuffer))
                {
                    frameBuffer = new UdpFrameBuffer
                    {
                        FrameId = frameId,
                        TotalPackets = totalPackets,
                        FirstPacketTime = DateTime.Now,
                        TimestampMs = timestampMs,
                        Packets = new byte[totalPackets][],
                        Received = new bool[totalPackets],
                        ReceivedCount = 0
                    };
                    framesForClient[frameId] = frameBuffer;
                }

                int payloadSize = buffer.Length - payloadOffset;
                if (payloadSize <= 0) return;

                if (!frameBuffer.Received[packetIndex])
                {
                    frameBuffer.Packets[packetIndex] = new byte[payloadSize];
                    Buffer.BlockCopy(buffer, payloadOffset, frameBuffer.Packets[packetIndex], 0, payloadSize);
                    frameBuffer.Received[packetIndex] = true;
                    frameBuffer.ReceivedCount++;
                }

                CleanupOldFramesLocked(logicalIp, framesForClient);

                if (frameBuffer.ReceivedCount == frameBuffer.TotalPackets)
                {
                    AssembleAndForwardFrameLocked(logicalIp, frameBuffer);
                    framesForClient.Remove(frameId);
                }
            }
        }

        /// <summary>
        /// Drop frames that are missing packets for longer than the timeout.
        /// </summary>
        private void CleanupOldFramesLocked(string ip, Dictionary<int, UdpFrameBuffer> framesForClient)
        {
            const int frameTimeoutMs = 200;
            var now = DateTime.Now;
            var toRemove = new List<int>();

            foreach (var kv in framesForClient)
            {
                var f = kv.Value;
                if ((now - f.FirstPacketTime).TotalMilliseconds > frameTimeoutMs)
                {
                    if (streamStats.TryGetValue(ip, out var stats))
                    {
                        double loss = 1.0 - (double)f.ReceivedCount / Math.Max(1, (int)f.TotalPackets);
                        stats.AvgPacketLossPercent = stats.AvgPacketLossPercent * 0.9 + loss * 100 * 0.1;
                        stats.LastFrameTotalPackets = f.TotalPackets;
                        stats.LastFrameReceivedPackets = f.ReceivedCount;
                    }
                    toRemove.Add(kv.Key);
                }
            }

            foreach (var id in toRemove)
            {
                framesForClient.Remove(id);
            }
        }

        /// <summary>
        /// Assemble a complete frame into JPEG bytes, compute packet loss (0% when full),
        /// then send it back to the controller via TCP as WEBCAM_FRAME.
        /// </summary>
        private void AssembleAndForwardFrameLocked(string ip, UdpFrameBuffer frame)
        {
            int totalSize = frame.Packets.Sum(p => p?.Length ?? 0);
            byte[] fullFrame = new byte[totalSize];
            int offset = 0;

            for (int i = 0; i < frame.TotalPackets; i++)
            {
                byte[] part = frame.Packets[i];
                if (part == null) continue;
                Buffer.BlockCopy(part, 0, fullFrame, offset, part.Length);
                offset += part.Length;
            }

            string base64 = Convert.ToBase64String(fullFrame);

            if (!sessions.ContainsKey(ip))
            {
                Console.WriteLine($"[UDP] Frame dropped: No session found for IP {ip}");
                return;
            }

            if (sessions[ip].ControllerClient == null)
            {
                Console.WriteLine($"[UDP] Frame dropped: No ControllerClient for IP {ip}");
                return;
            }

            var controllerClient = sessions[ip].ControllerClient;
            var stats = streamStats.ContainsKey(ip) ? streamStats[ip] : null;

            if (stats != null)
            {
                stats.FramesForwarded++;
                stats.LastFrameTime = DateTime.Now;
                stats.LastFrameTotalPackets = frame.TotalPackets;
                stats.LastFrameReceivedPackets = frame.ReceivedCount;

                double loss = 0;
                stats.AvgPacketLossPercent = stats.AvgPacketLossPercent * 0.9 + loss * 100 * 0.1;
            }

            double pingMs = stats?.AvgPingMs ?? -1;
            double lossPercent = stats?.AvgPacketLossPercent ?? -1;

            string data = $"RESPONSE|WEBCAM_FRAME|{base64}|{pingMs:F1}|{lossPercent:F1}";
            try
            {
                SendResponse(controllerClient.TcpClient.GetStream(), data);

                if (stats != null && stats.FramesForwarded % 30 == 0)
                {
                    Console.WriteLine($"[UDP] Forwarded {stats.FramesForwarded} frames to controller for {ip}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR][UDP] Forward frame to {ip}: {ex.Message}");
            }
        }

        // ==================== UDP PING ====================

        private readonly Dictionary<string, (int frameId, DateTime sendTime)> udpPingStates
            = new Dictionary<string, (int frameId, DateTime sendTime)>();

        /// <summary>
        /// Periodically send UDP ping packets to streaming clients to measure RTT.
        /// </summary>
        private async Task UdpPingLoop()
        {
            const int pingIntervalMs = 1000;

            while (isRunning)
            {
                try
                {
                    await Task.Delay(pingIntervalMs);

                    lock (lockObj)
                    {
                        if (udpStreamListener == null) continue;

                        foreach (var kv in udpClientEndpoints.ToList())
                        {
                            string ip = kv.Key;
                            IPEndPoint endpoint = kv.Value;

                            if (!streamStats.ContainsKey(ip) || !streamStats[ip].IsStreaming)
                                continue;

                            int pingId = Environment.TickCount;
                            long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                            byte[] packet = new byte[24];
                            Array.Copy(BitConverter.GetBytes(pingId), 0, packet, 0, 4);
                            Array.Copy(BitConverter.GetBytes((short)0), 0, packet, 4, 2);
                            Array.Copy(BitConverter.GetBytes((short)1), 0, packet, 6, 2);
                            Array.Copy(BitConverter.GetBytes(timestampMs), 0, packet, 8, 8);
                            packet[16] = 1;

                            udpStreamListener.Send(packet, packet.Length, endpoint);
                            udpPingStates[ip] = (pingId, DateTime.Now);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR][UDP] PingLoop: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Handle PONG from client and update moving-average RTT.
        /// </summary>
        private void HandleUdpPong(string remoteIp, int pingId, long timestampMs)
        {
            string ip = remoteIp;

            lock (lockObj)
            {
                if (!udpPingStates.TryGetValue(ip, out var state))
                    return;

                double rttMs = (DateTime.Now - state.sendTime).TotalMilliseconds;

                if (streamStats.TryGetValue(ip, out var stats))
                {
                    if (stats.AvgPingMs == 0)
                    {
                        stats.AvgPingMs = rttMs;
                    }
                    else
                    {
                        stats.AvgPingMs = stats.AvgPingMs * 0.8 + rttMs * 0.2;
                    }
                }
            }
        }

        private void SendResponse(NetworkStream stream, string response)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(response);
                byte[] lengthPrefix = BitConverter.GetBytes(data.Length);

                stream.Write(lengthPrefix, 0, lengthPrefix.Length);
                stream.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Send response: {ex.Message}");
            }
        }

        private void DisplayStats()
        {
            while (isRunning)
            {
                Thread.Sleep(5000);

                lock (lockObj)
                {
                    var activeStreams = streamStats.Where(s => s.Value.IsStreaming).ToList();

                    if (activeStreams.Any())
                    {
                        Console.WriteLine("\n╔════════════════════════════════════════════════════════════╗");
                        Console.WriteLine("║                    WEBCAM STREAM STATS                      ║");
                        Console.WriteLine("╠════════════════════════════════════════════════════════════╣");

                        foreach (var kvp in activeStreams)
                        {
                            var stats = kvp.Value;
                            var elapsed = (DateTime.Now - stats.StartTime).TotalSeconds;
                            var fps = elapsed > 0 ? stats.FramesForwarded / elapsed : 0;

                            Console.WriteLine(
                                $"║ IP: {stats.IP,-15} | Frames: {stats.FramesForwarded,6} | FPS: {fps,4:F1} | " +
                                $"Ping: {stats.AvgPingMs,6:F1} ms | Loss: {stats.AvgPacketLossPercent,5:F1}% ║");
                        }

                        Console.WriteLine("╚════════════════════════════════════════════════════════════╝\n");
                    }
                }
            }
        }

        public void Stop()
        {
            isRunning = false;
            listener?.Stop();

            try
            {
                udpStreamListener?.Close();
            }
            catch { }

            lock (lockObj)
            {
                foreach (var client in clients)
                {
                    client.TcpClient.Close();
                }
                clients.Clear();
                sessions.Clear();
                streamStats.Clear();
            }
        }
    }

    // ==================== UDP FRAME STRUCTURES ====================

    /// <summary>
    /// Buffer used to accumulate all UDP packets that belong to a single frame (by frame_id).
    /// </summary>
    public class UdpFrameBuffer
    {
        public int FrameId { get; set; }
        public short TotalPackets { get; set; }
        public DateTime FirstPacketTime { get; set; }
        public long TimestampMs { get; set; }

        public byte[][] Packets { get; set; }
        public bool[] Received { get; set; }
        public short ReceivedCount { get; set; }
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

    public class StreamStats
    {
        public string IP { get; set; }
        public bool IsStreaming { get; set; }
        public int FramesForwarded { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime LastFrameTime { get; set; }
        public double AvgPingMs { get; set; } = 0;
        public double AvgPacketLossPercent { get; set; } = 0;
        public int LastFrameTotalPackets { get; set; }
        public int LastFrameReceivedPackets { get; set; }
    }

    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║         REMOTE PC CONTROL SERVER v2.0                    ║");
            Console.WriteLine("║         HCMUS - Socket Programming Project               ║");
            Console.WriteLine("║         ✓ Webcam Streaming Support                       ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            Server server = new Server();
            server.Start(8888);

            Console.WriteLine("\n[INFO] Server is running. Features:");
            Console.WriteLine("  • Applications & Processes Control");
            Console.WriteLine("  • Screenshot Capture");
            Console.WriteLine("  • Keylogger");
            Console.WriteLine("  • Webcam Real-time Streaming (15 FPS)");
            Console.WriteLine("  • System Shutdown/Restart");
            Console.WriteLine("\nPress any key to stop the server...\n");

            Console.ReadKey();

            Console.WriteLine("\n[SERVER] Shutting down...");
            server.Stop();
            Console.WriteLine("[SERVER] Server stopped successfully.");
        }
    }
}