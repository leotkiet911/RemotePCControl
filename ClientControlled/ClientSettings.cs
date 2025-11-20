using System;
using System.IO;
using System.Text.Json;

namespace RemotePCControl
{
    public class ClientSettings
    {
        public const string ServerIpEnvVar = "REMOTEPC_SERVER_IP";
        public const string ServerPortEnvVar = "REMOTEPC_SERVER_PORT";

        public string ServerIp { get; set; } = "127.0.0.1";
        public int ServerPort { get; set; } = 8888;

        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static ClientSettings Load()
        {
            try
            {
                string settingsPath = GetSettingsPath();

                if (!File.Exists(settingsPath))
                {
                    var defaults = new ClientSettings();
                    Save(defaults, settingsPath);
                    return defaults;
                }

                string json = File.ReadAllText(settingsPath);
                var settings = JsonSerializer.Deserialize<ClientSettings>(json);

                if (settings == null)
                {
                    throw new InvalidOperationException("Settings file could not be parsed.");
                }

                if (string.IsNullOrWhiteSpace(settings.ServerIp))
                {
                    settings.ServerIp = "127.0.0.1";
                }

                if (settings.ServerPort <= 0 || settings.ServerPort > 65535)
                {
                    settings.ServerPort = 8888;
                }

                ApplyEnvironmentOverrides(settings);

                return settings;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SETTINGS] Failed to load settings: {ex.Message}. Using defaults.");
                return new ClientSettings();
            }
        }

        private static void ApplyEnvironmentOverrides(ClientSettings settings)
        {
            try
            {
                var envIp = Environment.GetEnvironmentVariable(ServerIpEnvVar);
                if (!string.IsNullOrWhiteSpace(envIp))
                {
                    settings.ServerIp = envIp.Trim();
                    Console.WriteLine($"[SETTINGS] Overriding server IP via {ServerIpEnvVar}={settings.ServerIp}");
                }

                var envPort = Environment.GetEnvironmentVariable(ServerPortEnvVar);
                if (int.TryParse(envPort, out int port) && port > 0 && port <= 65535)
                {
                    settings.ServerPort = port;
                    Console.WriteLine($"[SETTINGS] Overriding server port via {ServerPortEnvVar}={settings.ServerPort}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SETTINGS] Failed to apply environment overrides: {ex.Message}");
            }
        }

        private static void Save(ClientSettings settings, string path)
        {
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonSerializer.Serialize(settings, SerializerOptions);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SETTINGS] Failed to write default settings: {ex.Message}");
            }
        }

        private static string GetSettingsPath()
        {
            string baseDir = AppContext.BaseDirectory;
            return Path.Combine(baseDir, "clientsettings.json");
        }
    }
}

