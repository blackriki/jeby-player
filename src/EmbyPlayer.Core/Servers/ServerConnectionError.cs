namespace EmbyPlayer.Core.Servers;

public enum ServerConnectionError
{
    None,
    EmptyServerUrl,
    InvalidUrl,
    UnsupportedScheme,
    ServerUnreachable,
    ServerTimeout,
    UnrecognizedServer,
    Cancelled
}
