using EmbyPlayer.Core.Home;

namespace EmbyPlayer.UI.ViewModels;

public sealed class HomeMediaCardViewModel : ViewModelBase
{
    private bool isPreparingPlayback;

    public HomeMediaCardViewModel(MediaCard mediaCard)
    {
        Id = mediaCard.Id;
        Title = string.IsNullOrWhiteSpace(mediaCard.Title) ? "未命名媒体" : mediaCard.Title;
        Type = mediaCard.Type;
        PosterUrl = mediaCard.PosterUrl;
        HasDedicatedHeroImage = !string.IsNullOrWhiteSpace(mediaCard.HeroImageUrl)
            && !string.Equals(mediaCard.HeroImageUrl, mediaCard.PosterUrl, StringComparison.Ordinal);
        HeroImageUrl = string.IsNullOrWhiteSpace(mediaCard.HeroImageUrl)
            ? mediaCard.PosterUrl
            : mediaCard.HeroImageUrl;
        PlayedPercentage = mediaCard.PlayedPercentage;
        IsFavorite = mediaCard.IsFavorite;
        IsPlayed = mediaCard.IsPlayed;
        ResumePositionTicks = Math.Max(0, mediaCard.ResumePositionTicks.GetValueOrDefault());
        SeriesId = mediaCard.SeriesId;
        SeasonId = mediaCard.SeasonId;
        LogoUrl = mediaCard.LogoUrl;
        Year = mediaCard.Year;
        YearText = mediaCard.Year is null ? string.Empty : mediaCard.Year.Value.ToString();
        SubtitleText = string.IsNullOrWhiteSpace(mediaCard.Subtitle) ? YearText : mediaCard.Subtitle;
    }

    public string Id { get; }

    public string Title { get; }

    public string Type { get; }

    public string? PosterUrl { get; }

    public string? HeroImageUrl { get; }

    public bool HasDedicatedHeroImage { get; }

    public double? PlayedPercentage { get; }

    public bool IsFavorite { get; }

    public bool IsPlayed { get; }

    public long ResumePositionTicks { get; }

    public string? SeriesId { get; }

    public string? SeasonId { get; }

    public string? LogoUrl { get; }

    public int? Year { get; }

    public bool IsPlayableMedia => Type?.ToLowerInvariant() is "movie" or "episode" or "video";

    public bool IsPreparingPlayback
    {
        get => isPreparingPlayback;
        internal set
        {
            if (isPreparingPlayback == value)
            {
                return;
            }

            isPreparingPlayback = value;
            OnPropertyChanged();
        }
    }

    public string YearText { get; }

    public bool HasYear => !string.IsNullOrWhiteSpace(YearText);

    public string SubtitleText { get; }

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(SubtitleText);

    public bool HasProgress => PlayedPercentage is > 0 and < 100;

    public double ProgressValue => Math.Clamp(PlayedPercentage ?? 0d, 0d, 100d);
}
