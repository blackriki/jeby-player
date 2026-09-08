namespace EmbyPlayer.Core.Servers;

public sealed class ServerConnectionInfo
{
    public ServerConnectionInfo(string serverBase)
    {
        if (string.IsNullOrWhiteSpace(serverBase))
        {
            throw new ArgumentException("Server base is required.", nameof(serverBase));
        }

        ServerBase = serverBase;
    }

    public string ServerBase { get; }
}
