using ClientControlled;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace RemotePCControl
{
    static class Program
    {
        private static string logFile = Path.Combine(AppContext.BaseDirectory, "client-setup.log");

        [STAThread]
        static void Main()
        {
            // Initialize logging
            LogToFile("=== ClientControlled.exe Started ===");
            LogToFile($"Working Directory: {AppContext.BaseDirectory}");

            // Load settings and set environment variables before starting the form
            try
            {
                var settings = ClientSettings.Load();
                string logMsg = $"[INFO] Loaded settings: ServerIp={settings.ServerIp}, ServerPort={settings.ServerPort}";
                Console.WriteLine(logMsg);
                LogToFile(logMsg);
                
                SetEnvironmentVariables(settings.ServerIp, settings.ServerPort);
            }
            catch (Exception ex)
            {
                // Log error but continue - the app can still work with defaults
                string errorMsg = $"[WARN] Failed to set environment variables: {ex.Message}";
                Console.WriteLine(errorMsg);
                LogToFile(errorMsg);
                LogToFile($"Stack trace: {ex.StackTrace}");
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.Run(new Form1());
        }

        private static void LogToFile(string message)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.AppendAllText(logFile, $"[{timestamp}] {message}\n");
            }
            catch
            {
                // Ignore logging errors
            }
        }

        private static void SetEnvironmentVariables(string serverIp, int serverPort)
        {
            try
            {
                Console.WriteLine($"[ENV] Starting to set environment variables: IP={serverIp}, Port={serverPort}");
                
                // Run setx commands exactly like BuildProject does
                Console.WriteLine($"[ENV] Setting REMOTEPC_SERVER_IP...");
                RunCmd($"setx REMOTEPC_SERVER_IP {serverIp}");
                
                Console.WriteLine($"[ENV] Setting REMOTEPC_SERVER_PORT...");
                RunCmd($"setx REMOTEPC_SERVER_PORT {serverPort}");
                
                Console.WriteLine($"[ENV] Setting ServerConnection__Host...");
                RunCmd($"setx ServerConnection__Host {serverIp}");
                
                Console.WriteLine($"[ENV] Setting ServerConnection__Port...");
                RunCmd($"setx ServerConnection__Port {serverPort}");

                string successMsg = $"[ENV] All environment variables set successfully: IP={serverIp}, Port={serverPort}";
                Console.WriteLine(successMsg);
                LogToFile(successMsg);
                
                string noteMsg = "[ENV] Note: Variables will be available in new command prompt sessions.";
                Console.WriteLine(noteMsg);
                LogToFile(noteMsg);
            }
            catch (Exception ex)
            {
                string errorMsg = $"[ENV] Error setting environment variables: {ex.Message}";
                Console.WriteLine(errorMsg);
                LogToFile(errorMsg);
                
                string stackMsg = $"[ENV] Stack trace: {ex.StackTrace}";
                Console.WriteLine(stackMsg);
                LogToFile(stackMsg);
            }
        }

        public static void RunCmd(string command)
        {
            try
            {
                var process = new Process();
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/C " + command,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                process.StartInfo = startInfo;
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                // Log output and error for debugging
                if (!string.IsNullOrEmpty(output))
                {
                    string outputMsg = $"[CMD OUTPUT] {output.Trim()}";
                    Console.WriteLine(outputMsg);
                    LogToFile(outputMsg);
                }

                if (process.ExitCode != 0)
                {
                    string errorMsg = $"[ERROR] Command failed (ExitCode={process.ExitCode}): {command}";
                    Console.WriteLine(errorMsg);
                    LogToFile(errorMsg);
                    
                    if (!string.IsNullOrEmpty(error))
                    {
                        string errorDetail = $"[ERROR DETAIL] {error.Trim()}";
                        Console.WriteLine(errorDetail);
                        LogToFile(errorDetail);
                    }
                    
                    // Show message box for critical errors
                    MessageBox.Show(
                        $"Không thể thiết lập biến môi trường!\n\nLệnh: {command}\nLỗi: {error}\n\nVui lòng chạy với quyền Administrator.\n\nChi tiết đã được ghi vào: {logFile}",
                        "Lỗi thiết lập biến môi trường",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                }
                else
                {
                    string successMsg = $"[SUCCESS] Command executed: {command}";
                    Console.WriteLine(successMsg);
                    LogToFile(successMsg);
                }
            }
            catch (Exception ex)
            {
                string exceptionMsg = $"[EXCEPTION] Failed to run command '{command}': {ex.Message}";
                Console.WriteLine(exceptionMsg);
                LogToFile(exceptionMsg);
                LogToFile($"Stack trace: {ex.StackTrace}");
                
                MessageBox.Show(
                    $"Lỗi khi chạy lệnh: {command}\n\n{ex.Message}\n\nChi tiết đã được ghi vào: {logFile}",
                    "Lỗi",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }
    }
}