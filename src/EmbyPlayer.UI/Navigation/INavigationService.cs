namespace EmbyPlayer.UI.Navigation;

public interface INavigationService
{
    event EventHandler<AppPageNavigatingEventArgs>? Navigating;

    event EventHandler<AppPageChangedEventArgs>? CurrentPageChanged;

    AppPage CurrentPage { get; }

    void NavigateTo(AppPage page);

    void NavigateTo(AppPage page, object? parameter);
}
