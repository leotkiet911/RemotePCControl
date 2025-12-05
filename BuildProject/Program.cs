using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net;
using System.Net.Sockets;


namespace BuildProject;

internal record ShortcutDefinition(string Name, string ProjectFolder, string ExecutableName);

internal static class Program
{
    private static readonly ShortcutDefinition[] Targets =
    [
        new ShortcutDefinition("RemotePCControl - Server", "Server", "Server.exe"),
        new ShortcutDefinition("RemotePCControl - Client", "ClientControlled", "ClientControlled.exe"),
        new ShortcutDefinition("RemotePCControl - WebInterface", "WebInterface", "WebInterface.exe"),
    ];

    private static void Main()
    {
        try
        {
            string solutionRoot = FindSolutionRoot();
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string repoShortcutFolder = Path.Combine(solutionRoot, "Shortcuts");
            string ipAddress = GetLocalIpAddress();

            RunCmd($"setx REMOTEPC_SERVER_IP {ipAddress}");
            RunCmd("setx REMOTEPC_SERVER_PORT 8888");
            RunCmd($"setx ServerConnection__Host {ipAddress}");
            RunCmd("setx ServerConnection__Port 8888");

            Directory.CreateDirectory(repoShortcutFolder);

            Console.WriteLine("╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║    RemotePCControl Auto Build & Shortcut Builder     ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝");
            Console.WriteLine($"Solution root : {solutionRoot}");
            Console.WriteLine($"Desktop       : {desktopPath}");
            Console.WriteLine($"IP Address    : {ipAddress}");
            Console.WriteLine();

            foreach (var target in Targets)
            {
                Console.WriteLine($"[PROCESSING] {target.Name}...");
                
                // Build and publish project
                string publishPath = BuildAndPublishProject(solutionRoot, target);
                
                // find executable in publish folder
                string exePath = Path.Combine(publishPath, target.ExecutableName);
                
                if (!File.Exists(exePath))
                {
                    throw new FileNotFoundException($"Không tìm thấy {target.ExecutableName} sau khi publish tại {publishPath}");
                }

                Console.WriteLine($"[FOUND] {exePath}");

                // create shortcuts
                CreateShortcut(repoShortcutFolder, target.Name, exePath);
                CreateShortcut(desktopPath, target.Name, exePath);
                
                Console.WriteLine();
            }

            Console.WriteLine("[DONE] All projects have been built, published and shortcuts created!");
            Console.WriteLine($"       • Shortcuts at: {repoShortcutFolder}");
            Console.WriteLine($"       • Shortcuts at: {desktopPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    public static void RunCmd(string command)
    {
        var process = new System.Diagnostics.Process();
        var startInfo = new System.Diagnostics.ProcessStartInfo
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
        process.WaitForExit();
    }


    public static string GetLocalIpAddress()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            // CHỈ CHO PHÉP CARD THẬT
            if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet)
            {
                continue; // Bỏ VMware, VirtualBox, Hyper-V, Bluetooth, Loopback...
            }

            var props = ni.GetIPProperties();

            // ƯU TIÊN CARD CÓ GATEWAY (NGHĨA LÀ CARD ĐANG DÙNG INTERNET)
            if (props.GatewayAddresses.Count == 0)
                continue;

            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(addr.Address))
                {
                    return addr.Address.ToString();
                }
            }
        }

        return "";
    }


    private static string FindSolutionRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RemotePCControl.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new InvalidOperationException("Cannot find RemotePCControl.sln file when searching up.");
    }

    private static string BuildAndPublishProject(string solutionRoot, ShortcutDefinition definition)
    {
        string projectPath = Path.Combine(solutionRoot, definition.ProjectFolder);
        string publishPath = Path.Combine(projectPath, "bin", "publish");

        Console.WriteLine($"  → Building project: {definition.ProjectFolder}");

        // Tìm file .csproj
        string? csprojFile = Directory.GetFiles(projectPath, "*.csproj").FirstOrDefault();
        if (csprojFile == null)
        {
            throw new FileNotFoundException($"Cannot find .csproj file in {projectPath}");
        }

        // delete old publish folder if it exists
        if (Directory.Exists(publishPath))
        {
            Directory.Delete(publishPath, true);
        }

        // run dotnet publish
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"publish \"{csprojFile}\" -c Release -o \"{publishPath}\" --self-contained false",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Cannot start dotnet process");
        }

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            Console.WriteLine(output);
            Console.WriteLine(error);
            throw new InvalidOperationException($"Build/Publish failed for {definition.ProjectFolder} (Exit code: {process.ExitCode})");
        }

        Console.WriteLine($"  ✓ Published to: {publishPath}");
        return publishPath;
    }

    private static void CreateShortcut(string folder, string shortcutName, string targetPath)
    {
        string shortcutPath = Path.Combine(folder, $"{shortcutName}.lnk");

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Cannot create WScript.Shell (COM). Ensure running on Windows.");

        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Cannot create WScript.Shell instance.");

        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
        shortcut.IconLocation = targetPath;
        shortcut.WindowStyle = 1;
        shortcut.Save();

        Console.WriteLine($"  [SHORTCUT] {shortcutPath}");
    }
}