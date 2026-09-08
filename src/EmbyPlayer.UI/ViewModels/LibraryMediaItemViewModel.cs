using EmbyPlayer.Core.Library;

namespace EmbyPlayer.UI.ViewModels;

public sealed class LibraryMediaItemViewModel
{
    public LibraryMediaItemViewModel(LibraryMediaItem mediaItem)
    {
        Id = mediaItem.Id;
        Title = string.IsNullOrWhiteSpace(mediaItem.Title) ? "未命名媒体" : mediaItem.Title;
        Type = mediaItem.Type;
        PosterUrl = mediaItem.PosterUrl;
        PlayedPercentage = mediaItem.PlayedPercentage;
        IsFavorite = mediaItem.IsFavorite;
        IsPlayed = mediaItem.IsPlayed;
        YearText = mediaItem.Year is null ? string.Empty : mediaItem.Year.Value.ToString();
    }

    public string Id { get; }

    public string Title { get; }

    public string Type { get; }

    public string? PosterUrl { get; }

    public double? PlayedPercentage { get; }

    public bool IsFavorite { get; }

    public bool IsPlayed { get; }

    public string YearText { get; }

    public bool HasYear => !string.IsNullOrWhiteSpace(YearText);

    public bool HasProgress => PlayedPercentage is > 0 and < 100;

    public double ProgressValue => Math.Clamp(PlayedPercentage ?? 0d, 0d, 100d);
}
