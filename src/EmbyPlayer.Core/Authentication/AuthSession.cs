namespace EmbyPlayer.Core.Authentication;

public sealed class AuthSession
{
    public AuthSession(
        string serverBase,
        string accessToken,
        string userId,
        string userName,
        string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverBase))
        {
            throw new ArgumentException("Server base is required.", nameof(serverBase));
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("Access token is required.", nameof(accessToken));
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new ArgumentException("User name is required.", nameof(userName));
        }

        ServerBase = serverBase;
        AccessToken = accessToken;
        UserId = userId;
        UserName = userName;
        ServerId = serverId;
    }

    public string ServerBase { get; }

    public string AccessToken { get; }

    public string UserId { get; }

    public string UserName { get; }

    public string ServerId { get; }
}
