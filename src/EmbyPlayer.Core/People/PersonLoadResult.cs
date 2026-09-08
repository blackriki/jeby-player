namespace EmbyPlayer.Core.People;

public enum PersonLoadError
{
    None,
    Unauthorized,
    Forbidden,
    NotFound,
    ServerUnreachable,
    ServerTimeout,
    ServerError,
    InvalidResponse,
    Cancelled
}

public sealed record PersonLoadResult(PersonDetail? Person, PersonLoadError Error)
{
    public bool IsSuccess => Person is not null && Error == PersonLoadError.None;

    public static PersonLoadResult Success(PersonDetail person) => new(person, PersonLoadError.None);

    public static PersonLoadResult Failure(PersonLoadError error) => new(null, error);
}
