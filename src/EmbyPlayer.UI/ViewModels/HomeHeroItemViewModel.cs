namespace EmbyPlayer.UI.ViewModels;

public sealed class HomeHeroItemViewModel : ViewModelBase
{
    private bool isSelected;

    public HomeHeroItemViewModel(HomeMediaCardViewModel card, int position)
    {
        Card = card ?? throw new ArgumentNullException(nameof(card));
        Position = position;
    }

    public HomeMediaCardViewModel Card { get; }

    public int Position { get; }

    public string Title => Card.Title;

    public string AutomationName => $"\u663e\u793a\u7b2c {Position} \u4e2a\u63a8\u8350\uff1a{Title}";

    public bool IsSelected
    {
        get => isSelected;
        internal set
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
