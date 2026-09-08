namespace EmbyPlayer.Core.Servers;

public sealed class ServerUrlNormalizationResult
{
    private ServerUrlNormalizationResult(bool isSuccess, ServerConnectionInfo? server, ServerConnectionError error)
    {
        IsSuccess = isSuccess;
        Server = server;
        Error = error;
    }

    public bool IsSuccess { get; }

    public ServerConnectionInfo? Server { get; }

    public ServerConnectionError Error { get; }

    public static ServerUrlNormalizationResult Success(ServerConnectionInfo server)
    {
        return new ServerUrlNormalizationResult(true, server, ServerConnectionError.None);
    }

    public static ServerUrlNormalizationResult Failure(ServerConnectionError error)
    {
        if (error == ServerConnectionError.None)
        {
            throw new ArgumentException("Failure requires an error.", nameof(error));
        }

        return new ServerUrlNormalizationResult(false, null, error);
    }
}
