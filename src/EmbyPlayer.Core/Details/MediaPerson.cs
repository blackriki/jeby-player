namespace EmbyPlayer.Core.Details;

public sealed record MediaPerson(
    string Id,
    string Name,
    string Role,
    string Type,
    string? ImageUrl);
