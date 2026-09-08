using System.Diagnostics;
using System.Windows.Input;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.People;
using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed class PersonViewModel : ViewModelBase
{
    private const int PageSize = 48;
    private readonly INavigationService navigationService;
    private readonly IPersonService personService;
    private readonly ICurrentSessionService currentSessionService;
    private readonly IAuthSessionStore authSessionStore;
    private readonly Action<string> showLoginError;
    private PersonNavigationParameter? navigation;
    private AuthSession? loadedSession;
    private CancellationTokenSource? cancellation;
    private long generation;
    private bool hasWorksLoaded;
    private int nextStartIndex;
    private bool retryWorksAppend;

    public PersonViewModel(INavigationService navigationService, IPersonService personService,
        ICurrentSessionService currentSessionService, IAuthSessionStore authSessionStore, Action<string> showLoginError)
    {
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        this.personService = personService ?? throw new ArgumentNullException(nameof(personService));
        this.currentSessionService = currentSessionService ?? throw new ArgumentNullException(nameof(currentSessionService));
        this.authSessionStore = authSessionStore ?? throw new ArgumentNullException(nameof(authSessionStore));
        this.showLoginError = showLoginError ?? throw new ArgumentNullException(nameof(showLoginError));
        BackCommand = new RelayCommand(_ =>
        {
            if (navigation?.ReturnDetail is { } source) navigationService.NavigateTo(AppPage.Detail, source);
            else navigationService.NavigateTo(AppPage.Home);
        });
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading && !IsWorksLoading);
        RetryWorksCommand = new AsyncRelayCommand(() => LoadWorksAsync(retryWorksAppend), () => !IsLoading && !IsWorksLoading && Person is not null);
        LoadMoreCommand = new AsyncRelayCommand(() => LoadWorksAsync(true), () => HasMore && !IsLoading && !IsWorksLoading);
        OpenMediaCommand = new RelayCommand(parameter =>
        {
            if (parameter is LibraryMediaItemViewModel item && navigation is not null)
                navigationService.NavigateTo(AppPage.Detail,
                    new DetailNavigationParameter(item.Id, AppPage.Person, PersonReturnTarget: navigation));
        });
    }

    public PersonDetail? Person { get; private set; }
    public string Name => string.IsNullOrWhiteSpace(Person?.Name) ? "演职人员" : Person.Name;
    public string Overview => string.IsNullOrWhiteSpace(Person?.Overview) ? "暂无人物简介" : Person.Overview;
    public string? ImageUrl => Person?.ImageUrl;
    public IReadOnlyList<LibraryMediaItemViewModel> Works { get; private set; } = Array.Empty<LibraryMediaItemViewModel>();
    public bool IsLoading { get; private set; }
    public bool IsWorksLoading { get; private set; }
    public bool HasMore { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? WorksErrorMessage { get; private set; }
    public bool IsInitialLoading => IsLoading && Person is null;
    public bool IsContentVisible => Person is not null;
    public bool IsRefreshing => IsLoading && Person is not null;
    public bool IsErrorVisible => !IsLoading && Person is null && ErrorMessage is not null;
    public bool IsRefreshErrorVisible => !IsLoading && Person is not null && ErrorMessage is not null;
    public bool IsWorksInitialLoading => IsWorksLoading && !hasWorksLoaded;
    public bool IsWorksEmpty => hasWorksLoaded && !IsWorksLoading && Works.Count == 0 && WorksErrorMessage is null;
    public bool IsWorksErrorVisible => !IsWorksLoading && WorksErrorMessage is not null && Works.Count == 0;
    public bool IsWorksRefreshErrorVisible => !IsWorksLoading && WorksErrorMessage is not null && Works.Count > 0;
    public bool IsLoadingMore => IsWorksLoading && hasWorksLoaded;
    public bool IsLoadMoreVisible => HasMore && !IsWorksLoading;
    public double ScrollOffset { get; set; }
    public ICommand BackCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RetryWorksCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand OpenMediaCommand { get; }

    public Task LoadAsync(PersonNavigationParameter? parameter = null)
    {
        var session = currentSessionService.CurrentSession;
        if (session is null)
        {
            ResetSession();
            navigationService.NavigateTo(AppPage.Login);
            showLoginError("请先登录");
            return Task.CompletedTask;
        }
        var samePerson = ReferenceEquals(session, loadedSession)
            && navigation is not null && (parameter is null || parameter.PersonId == navigation.PersonId);
        if (samePerson && Person is not null)
        {
            navigation = parameter ?? navigation;
            return !hasWorksLoaded && !IsWorksLoading ? LoadWorksAsync(false) : Task.CompletedTask;
        }
        Deactivate();
        navigation = parameter ?? navigation;
        loadedSession = session;
        Person = null;
        Works = Array.Empty<LibraryMediaItemViewModel>();
        hasWorksLoaded = false;
        HasMore = false;
        nextStartIndex = 0;
        ErrorMessage = null;
        WorksErrorMessage = null;
        ScrollOffset = 0;
        OnPropertyChanged(nameof(ScrollOffset));
        if (string.IsNullOrWhiteSpace(navigation?.PersonId))
        {
            ErrorMessage = "未选择演职人员";
            NotifyState();
            return Task.CompletedTask;
        }
        return RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var session = currentSessionService.CurrentSession;
        if (session is null || !ReferenceEquals(session, loadedSession))
        {
            await LoadAsync(navigation).ConfigureAwait(true);
            return;
        }
        if (navigation is null) return;
        Deactivate();
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var requestGeneration = generation;
        IsLoading = true;
        ErrorMessage = null;
        NotifyState();
        try
        {
            var result = await personService.LoadPersonAsync(session, navigation.PersonId, token).ConfigureAwait(true);
            if (!IsCurrent(session, requestGeneration, token)) return;
            if (result.IsSuccess)
            {
                Person = result.Person;
                IsLoading = false;
                NotifyState();
                await LoadWorksAsync(false).ConfigureAwait(true);
            }
            else await HandleErrorAsync(result.Error, false, session, requestGeneration, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Trace.TraceError("Person detail loading failed: {0}", exception.GetType().Name);
            if (IsCurrent(session, requestGeneration, token)) ErrorMessage = "人物资料加载失败，请稍后重试";
        }
        finally
        {
            if (IsCurrent(session, requestGeneration, token))
            {
                IsLoading = false;
                NotifyState();
            }
        }
    }

    private async Task LoadWorksAsync(bool append)
    {
        if (IsWorksLoading || Person is null || navigation is null || (append && !HasMore)) return;
        var session = currentSessionService.CurrentSession;
        if (session is null || !ReferenceEquals(session, loadedSession))
        {
            await LoadAsync(navigation).ConfigureAwait(true);
            return;
        }
        cancellation ??= new CancellationTokenSource();
        var token = cancellation.Token;
        var requestGeneration = generation;
        IsWorksLoading = true;
        retryWorksAppend = append;
        WorksErrorMessage = null;
        NotifyState();
        try
        {
            var result = await personService.LoadWorksAsync(session, navigation.PersonId,
                append ? nextStartIndex : 0, PageSize, token).ConfigureAwait(true);
            if (!IsCurrent(session, requestGeneration, token)) return;
            if (result.IsSuccess)
            {
                Works = (append ? Works : Array.Empty<LibraryMediaItemViewModel>())
                    .Concat(result.Items.Select(item => new LibraryMediaItemViewModel(item)))
                    .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray();
                nextStartIndex = result.NextStartIndex;
                HasMore = result.HasMore;
                hasWorksLoaded = true;
            }
            else await HandleErrorAsync(result.Error, true, session, requestGeneration, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Trace.TraceError("Person works loading failed: {0}", exception.GetType().Name);
            if (IsCurrent(session, requestGeneration, token)) WorksErrorMessage = "相关作品加载失败，请稍后重试";
        }
        finally
        {
            if (IsCurrent(session, requestGeneration, token))
            {
                IsWorksLoading = false;
                NotifyState();
            }
        }
    }

    private async Task HandleErrorAsync(PersonLoadError error, bool works, AuthSession session, long requestGeneration, CancellationToken token)
    {
        if (error == PersonLoadError.Unauthorized)
        {
            await ExpiredSessionRecovery.ClearAsync(authSessionStore, currentSessionService, "person", session, token).ConfigureAwait(true);
            if (requestGeneration != generation || token.IsCancellationRequested || currentSessionService.CurrentSession is not null) return;
            ResetSession();
            navigationService.NavigateTo(AppPage.Login);
            showLoginError("登录状态已失效，请重新登录");
            return;
        }
        if (error == PersonLoadError.Cancelled) return;
        var message = error switch
        {
            PersonLoadError.Forbidden => "没有权限查看此人物的资料或作品",
            PersonLoadError.NotFound => "人物资料暂时不可用",
            PersonLoadError.ServerTimeout => "加载超时，请稍后重试",
            PersonLoadError.ServerUnreachable => "无法连接服务器，请检查网络",
            _ => "加载失败，请稍后重试"
        };
        if (works) WorksErrorMessage = message;
        else ErrorMessage = message;
    }

    public void Deactivate()
    {
        generation++;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        IsLoading = false;
        IsWorksLoading = false;
        NotifyState();
    }

    public void ResetSession()
    {
        Deactivate();
        navigation = null;
        loadedSession = null;
        Person = null;
        Works = Array.Empty<LibraryMediaItemViewModel>();
        hasWorksLoaded = false;
        HasMore = false;
        ErrorMessage = null;
        WorksErrorMessage = null;
        ScrollOffset = 0;
        NotifyState();
    }

    private bool IsCurrent(AuthSession session, long requestGeneration, CancellationToken token)
        => generation == requestGeneration && !token.IsCancellationRequested
            && ReferenceEquals(session, loadedSession) && ReferenceEquals(session, currentSessionService.CurrentSession);

    private void NotifyState()
    {
        foreach (var name in new[] { nameof(Person), nameof(Name), nameof(Overview), nameof(ImageUrl), nameof(Works),
            nameof(IsLoading), nameof(IsWorksLoading), nameof(HasMore), nameof(ErrorMessage), nameof(WorksErrorMessage),
            nameof(IsInitialLoading), nameof(IsContentVisible), nameof(IsRefreshing), nameof(IsErrorVisible), nameof(IsRefreshErrorVisible),
            nameof(IsWorksInitialLoading), nameof(IsWorksEmpty), nameof(IsWorksErrorVisible), nameof(IsWorksRefreshErrorVisible),
            nameof(IsLoadingMore), nameof(IsLoadMoreVisible) }) OnPropertyChanged(name);
        ((AsyncRelayCommand)RefreshCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)RetryWorksCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)LoadMoreCommand).NotifyCanExecuteChanged();
    }
}
