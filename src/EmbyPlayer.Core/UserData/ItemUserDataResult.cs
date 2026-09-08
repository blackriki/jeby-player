namespace EmbyPlayer.Core.UserData;

public sealed class ItemUserDataResult
{
    private ItemUserDataResult(bool isSuccess, ItemUserDataError error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public ItemUserDataError Error { get; }

    public static ItemUserDataResult Success()
    {
        return new ItemUserDataResult(true, ItemUserDataError.None);
    }

    public static ItemUserDataResult Failure(ItemUserDataError error)
    {
        return new ItemUserDataResult(false, error);
    }
}
