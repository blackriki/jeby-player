namespace EmbyPlayer.Player;

public sealed class PlayerOperationResult
{
    private PlayerOperationResult(bool isSuccess, PlayerError error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public PlayerError Error { get; }

    public static PlayerOperationResult Success()
    {
        return new PlayerOperationResult(true, PlayerError.None);
    }

    public static PlayerOperationResult Failure(PlayerError error)
    {
        return new PlayerOperationResult(false, error);
    }
}
