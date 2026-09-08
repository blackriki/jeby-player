using EmbyPlayer.Core.Details;

namespace EmbyPlayer.UI.ViewModels;

public sealed class SimilarMediaItemViewModel
{
    public SimilarMediaItemViewModel(SimilarMediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Id = item.Id;
        Title = string.IsNullOrWhiteSpace(item.Title) ? "未命名媒体" : item.Title;
        Type = item.Type;
        Year = item.Year;
        PosterUrl = item.PosterUrl;
        PlayedPercentage = item.PlayedPercentage;
        ResumePositionTicks = item.ResumePositionTicks;
        IsPlayed = item.IsPlayed;
    }

    public string Id { get; }

    public string Title { get; }

    public string Type { get; }

    public int? Year { get; }

    public string? PosterUrl { get; }

    public double? PlayedPercentage { get; }

    public long? ResumePositionTicks { get; }

    public bool IsPlayed { get; }

    public string YearText => Year?.ToString() ?? string.Empty;

    public bool HasYear => Year is not null;

    public bool HasProgress => PlayedPercentage is > 0 and < 100;

    public double ProgressValue => Math.Clamp(PlayedPercentage ?? 0d, 0d, 100d);

    public string AutomationName => IsPlayed
        ? $"打开类似作品：{Title}，已观看"
        : $"打开类似作品：{Title}";
}
