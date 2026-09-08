using EmbyPlayer.Core.Search;

namespace EmbyPlayer.UI.ViewModels;

public sealed class SearchResultItemViewModel
{
    public SearchResultItemViewModel(SearchResultItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Id = item.Id;
        Title = item.Title;
        RawType = item.Type;
        Type = ToChineseType(item.Type);
        Year = item.Year;
        PosterUrl = item.PosterUrl;
        ProgressValue = item.PlayedPercentage ?? 0;
        HasProgress = item.PlayedPercentage is > 0 and < 100;
        IsFavorite = item.IsFavorite;
        IsPlayed = item.IsPlayed;
        MatchInfo = item.MatchInfo;
    }

    public string Id { get; }

    public string Title { get; }

    public string RawType { get; }

    public string Type { get; }

    public int? Year { get; }

    public string? PosterUrl { get; }

    public double ProgressValue { get; }

    public bool HasProgress { get; }

    public bool IsFavorite { get; }

    public bool IsPlayed { get; }

    public SearchMatchInfo? MatchInfo { get; }

    public bool HasYear => Year.HasValue;

    public string YearText => Year?.ToString() ?? string.Empty;

    private static string ToChineseType(string type)
    {
        return type.Trim().ToLowerInvariant() switch
        {
            "movie" => "电影",
            "series" => "电视剧",
            "episode" => "单集",
            "boxset" => "合集",
            "playlist" => "\u64ad\u653e\u5217\u8868",
            "playlists" => "\u64ad\u653e\u5217\u8868",
            "video" => "视频",
            _ => string.IsNullOrWhiteSpace(type) ? "未知" : type
        };
    }
}
