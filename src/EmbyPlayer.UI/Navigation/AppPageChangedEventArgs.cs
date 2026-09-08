namespace EmbyPlayer.UI.Navigation;

public sealed class AppPageChangedEventArgs : EventArgs
{
    public AppPageChangedEventArgs(AppPage previousPage, AppPage currentPage)
        : this(previousPage, currentPage, null)
    {
    }

    public AppPageChangedEventArgs(AppPage previousPage, AppPage currentPage, object? parameter)
    {
        PreviousPage = previousPage;
        CurrentPage = currentPage;
        Parameter = parameter;
    }

    public AppPage PreviousPage { get; }

    public AppPage CurrentPage { get; }

    public object? Parameter { get; }
}
