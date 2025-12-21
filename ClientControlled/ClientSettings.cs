using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
                string baseDir = AppContext.BaseDirectory;
                string settingsPath = GetSettingsPath();
                ClientSettings? settings = null;

                // First, try to load from server-info.json if it exists (has priority)
                string serverInfoPath = Path.Combine(baseDir, "server-info.json");
                if (File.Exists(serverInfoPath))
                {
                    try
                    {
                        string serverInfoJson = File.ReadAllText(serverInfoPath);
                        var serverInfo = JsonSerializer.Deserialize<ServerInfo>(serverInfoJson);
                        if (serverInfo != null && !string.IsNullOrWhiteSpace(serverInfo.Ip))
                        {
                            settings = new ClientSettings
                            {
                                ServerIp = serverInfo.Ip,
                                ServerPort = serverInfo.Port
                            };
                            Console.WriteLine($"[SETTINGS] Loaded from server-info.json: {serverInfo.Ip}:{serverInfo.Port}");

                            // Update clientsettings.json with the correct values
                            Save(settings, settingsPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SETTINGS] Failed to load server-info.json: {ex.Message}");
                    }
                }

                // If server-info.json not found or failed, try clientsettings.json
                if (settings == null && File.Exists(settingsPath))
                {
                    try
                    {
                        string json = File.ReadAllText(settingsPath);
                        settings = JsonSerializer.Deserialize<ClientSettings>(json);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SETTINGS] Failed to load clientsettings.json: {ex.Message}");
                    }
                }

                // If still no settings, create defaults
                if (settings == null)
                {
                    settings = new ClientSettings();
                    Save(settings, settingsPath);
                }

                // Validate and fix settings
                if (string.IsNullOrWhiteSpace(settings.ServerIp))
                {
                    settings.ServerIp = "127.0.0.1";
                }

                if (settings.ServerPort <= 0 || settings.ServerPort > 65535)
                {
                    settings.ServerPort = 8888;
                }

                // DO NOT apply environment overrides if we loaded from file
                // Environment variables may contain old/incorrect values (e.g., client's own IP)
                // File-based settings (server-info.json or clientsettings.json) have the correct server IP
                // Only apply environment overrides if no file was found
                bool loadedFromFile = File.Exists(serverInfoPath) || File.Exists(settingsPath);
                if (!loadedFromFile)
                {
                    Console.WriteLine("[SETTINGS] No settings file found, checking environment variables...");
                    ApplyEnvironmentOverrides(settings);
                }
                else
                {
                    Console.WriteLine("[SETTINGS] Settings loaded from file, skipping environment variable overrides");
                }

                return settings;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SETTINGS] Failed to load settings: {ex.Message}. Using defaults.");
                return new ClientSettings();
            }
        }

        // Helper class for deserializing server-info.json
        private class ServerInfo
        {
            [JsonPropertyName("Ip")]
            public string Ip { get; set; } = "";

            [JsonPropertyName("Port")]
            public int Port { get; set; } = 8888;
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
                string? directory = Path.GetDirectoryName(path);
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
            string? combined = Path.Combine(baseDir, "clientsettings.json");
            return combined ?? Path.Combine(Environment.CurrentDirectory, "clientsettings.json");
        }
    }
}

