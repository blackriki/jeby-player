using EmbyPlayer.Core.Home;

namespace EmbyPlayer.UI.ViewModels;

public sealed class HomeLibraryViewModel
{
    public HomeLibraryViewModel(MediaLibrary library)
    {
        Id = library.Id;
        Name = string.IsNullOrWhiteSpace(library.Name) ? "未命名媒体库" : library.Name;
        Type = GetTypeLabel(library.Type);
    }

    public string Id { get; }

    public string Name { get; }

    public string Type { get; }

    private static string GetTypeLabel(string type)
    {
        return type.Trim().ToLowerInvariant() switch
        {
            "playlists" => "播放列表",
            "movies" => "电影",
            "tvshows" => "电视剧",
            "collections" => "合集",
            "boxsets" => "合集",
            "mixed" => "混合内容",
            _ => string.IsNullOrWhiteSpace(type) ? "媒体库" : type
        };
    }
}
