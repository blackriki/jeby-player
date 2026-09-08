namespace EmbyPlayer.UI.Navigation;

public sealed class NavigationService : INavigationService
{
    public event EventHandler<AppPageNavigatingEventArgs>? Navigating;

    public event EventHandler<AppPageChangedEventArgs>? CurrentPageChanged;

    public AppPage CurrentPage { get; private set; } = AppPage.ServerConnection;

    public void NavigateTo(AppPage page)
    {
        NavigateTo(page, null);
    }

    public void NavigateTo(AppPage page, object? parameter)
    {
        if (page == CurrentPage && parameter is null)
        {
            return;
        }

        var navigatingArgs = new AppPageNavigatingEventArgs(CurrentPage, page, parameter);
        Navigating?.Invoke(this, navigatingArgs);
        if (navigatingArgs.Cancel)
        {
            return;
        }

        var previousPage = CurrentPage;
        CurrentPage = page;
        CurrentPageChanged?.Invoke(this, new AppPageChangedEventArgs(previousPage, CurrentPage, parameter));
    }
}
