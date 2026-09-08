namespace EmbyPlayer.UI.ViewModels;

public sealed class SearchFilterViewModel : ViewModelBase
{
    private bool isSelected;
    private int count;

    public SearchFilterViewModel(string key, string label)
    {
        Key = key ?? string.Empty;
        Label = label ?? string.Empty;
    }

    public string Key { get; }

    public string Label { get; }

    public int Count
    {
        get => count;
        set
        {
            if (count == value)
            {
                return;
            }

            count = value;
            OnPropertyChanged();
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
