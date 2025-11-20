namespace RemotePCControl.WebInterface;

public class ServerConnectionOptions
{
    public const string SectionName = "ServerConnection";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8888;
}

