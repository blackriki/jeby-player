using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed class NavigationItemViewModel
{
    public NavigationItemViewModel(string label, AppPage page, bool isPlaceholder = false)
    {
        Label = label;
        Page = page;
        IsPlaceholder = isPlaceholder;
    }

    public string Label { get; }

    public AppPage Page { get; }

    public bool IsPlaceholder { get; }
}
