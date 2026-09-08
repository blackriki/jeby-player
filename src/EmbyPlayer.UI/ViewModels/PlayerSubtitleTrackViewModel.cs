using EmbyPlayer.Core.Playback;
using EmbyPlayer.Player;

namespace EmbyPlayer.UI.ViewModels;

public sealed class PlayerSubtitleTrackViewModel : ViewModelBase
{
    public string Codec => codec;
    private bool isSelected;
    private readonly string language;
    private readonly string codec;
    private readonly string displayTitle;
    private readonly bool isDefault;
    private readonly bool isExternal;

    public PlayerSubtitleTrackViewModel(PlaybackSubtitle? subtitle)
    {
        IsOffOption = subtitle is null;
        MediaStreamIndex = subtitle?.Index;
        language = subtitle?.Language ?? string.Empty;
        codec = subtitle?.Codec ?? string.Empty;
        displayTitle = subtitle?.DisplayTitle ?? string.Empty;
        isDefault = subtitle?.IsDefault == true;
        isExternal = subtitle?.IsExternal == true;
    }

    public PlayerSubtitleTrackViewModel(PlayerTrackInfo track)
    {
        LocalFilePath = track.LocalFilePath;
        MpvTrackId = track.Id;
        MediaStreamIndex = track.MediaStreamIndex;
        language = track.Language ?? string.Empty;
        codec = track.Codec ?? string.Empty;
        displayTitle = track.Title ?? string.Empty;
        isDefault = track.IsDefault == true;
        isExternal = track.IsExternal == true;
    }

    public int? MpvTrackId { get; }

    public string? LocalFilePath { get; }

    public int? MediaStreamIndex { get; }

    public int? Index => MediaStreamIndex;

    public string Language => language;

    public string DisplayTitle => displayTitle;

    public bool IsDefault => isDefault;

    public bool IsExternal => isExternal;

    public bool IsOffOption { get; }

    public string DisplayText
    {
        get
        {
            if (IsOffOption)
            {
                return "关闭字幕";
            }

            var title = string.IsNullOrWhiteSpace(displayTitle)
                ? "未命名字幕"
                : displayTitle;
            var languageText = string.IsNullOrWhiteSpace(language) ? "未知语言" : language;
            var codecText = string.IsNullOrWhiteSpace(codec) ? "未知编码" : codec;
            var source = LocalFilePath is not null ? "本地" : isExternal ? "外挂" : "内封";
            var defaultText = isDefault ? " · 默认" : string.Empty;
            return $"{title} · {languageText} · {codecText} · {source}{defaultText}";
        }
    }

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
}
