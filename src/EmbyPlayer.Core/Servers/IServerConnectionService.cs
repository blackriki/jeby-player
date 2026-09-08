namespace EmbyPlayer.Core.Servers;

public interface IServerConnectionService
{
    Task<ServerConnectionResult> ConnectAsync(string serverUrl, CancellationToken cancellationToken);
}
