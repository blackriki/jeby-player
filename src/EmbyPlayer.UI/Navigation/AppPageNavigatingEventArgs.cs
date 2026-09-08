namespace EmbyPlayer.UI.Navigation;

public sealed class AppPageNavigatingEventArgs : EventArgs
{
    public AppPageNavigatingEventArgs(AppPage currentPage, AppPage targetPage, object? parameter)
    {
        CurrentPage = currentPage;
        TargetPage = targetPage;
        Parameter = parameter;
    }

    public AppPage CurrentPage { get; }

    public AppPage TargetPage { get; }

    public object? Parameter { get; }

    public bool Cancel { get; set; }
}
