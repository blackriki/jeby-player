using EmbyPlayer.Core.Library;

namespace EmbyPlayer.UI.ViewModels;

public sealed class LibraryItemViewModel : ViewModelBase
{
    private bool isSelected;

    public LibraryItemViewModel(LibraryItem library)
    {
        Source = library ?? throw new ArgumentNullException(nameof(library));
        Id = library.Id;
        Name = string.IsNullOrWhiteSpace(library.Name) ? "未命名媒体库" : library.Name;
        Type = GetTypeLabel(library.Type);
    }

    public LibraryItem Source { get; }

    public string Id { get; }

    public string Name { get; }

    public string Type { get; }

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value)
            {
                return;
            }

            isSelected = value;
            OnPropertyChanged();
        }
    }

    private static string GetTypeLabel(string type)
    {
        return type.Trim().ToLowerInvariant() switch
        {
            "playlists" => "播放列表",
            "movies" or "movie" => "电影",
            "tvshows" or "tv" => "电视剧",
            "collections" or "boxsets" => "合集",
            "mixed" => "混合内容",
            _ => string.IsNullOrWhiteSpace(type) ? "媒体库" : type
        };
    }
}
