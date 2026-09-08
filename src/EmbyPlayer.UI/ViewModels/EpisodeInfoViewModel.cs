using EmbyPlayer.Core.Series;

namespace EmbyPlayer.UI.ViewModels;

public sealed class EpisodeInfoViewModel : ViewModelBase
{
    private bool isNavigationTarget;

    public EpisodeInfoViewModel(EpisodeInfo episode, bool isNavigationTarget = false)
    {
        Source = episode ?? throw new ArgumentNullException(nameof(episode));
        Id = episode.Id;
        Title = string.IsNullOrWhiteSpace(episode.Title)
            ? episode.EpisodeIndex is null ? "未命名单集" : $"第 {episode.EpisodeIndex.Value} 集"
            : episode.Title;
        SeasonIndex = episode.SeasonIndex;
        EpisodeIndex = episode.EpisodeIndex;
        OverviewText = string.IsNullOrWhiteSpace(episode.Overview) ? "暂无简介" : episode.Overview!;
        DurationText = FormatDuration(episode.RunTimeTicks);
        RunTimeTicks = episode.RunTimeTicks;
        PlayedPercentage = episode.PlayedPercentage;
        ResumePositionTicks = episode.ResumePositionTicks;
        ThumbnailUrl = episode.ThumbnailUrl;
        HeroImageUrl = episode.HeroImageUrl;
        IsPlayed = episode.IsPlayed;
        NumberText = GetNumberText(episode);
        QuickNumberText = episode.EpisodeIndex?.ToString() ?? NumberText;
        this.isNavigationTarget = isNavigationTarget;
    }

    public EpisodeInfo Source { get; }

    public string Id { get; }

    public string Title { get; }

    public int? SeasonIndex { get; }

    public int? EpisodeIndex { get; }

    public string NumberText { get; }

    public string QuickNumberText { get; }

    public string DurationText { get; }

    public long? RunTimeTicks { get; }

    public string OverviewText { get; }

    public double? PlayedPercentage { get; private set; }

    public long? ResumePositionTicks { get; private set; }

    public string? ThumbnailUrl { get; }

    public string? HeroImageUrl { get; }

    public bool IsPlayed { get; private set; }

    public string PlayActionText => HasProgress ? "继续" : IsPlayed ? "重播" : "播放";

    public string AutomationName => $"{NumberText} {Title}，{GetWatchStateText()}";

    public string PlayAutomationName => $"{PlayActionText} {NumberText} {Title}";

    public bool IsNavigationTarget
    {
        get => isNavigationTarget;
        internal set
        {
            if (isNavigationTarget == value)
            {
                return;
            }

            isNavigationTarget = value;
            OnPropertyChanged();
        }
    }

    public bool HasProgress => !IsPlayed
        && (PlayedPercentage is > 0 and < 100 || ResumePositionTicks.GetValueOrDefault() > 0);

    public double ProgressValue
    {
        get
        {
            if (PlayedPercentage is > 0)
            {
                return Math.Clamp(PlayedPercentage.Value, 0d, 100d);
            }

            var runTimeTicks = RunTimeTicks.GetValueOrDefault();
            return runTimeTicks <= 0
                ? 0d
                : Math.Clamp(ResumePositionTicks.GetValueOrDefault() * 100d / runTimeTicks, 0d, 100d);
        }
    }

    internal void ApplyPlaybackState(long positionTicks, double? playedPercentage, bool isPlayed)
    {
        ResumePositionTicks = isPlayed ? 0 : Math.Max(0, positionTicks);
        PlayedPercentage = isPlayed ? 100d : playedPercentage;
        IsPlayed = isPlayed;
        OnPropertyChanged(nameof(ResumePositionTicks));
        OnPropertyChanged(nameof(PlayedPercentage));
        OnPropertyChanged(nameof(IsPlayed));
        OnPropertyChanged(nameof(PlayActionText));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(PlayAutomationName));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressValue));
    }

    private string GetWatchStateText()
    {
        if (IsPlayed)
        {
            return "已观看";
        }

        return HasProgress ? $"已观看 {ProgressValue:0}%" : "未观看";
    }

    private static string GetNumberText(EpisodeInfo episode)
    {
        if (episode.SeasonIndex is not null && episode.EpisodeIndex is not null)
        {
            return $"S{episode.SeasonIndex}:E{episode.EpisodeIndex}";
        }

        return episode.EpisodeIndex is null ? "单集" : $"第 {episode.EpisodeIndex.Value} 集";
    }

    private static string FormatDuration(long? runTimeTicks)
    {
        var ticks = runTimeTicks.GetValueOrDefault();
        if (ticks <= 0)
        {
            return "未知时长";
        }

        var timeSpan = TimeSpan.FromTicks(ticks);
        if (timeSpan.TotalHours >= 1)
        {
            return $"{(int)timeSpan.TotalHours} 小时 {timeSpan.Minutes} 分钟";
        }

        return $"{Math.Max(1, (int)Math.Round(timeSpan.TotalMinutes))} 分钟";
    }
}
