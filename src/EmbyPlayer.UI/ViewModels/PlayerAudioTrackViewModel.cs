using EmbyPlayer.Core.Playback;
using EmbyPlayer.Player;

namespace EmbyPlayer.UI.ViewModels;

public sealed class PlayerAudioTrackViewModel : ViewModelBase
{
    public string Codec => codec;
    private bool isSelected;
    private readonly string language;
    private readonly string codec;
    private readonly string displayTitle;
    private readonly bool isDefault;

    public PlayerAudioTrackViewModel(PlaybackTrack track)
    {
        MediaStreamIndex = track.Index;
        language = track.Language;
        codec = track.Codec;
        displayTitle = track.DisplayTitle;
        isDefault = track.IsDefault;
    }

    public PlayerAudioTrackViewModel(PlayerTrackInfo track)
    {
        MpvTrackId = track.Id;
        MediaStreamIndex = track.MediaStreamIndex;
        language = track.Language ?? string.Empty;
        codec = track.Codec ?? string.Empty;
        displayTitle = track.Title ?? string.Empty;
        isDefault = track.IsDefault == true;
    }

    public int? MpvTrackId { get; }

    public int? MediaStreamIndex { get; }

    public int? Index => MediaStreamIndex;

    public string Language => language;

    public string DisplayTitle => displayTitle;

    public bool IsDefault => isDefault;

    public string DisplayText
    {
        get
        {
            var title = string.IsNullOrWhiteSpace(displayTitle)
                ? "未命名音轨"
                : displayTitle;
            var languageText = string.IsNullOrWhiteSpace(language) ? "未知语言" : language;
            var codecText = string.IsNullOrWhiteSpace(codec) ? "未知编码" : codec;
            var defaultText = isDefault ? " · 默认" : string.Empty;
            return $"{title} · {languageText} · {codecText}{defaultText}";
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
