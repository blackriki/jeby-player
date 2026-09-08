using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed class PlaceholderPageViewModel
{
    public PlaceholderPageViewModel(AppPage page, string title, string description, string statusText)
    {
        Page = page;
        Title = title;
        Description = description;
        StatusText = statusText;
    }

    public AppPage Page { get; }

    public string Title { get; }

    public string Description { get; }

    public string StatusText { get; }
}
