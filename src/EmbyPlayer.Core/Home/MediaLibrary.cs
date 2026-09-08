namespace EmbyPlayer.Core.Home;

public sealed class MediaLibrary
{
    public MediaLibrary(string id, string name, string type)
    {
        Id = id ?? string.Empty;
        Name = name ?? string.Empty;
        Type = type ?? string.Empty;
    }

    public string Id { get; }

    public string Name { get; }

    public string Type { get; }
}
