namespace EmbyPlayer.Core.Servers;

public sealed class ServerConnectionResult
{
    private ServerConnectionResult(bool isSuccess, ServerConnectionInfo? server, ServerConnectionError error)
    {
        IsSuccess = isSuccess;
        Server = server;
        Error = error;
    }

    public bool IsSuccess { get; }

    public ServerConnectionInfo? Server { get; }

    public ServerConnectionError Error { get; }

    public static ServerConnectionResult Success(ServerConnectionInfo server)
    {
        return new ServerConnectionResult(true, server, ServerConnectionError.None);
    }

    public static ServerConnectionResult Failure(ServerConnectionError error)
    {
        if (error == ServerConnectionError.None)
        {
            throw new ArgumentException("Failure requires an error.", nameof(error));
        }

        return new ServerConnectionResult(false, null, error);
    }
}
