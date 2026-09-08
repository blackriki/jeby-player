using EmbyPlayer.Core.Series;

namespace EmbyPlayer.UI.ViewModels;

public sealed class SeasonInfoViewModel : ViewModelBase
{
    private bool isSelected;

    public SeasonInfoViewModel(SeasonInfo season)
    {
        Source = season ?? throw new ArgumentNullException(nameof(season));
        Id = season.Id;
        Name = string.IsNullOrWhiteSpace(season.Name)
            ? season.IndexNumber is null ? "未命名季" : $"第 {season.IndexNumber.Value} 季"
            : season.Name;
        IndexNumber = season.IndexNumber;
        HasResumeOrUnwatched = season.HasResumeOrUnwatched;
    }

    public SeasonInfo Source { get; }

    public string Id { get; }

    public string Name { get; }

    public int? IndexNumber { get; }

    public bool HasResumeOrUnwatched { get; }

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
