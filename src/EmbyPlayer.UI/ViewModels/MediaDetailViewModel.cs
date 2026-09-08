using System.Windows.Input;
using EmbyPlayer.Core.Authentication;
using EmbyPlayer.Core.Details;
using EmbyPlayer.Core.Playback;
using EmbyPlayer.Core.Search;
using EmbyPlayer.Core.Series;
using EmbyPlayer.Core.UserData;
using EmbyPlayer.UI.Navigation;

namespace EmbyPlayer.UI.ViewModels;

public sealed partial class MediaDetailViewModel : ViewModelBase
{
    private const double EpisodeCompletionThreshold = 90d;
    private const string SessionExpiredMessage = "登录状态已失效，请重新登录";
    private readonly IAuthSessionStore authSessionStore;
    private readonly ICurrentSessionService currentSessionService;
    private readonly IItemUserDataService itemUserDataService;
    private readonly ILocalMediaSearchIndex? localMediaSearchIndex;
    private readonly IMediaDetailService mediaDetailService;
    private readonly ISimilarMediaService similarMediaService;
    private readonly INavigationService navigationService;
    private readonly IPlaybackService playbackService;
    private readonly ISeriesService seriesService;
    private readonly Action<string> showLoginError;
    private DetailBackTarget? backTarget;
    private PersonNavigationParameter? personReturnTarget;
    private CancellationTokenSource? episodeLoadCancellation;
    private CancellationTokenSource? mediaLoadCancellation;
    private string? currentItemId;
    private AppPage returnPage = AppPage.Home;
    private MediaDetail? detail;
    private string? episodeErrorMessage;
    private string? errorMessage;
    private bool isEpisodesLoading;
    private bool isFavorite;
    private bool isFavoriteChanging;
    private bool isLoading;
    private bool isPlayed;
    private bool isPlayedChanging;
    private bool isPreparingPlayback;
    private bool isSeasonsLoading;
    private IReadOnlyList<EpisodeInfoViewModel> episodes = Array.Empty<EpisodeInfoViewModel>();
    private long episodeLoadGeneration;
    private PlaybackStartRequest? lastPlaybackRequest;
    private string? playbackErrorMessage;
    private PlaybackReturnState? playbackReturnState;
    private string? playbackSyncWarningMessage;
    private string? playedErrorMessage;
    private IReadOnlyList<SeasonInfoViewModel> seasons = Array.Empty<SeasonInfoViewModel>();
    private EpisodeInfoViewModel? selectedEpisode;
    private SeasonInfoViewModel? selectedSeason;
    private string? favoriteErrorMessage;
    private string? fallbackItemId;
    private long mediaLoadGeneration;
    private string? preferredSeriesPlaybackEpisodeId;
    private string? requestedEpisodeId;
    private string? seasonErrorMessage;
    private bool shouldBringEpisodeSectionIntoView;
    private IReadOnlyList<SimilarMediaItemViewModel> similarItems = Array.Empty<SimilarMediaItemViewModel>();
    private string? similarErrorMessage;
    private bool isSimilarLoading;

    public MediaDetailViewModel(
        INavigationService navigationService,
        IMediaDetailService mediaDetailService,
        ISimilarMediaService similarMediaService,
        IItemUserDataService itemUserDataService,
        ISeriesService seriesService,
        IPlaybackService playbackService,
        ICurrentSessionService currentSessionService,
        IAuthSessionStore authSessionStore,
        Action<string> showLoginError,
        ILocalMediaSearchIndex? localMediaSearchIndex = null,
        EmbyPlayer.Core.WatchLater.IWatchLaterStore? watchLaterStore = null,
        EmbyPlayer.Core.PlaybackQueue.IPlaybackQueueService? playbackQueue = null)
    {
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        this.mediaDetailService = mediaDetailService ?? throw new ArgumentNullException(nameof(mediaDetailService));
        this.similarMediaService = similarMediaService ?? throw new ArgumentNullException(nameof(similarMediaService));
        this.itemUserDataService = itemUserDataService ?? throw new ArgumentNullException(nameof(itemUserDataService));
        this.seriesService = seriesService ?? throw new ArgumentNullException(nameof(seriesService));
        this.playbackService = playbackService ?? throw new ArgumentNullException(nameof(playbackService));
        this.currentSessionService = currentSessionService ?? throw new ArgumentNullException(nameof(currentSessionService));
        this.authSessionStore = authSessionStore ?? throw new ArgumentNullException(nameof(authSessionStore));
        this.showLoginError = showLoginError ?? throw new ArgumentNullException(nameof(showLoginError));
        this.localMediaSearchIndex = localMediaSearchIndex;
        InitializePersonalMedia(watchLaterStore, playbackQueue);

        RetryCommand = new AsyncRelayCommand(RetryAsync, () => !IsLoading && !string.IsNullOrWhiteSpace(currentItemId));
        RetrySeasonsCommand = new AsyncRelayCommand(RetrySeasonsAsync, () => !IsSeasonsLoading && IsSeriesDetail);
        RetryEpisodesCommand = new AsyncRelayCommand(RetryEpisodesAsync, () => !IsEpisodesLoading && SelectedSeason is not null);
        RetrySimilarCommand = new AsyncRelayCommand(RetrySimilarAsync, CanRetrySimilar);
        RetryPlaybackCommand = new AsyncRelayCommand(RetryPlaybackAsync, CanRetryPlayback);
        BackCommand = new RelayCommand(_ => NavigateBack());
        OpenPersonCommand = new RelayCommand(parameter =>
        {
            if (parameter is MediaPerson person && IsPeopleSectionVisible && !string.IsNullOrWhiteSpace(person.Id))
            {
                navigationService.NavigateTo(AppPage.Person,
                    new PersonNavigationParameter(person.Id, CreateCurrentDetailParameter()));
            }
        });
        SelectSeasonCommand = new RelayCommand(parameter =>
        {
            if (parameter is SeasonInfoViewModel season)
            {
                _ = SelectSeasonAsync(season);
            }
        });
        OpenEpisodeCommand = new RelayCommand(parameter =>
        {
            if (parameter is EpisodeInfoViewModel episode)
            {
                SelectedEpisode = episode;
                navigationService.NavigateTo(
                    AppPage.Detail,
                    new DetailNavigationParameter(
                        episode.Id,
                        BackTarget: new DetailBackTarget(
                            AppPage.Detail,
                            currentItemId,
                            returnPage,
                            SelectedSeason?.Id,
                            episode.Id,
                            personReturnTarget),
                        PersonReturnTarget: personReturnTarget));
            }
        });
        OpenSimilarCommand = new RelayCommand(parameter =>
        {
            if (parameter is SimilarMediaItemViewModel item
                && IsSupplementarySectionVisible
                && !string.IsNullOrWhiteSpace(currentItemId))
            {
                navigationService.NavigateTo(
                    AppPage.Detail,
                    new DetailNavigationParameter(
                        item.Id,
                        returnPage,
                        BackTarget: new DetailBackTarget(
                            AppPage.Detail,
                            currentItemId,
                            returnPage,
                            SelectedSeason?.Id,
                            SelectedEpisode?.Id,
                            personReturnTarget),
                        PersonReturnTarget: personReturnTarget));
            }
        });
        PlayCommand = new AsyncRelayCommand(() => PreparePlaybackAsync(0), CanStartPlayback);
        ContinuePlayCommand = new AsyncRelayCommand(
            () => PreparePlaybackAsync(Detail?.ResumePositionTicks ?? 0),
            CanContinuePlayback);
        RestartPlayCommand = new AsyncRelayCommand(() => PreparePlaybackAsync(0), CanStartPlayback);
        SeriesPlayCommand = new AsyncRelayCommand(PlaySeriesTargetAsync, CanPlaySeriesTarget);
        PlayEpisodeCommand = new AsyncRelayCommand(PlayEpisodeAsync, CanPlayEpisode);
        ToggleFavoriteCommand = new AsyncRelayCommand(ToggleFavoriteAsync, CanToggleFavorite);
        TogglePlayedCommand = new AsyncRelayCommand(TogglePlayedAsync, CanTogglePlayed);
    }

    public bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (isLoading == value)
            {
                return;
            }

            isLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInitialLoading));
            OnPropertyChanged(nameof(IsRefreshing));
            OnPropertyChanged(nameof(IsContentVisible));
            OnPropertyChanged(nameof(IsErrorVisible));
            OnPropertyChanged(nameof(IsRefreshErrorVisible));
            OnPlaybackAvailabilityChanged();
            OnSeriesStateChanged();
            NotifyCommandsCanExecuteChanged();
        }
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set
        {
            if (errorMessage == value)
            {
                return;
            }

            errorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(IsErrorVisible));
            OnPropertyChanged(nameof(IsRefreshErrorVisible));
            OnPropertyChanged(nameof(IsContentVisible));
            OnPlaybackAvailabilityChanged();
            OnSeriesStateChanged();
        }
    }

    public MediaDetail? Detail
    {
        get => detail;
        private set
        {
            detail = value;
            isFavorite = value?.IsFavorite ?? false;
            isPlayed = value?.IsPlayed ?? false;
            OnPropertyChanged();
            OnDetailPropertiesChanged();
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsInitialLoading => IsLoading && Detail is null;

    public bool IsRefreshing => IsLoading && Detail is not null;

    public bool IsErrorVisible => HasError && !IsLoading && Detail is null;

    public bool IsRefreshErrorVisible => HasError && !IsLoading && Detail is not null;

    public bool IsContentVisible => Detail is not null;

    public bool IsSeriesDetail => string.Equals(Detail?.Type, "Series", StringComparison.OrdinalIgnoreCase);

    public bool IsSeriesSectionVisible => IsContentVisible && IsSeriesDetail;

    public bool IsSupplementarySectionVisible => IsContentVisible
        && (IsSeriesDetail || string.Equals(Detail?.Type, "Movie", StringComparison.OrdinalIgnoreCase));

    public bool ShouldBringEpisodeSectionIntoView
    {
        get => shouldBringEpisodeSectionIntoView;
        private set
        {
            if (shouldBringEpisodeSectionIntoView == value)
            {
                return;
            }

            shouldBringEpisodeSectionIntoView = value;
            OnPropertyChanged();
        }
    }

    public string Title => string.IsNullOrWhiteSpace(Detail?.Title) ? "未命名媒体" : Detail.Title;

    public string YearText => Detail?.Year is null ? "未知年份" : Detail.Year.Value.ToString();

    public string TypeText => GetTypeLabel(Detail?.Type);

    public string DurationText => FormatDuration(Detail?.RunTimeTicks);

    public string OverviewText => string.IsNullOrWhiteSpace(Detail?.Overview) ? "暂无简介" : Detail.Overview!;

    public string GenresText => Detail?.Genres.Count > 0 ? string.Join(" / ", Detail.Genres) : "暂无类型标签";

    public string RatingText => Detail?.CommunityRating is null ? "暂无评分" : Detail.CommunityRating.Value.ToString("0.0");

    public string ProgressText => HasProgress ? $"已观看 {ProgressValue:0}%" : "暂无播放进度";

    public double ProgressValue => Math.Clamp(Detail?.PlayedPercentage ?? 0d, 0d, 100d);

    public bool HasProgress => Detail?.PlayedPercentage is > 0 and < 100;

    public bool IsPlayableMedia => IsPlayableType(Detail?.Type);

    public bool IsPlaybackControlsVisible => IsContentVisible && IsPlayableMedia;

    public bool IsStandardPlaybackPanelVisible => IsPlaybackControlsVisible && !IsSeriesDetail;

    public bool IsStandardProgressVisible => HasProgress && !IsSeriesDetail;

    public bool IsPlaybackUnavailableVisible => IsContentVisible && !IsPlayableMedia;

    public bool IsNonSeriesPlaybackUnavailableVisible => IsPlaybackUnavailableVisible && !IsSeriesDetail;

    public string PlaybackUnavailableText => "请选择具体单集播放";

    public bool IsContinuePlaybackVisible => IsPlayableMedia && Detail?.ResumePositionTicks is > 0;

    public bool IsFavorite => isFavorite;

    public bool IsFavoriteChanging
    {
        get => isFavoriteChanging;
        private set
        {
            if (isFavoriteChanging == value)
            {
                return;
            }

            isFavoriteChanging = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FavoriteButtonText));
            NotifyCommandsCanExecuteChanged();
        }
    }

    public string FavoriteButtonText => IsFavorite
        ? "\u5df2\u6536\u85cf"
        : "\u6536\u85cf";

    public string? FavoriteErrorMessage
    {
        get => favoriteErrorMessage;
        private set
        {
            if (favoriteErrorMessage == value)
            {
                return;
            }

            favoriteErrorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFavoriteErrorVisible));
        }
    }

    public bool IsFavoriteErrorVisible => !string.IsNullOrWhiteSpace(FavoriteErrorMessage);

    public bool IsPlayed => isPlayed;

    public bool IsPlayedChanging
    {
        get => isPlayedChanging;
        private set
        {
            if (isPlayedChanging == value)
            {
                return;
            }

            isPlayedChanging = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlayedButtonText));
            NotifyCommandsCanExecuteChanged();
        }
    }

    public string PlayedButtonText => IsPlayedChanging
        ? "正在更新..."
        : IsPlayed
            ? "标记未观看"
            : "标记已观看";

    public string? PlayedErrorMessage
    {
        get => playedErrorMessage;
        private set
        {
            if (playedErrorMessage == value)
            {
                return;
            }

            playedErrorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlayedErrorVisible));
        }
    }

    public bool IsPlayedErrorVisible => !string.IsNullOrWhiteSpace(PlayedErrorMessage);

    public string? PosterUrl => Detail?.PosterUrl;

    public string? BackdropUrl => Detail?.BackdropUrl;

    public string? SelectedEpisodeHeroUrl => IsSeriesDetail ? SelectedEpisode?.HeroImageUrl : null;

    public string? HeroBackdropUrl => IsSeriesDetail
        ? SelectedEpisodeHeroUrl ?? BackdropUrl
        : BackdropUrl;

    public IReadOnlyList<MediaPerson> People => IsSupplementarySectionVisible
        ? Detail?.People ?? Array.Empty<MediaPerson>()
        : Array.Empty<MediaPerson>();

    public IReadOnlyList<string> ArtworkUrls => IsSupplementarySectionVisible
        ? Detail?.ArtworkUrls ?? Array.Empty<string>()
        : Array.Empty<string>();

    public bool IsPeopleSectionVisible => IsSupplementarySectionVisible && People.Count > 0;

    public bool IsArtworkSectionVisible => IsSupplementarySectionVisible && ArtworkUrls.Count > 0;

    public IReadOnlyList<SimilarMediaItemViewModel> SimilarItems
    {
        get => similarItems;
        private set
        {
            similarItems = value;
            OnPropertyChanged();
            OnSimilarStateChanged();
        }
    }

    public bool IsSimilarLoading
    {
        get => isSimilarLoading;
        private set
        {
            if (isSimilarLoading == value)
            {
                return;
            }

            isSimilarLoading = value;
            OnPropertyChanged();
            OnSimilarStateChanged();
        }
    }

    public string? SimilarErrorMessage
    {
        get => similarErrorMessage;
        private set
        {
            if (similarErrorMessage == value)
            {
                return;
            }

            similarErrorMessage = value;
            OnPropertyChanged();
            OnSimilarStateChanged();
        }
    }

    public bool HasSimilarError => !string.IsNullOrWhiteSpace(SimilarErrorMessage);

    public bool IsSimilarInitialLoading => IsSupplementarySectionVisible
        && IsSimilarLoading
        && SimilarItems.Count == 0;

    public bool IsSimilarEmpty => IsSupplementarySectionVisible
        && !IsSimilarLoading
        && !HasSimilarError
        && SimilarItems.Count == 0;

    public bool IsSimilarErrorVisible => IsSupplementarySectionVisible
        && !IsSimilarLoading
        && HasSimilarError;

    public bool IsSimilarItemsVisible => IsSupplementarySectionVisible && SimilarItems.Count > 0;

    public bool IsSimilarSectionVisible => IsSupplementarySectionVisible
        && (IsSimilarLoading || HasSimilarError || SimilarItems.Count > 0);

    public bool IsPreparingPlayback
    {
        get => isPreparingPlayback;
        private set
        {
            if (isPreparingPlayback == value)
            {
                return;
            }

            isPreparingPlayback = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlaybackErrorVisible));
            NotifyCommandsCanExecuteChanged();
        }
    }

    public string? PlaybackErrorMessage
    {
        get => playbackErrorMessage;
        private set
        {
            if (playbackErrorMessage == value)
            {
                return;
            }

            playbackErrorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlaybackErrorVisible));
            NotifyCommandsCanExecuteChanged();
        }
    }

    public bool IsPlaybackErrorVisible => !string.IsNullOrWhiteSpace(PlaybackErrorMessage)
        && !IsPreparingPlayback;

    public string? PlaybackSyncWarningMessage
    {
        get => playbackSyncWarningMessage;
        private set
        {
            if (playbackSyncWarningMessage == value)
            {
                return;
            }

            playbackSyncWarningMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlaybackSyncWarningVisible));
        }
    }

    public bool IsPlaybackSyncWarningVisible => !string.IsNullOrWhiteSpace(PlaybackSyncWarningMessage);

    public bool IsSeasonsLoading
    {
        get => isSeasonsLoading;
        private set
        {
            if (isSeasonsLoading == value)
            {
                return;
            }

            isSeasonsLoading = value;
            OnPropertyChanged();
            OnSeriesStateChanged();
            NotifyCommandsCanExecuteChanged();
        }
    }

    public bool IsEpisodesLoading
    {
        get => isEpisodesLoading;
        private set
        {
            if (isEpisodesLoading == value)
            {
                return;
            }

            isEpisodesLoading = value;
            OnPropertyChanged();
            OnSeriesStateChanged();
            NotifyCommandsCanExecuteChanged();
        }
    }

    public string? SeasonErrorMessage
    {
        get => seasonErrorMessage;
        private set
        {
            if (seasonErrorMessage == value)
            {
                return;
            }

            seasonErrorMessage = value;
            OnPropertyChanged();
            OnSeriesStateChanged();
        }
    }

    public string? EpisodeErrorMessage
    {
        get => episodeErrorMessage;
        private set
        {
            if (episodeErrorMessage == value)
            {
                return;
            }

            episodeErrorMessage = value;
            OnPropertyChanged();
            OnSeriesStateChanged();
        }
    }

    public bool HasSeasonError => !string.IsNullOrWhiteSpace(SeasonErrorMessage);

    public bool HasEpisodeError => !string.IsNullOrWhiteSpace(EpisodeErrorMessage);

    public bool IsSeasonErrorVisible => IsSeriesSectionVisible && HasSeasonError && !IsSeasonsLoading && Seasons.Count == 0;

    public bool IsEpisodeErrorVisible => IsSeriesSectionVisible && HasEpisodeError && !IsEpisodesLoading && Episodes.Count == 0;

    public bool IsInitialSeasonsLoading => IsSeriesSectionVisible && IsSeasonsLoading && Seasons.Count == 0;

    public bool IsInitialEpisodesLoading => IsSeriesSectionVisible && IsEpisodesLoading && Episodes.Count == 0;

    public bool IsRefreshingSeasons => IsSeriesSectionVisible && IsSeasonsLoading && Seasons.Count > 0;

    public bool IsRefreshingEpisodes => IsSeriesSectionVisible && IsEpisodesLoading && Episodes.Count > 0;

    public bool IsSeasonRefreshErrorVisible => IsSeriesSectionVisible && HasSeasonError && !IsSeasonsLoading && Seasons.Count > 0;

    public bool IsEpisodeRefreshErrorVisible => IsSeriesSectionVisible && HasEpisodeError && !IsEpisodesLoading && Episodes.Count > 0;

    public bool IsSeasonsEmpty => IsSeriesSectionVisible && !IsSeasonsLoading && !HasSeasonError && Seasons.Count == 0;

    public bool IsEpisodesEmpty => IsSeriesSectionVisible
        && SelectedSeason is not null
        && !IsEpisodesLoading
        && !HasEpisodeError
        && Episodes.Count == 0;

    public bool IsSeasonsListVisible => IsSeriesSectionVisible && Seasons.Count > 0;

    public bool IsEpisodesListVisible => IsSeriesSectionVisible && Episodes.Count > 0;

    public bool IsSeriesActionPanelVisible => IsSeriesSectionVisible;

    public bool IsSeriesPrimaryButtonVisible => IsSeriesActionPanelVisible && GetSeriesPlaybackTarget() is not null;

    public string SeriesPrimaryButtonText => GetSeriesPlaybackTarget()?.ButtonText ?? string.Empty;

    public string SeriesPlaybackTargetText => GetSeriesPlaybackTarget()?.TargetText ?? "当前季暂时没有可播放单集";

    public bool IsSeriesPlaybackTargetVisible => IsSeriesActionPanelVisible
        && !IsSeasonsLoading
        && !IsEpisodesLoading
        && !HasSeasonError
        && !HasEpisodeError
        && !string.IsNullOrWhiteSpace(SeriesPlaybackTargetText);

    public bool IsSeriesResumeProgressVisible => GetSeriesPlaybackTarget()?.Intent == SeriesPlaybackIntent.Resume;

    public string SeriesResumeProgressText
    {
        get
        {
            var target = GetSeriesPlaybackTarget();
            return target is null ? string.Empty : $"已观看 {target.Episode.ProgressValue:0}%";
        }
    }

    public string SeriesResumePositionText
    {
        get
        {
            var target = GetSeriesPlaybackTarget();
            return target is null ? string.Empty : $"继续位置 {FormatPosition(target.StartPositionTicks)}";
        }
    }

    public double SeriesResumeProgressValue => GetSeriesPlaybackTarget()?.Episode.ProgressValue ?? 0d;

    public IReadOnlyList<SeasonInfoViewModel> Seasons
    {
        get => seasons;
        private set
        {
            seasons = value;
            OnPropertyChanged();
            OnSeriesStateChanged();
        }
    }

    public SeasonInfoViewModel? SelectedSeason
    {
        get => selectedSeason;
        private set
        {
            if (selectedSeason == value)
            {
                return;
            }

            if (selectedSeason is not null)
            {
                selectedSeason.IsSelected = false;
            }

            selectedSeason = value;
            if (selectedSeason is not null)
            {
                selectedSeason.IsSelected = true;
            }

            OnPropertyChanged();
            OnSeriesStateChanged();
            NotifyCommandsCanExecuteChanged();
        }
    }

    public IReadOnlyList<EpisodeInfoViewModel> Episodes
    {
        get => episodes;
        private set
        {
            episodes = value;
            OnPropertyChanged();
            OnSeriesStateChanged();
        }
    }

    public EpisodeInfoViewModel? SelectedEpisode
    {
        get => selectedEpisode;
        set
        {
            if (ReferenceEquals(selectedEpisode, value))
            {
                return;
            }

            if (selectedEpisode is not null)
            {
                selectedEpisode.IsNavigationTarget = false;
            }

            selectedEpisode = value;
            if (selectedEpisode is not null)
            {
                selectedEpisode.IsNavigationTarget = true;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(FocusedEpisode));
            OnPropertyChanged(nameof(SelectedEpisodeHeroUrl));
            OnPropertyChanged(nameof(HeroBackdropUrl));
            OnSeriesPlaybackTargetChanged();
            NotifyCommandsCanExecuteChanged();
        }
    }

    public EpisodeInfoViewModel? FocusedEpisode => SelectedEpisode;

    public ICommand RetryCommand { get; }

    public ICommand RetrySeasonsCommand { get; }

    public ICommand RetryEpisodesCommand { get; }

    public ICommand RetrySimilarCommand { get; }

    public ICommand RetryPlaybackCommand { get; }

    public ICommand BackCommand { get; }

    public ICommand SelectSeasonCommand { get; }

    public ICommand OpenEpisodeCommand { get; }

    public ICommand OpenSimilarCommand { get; }

    public ICommand OpenPersonCommand { get; }

    public ICommand PlayCommand { get; }

    public ICommand ContinuePlayCommand { get; }

    public ICommand RestartPlayCommand { get; }

    public ICommand SeriesPlayCommand { get; }

    public ICommand PlayEpisodeCommand { get; }

    public ICommand ToggleFavoriteCommand { get; }

    public ICommand TogglePlayedCommand { get; }

    public void CancelPendingLoad()
    {
        mediaLoadGeneration++;
        mediaLoadCancellation?.Cancel();
        mediaLoadCancellation?.Dispose();
        mediaLoadCancellation = null;

        episodeLoadGeneration++;
        episodeLoadCancellation?.Cancel();
        episodeLoadCancellation?.Dispose();
        episodeLoadCancellation = null;

        IsLoading = false;
        IsSeasonsLoading = false;
        IsEpisodesLoading = false;
        IsSimilarLoading = false;
    }

    public async Task LoadAsync(string? itemId, AppPage? requestedReturnPage = null)
    {
        await LoadAsync(new DetailNavigationParameter(itemId, requestedReturnPage)).ConfigureAwait(true);
    }

    public async Task LoadAsync(DetailNavigationParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var requestGeneration = BeginMediaLoad();
        var cancellationToken = mediaLoadCancellation!.Token;

        if (parameter.ReturnPage is AppPage.Home or AppPage.HomeSection or AppPage.Library or AppPage.Search or AppPage.Person or AppPage.WatchLater or AppPage.PlaybackQueue)
        {
            returnPage = parameter.ReturnPage.Value;
        }

        backTarget = parameter.BackTarget;
        personReturnTarget = parameter.PersonReturnTarget;
        fallbackItemId = parameter.FallbackItemId;
        requestedEpisodeId = parameter.FocusedEpisodeId;
        preferredSeriesPlaybackEpisodeId = parameter.FocusedEpisodeId;
        ShouldBringEpisodeSectionIntoView = !string.IsNullOrWhiteSpace(parameter.FocusedEpisodeId);
        var isRefreshingCurrentItem = Detail is not null
            && string.Equals(currentItemId, parameter.ItemId, StringComparison.OrdinalIgnoreCase);
        UpdatePlaybackReturnState(parameter);
        if (isRefreshingCurrentItem)
        {
            ApplyPlaybackReturnStateToCurrentView();
        }

        currentItemId = parameter.ItemId;
        ResetPersonalMediaState();
        NotifyCommandsCanExecuteChanged();

        if (string.IsNullOrWhiteSpace(parameter.ItemId))
        {
            Detail = null;
            ClearSeriesState();
            ClearSimilarState();
            ErrorMessage = "未选择媒体";
            IsLoading = false;
            return;
        }

        var session = currentSessionService.CurrentSession;
        if (session is null)
        {
            IsLoading = false;
            navigationService.NavigateTo(AppPage.Login);
            showLoginError("请先登录");
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        PlaybackErrorMessage = null;
        FavoriteErrorMessage = null;
        PlayedErrorMessage = null;
        lastPlaybackRequest = null;
        if (!isRefreshingCurrentItem)
        {
            Detail = null;
            ClearSeriesState();
            ClearSimilarState();
        }

        try
        {
            var result = await mediaDetailService
                .LoadDetailAsync(session, parameter.ItemId, cancellationToken)
                .ConfigureAwait(true);

            if (!IsCurrentMediaLoad(requestGeneration, cancellationToken))
            {
                return;
            }

            if (result.IsSuccess && result.Detail is not null)
            {
                Detail = MergePlaybackReturnState(result.Detail);
                await RefreshWatchLaterAsync().ConfigureAwait(true);
            }
            else if (result.Error == MediaDetailLoadError.Unauthorized)
            {
                await HandleExpiredSessionAsync().ConfigureAwait(true);
                return;
            }
            else
            {
                if (result.Error == MediaDetailLoadError.NotFound
                    && !string.IsNullOrWhiteSpace(fallbackItemId)
                    && !string.Equals(fallbackItemId, parameter.ItemId, StringComparison.OrdinalIgnoreCase))
                {
                    navigationService.NavigateTo(
                        AppPage.Detail,
                        new DetailNavigationParameter(
                            fallbackItemId,
                            returnPage,
                            backTarget,
                            PersonReturnTarget: personReturnTarget));
                    return;
                }

                if (result.Error == MediaDetailLoadError.NotFound)
                {
                    StartRefreshingLocalSearchIndex(session);
                }

                ErrorMessage = GetErrorMessage(result.Error);
                return;
            }

            var seasonsTask = IsSeriesDetail
                ? LoadSeasonsAsync(
                    parameter.SelectedSeasonId,
                    parameter.FocusedEpisodeId,
                    requestGeneration,
                    cancellationToken)
                : Task.CompletedTask;
            if (IsSupplementarySectionVisible
                && currentSessionService.CurrentSession is not null
                && IsCurrentMediaLoad(requestGeneration, cancellationToken))
            {
                _ = LoadSimilarAsync(requestGeneration, cancellationToken);
            }
            else if (!IsSupplementarySectionVisible)
            {
                ClearSimilarState();
            }

            await seasonsTask.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer media request owns the view model state now.
        }
        finally
        {
            if (IsCurrentMediaLoad(requestGeneration, cancellationToken))
            {
                IsLoading = false;
            }
        }
    }

    private async Task RetryAsync()
    {
        await LoadAsync(new DetailNavigationParameter(
            currentItemId,
            returnPage,
            backTarget,
            SelectedSeason?.Id,
            SelectedEpisode?.Id,
            fallbackItemId,
            playbackReturnState,
            personReturnTarget)).ConfigureAwait(true);
    }

    private async Task RetrySeasonsAsync()
    {
        await LoadSeasonsAsync(
                SelectedSeason?.Id,
                SelectedEpisode?.Id ?? requestedEpisodeId,
                mediaLoadGeneration,
                mediaLoadCancellation?.Token ?? CancellationToken.None)
            .ConfigureAwait(true);
    }

    private async Task RetryEpisodesAsync()
    {
        if (SelectedSeason is not null)
        {
            await LoadEpisodesAsync(
                    SelectedSeason,
                    SelectedEpisode?.Id ?? requestedEpisodeId,
                    mediaLoadGeneration,
                    mediaLoadCancellation?.Token ?? CancellationToken.None)
                .ConfigureAwait(true);
        }
    }

    private async Task RetryPlaybackAsync()
    {
        if (lastPlaybackRequest is not null)
        {
            await PreparePlaybackAsync(lastPlaybackRequest).ConfigureAwait(true);
        }
    }

    private async Task PlaySeriesTargetAsync()
    {
        var target = GetSeriesPlaybackTarget();
        if (target is null)
        {
            return;
        }

        SelectedEpisode = target.Episode;
        await PreparePlaybackAsync(new PlaybackStartRequest(
            target.Episode.Id,
            target.Episode.Title,
            target.StartPositionTicks,
            "Episode")).ConfigureAwait(true);
    }

    private async Task PlayEpisodeAsync(object? parameter)
    {
        if (parameter is not EpisodeInfoViewModel episode || !Episodes.Contains(episode))
        {
            return;
        }

        SelectedEpisode = episode;
        await PreparePlaybackAsync(new PlaybackStartRequest(
            episode.Id,
            episode.Title,
            HasResumeProgress(episode) ? GetResumePositionTicks(episode) : 0,
            "Episode")).ConfigureAwait(true);
    }

    private bool CanPlayEpisode(object? parameter)
    {
        return parameter is EpisodeInfoViewModel episode
            && Episodes.Contains(episode)
            && !IsPreparingPlayback
            && Detail is not null;
    }

    private async Task ToggleFavoriteAsync()
    {
        if (!CanToggleFavorite() || Detail is null)
        {
            return;
        }

        var session = currentSessionService.CurrentSession;
        if (session is null)
        {
            navigationService.NavigateTo(AppPage.Login);
            showLoginError("请先登录");
            return;
        }

        var targetState = !IsFavorite;
        IsFavoriteChanging = true;
        FavoriteErrorMessage = null;

        try
        {
            var result = await itemUserDataService
                .SetFavoriteAsync(session, Detail.Id, targetState, CancellationToken.None)
                .ConfigureAwait(true);

            if (result.IsSuccess)
            {
                isFavorite = targetState;
                OnPropertyChanged(nameof(IsFavorite));
                OnPropertyChanged(nameof(FavoriteButtonText));
                return;
            }

            if (result.Error == ItemUserDataError.Unauthorized)
            {
                await HandleExpiredSessionAsync().ConfigureAwait(true);
                return;
            }

            FavoriteErrorMessage = GetFavoriteErrorMessage(result.Error);
        }
        finally
        {
            IsFavoriteChanging = false;
        }
    }

    private async Task TogglePlayedAsync()
    {
        if (!CanTogglePlayed() || Detail is null)
        {
            return;
        }

        var session = currentSessionService.CurrentSession;
        if (session is null)
        {
            navigationService.NavigateTo(AppPage.Login);
            showLoginError("请先登录");
            return;
        }

        var targetState = !IsPlayed;
        IsPlayedChanging = true;
        PlayedErrorMessage = null;

        try
        {
            var result = await itemUserDataService
                .SetPlayedAsync(session, Detail.Id, targetState, CancellationToken.None)
                .ConfigureAwait(true);

            if (result.IsSuccess)
            {
                isPlayed = targetState;
                if (playbackReturnState is not null
                    && string.Equals(playbackReturnState.ItemId, Detail.Id, StringComparison.OrdinalIgnoreCase))
                {
                    playbackReturnState = null;
                    PlaybackSyncWarningMessage = null;
                }

                OnPropertyChanged(nameof(IsPlayed));
                OnPropertyChanged(nameof(PlayedButtonText));
                return;
            }

            if (result.Error == ItemUserDataError.Unauthorized)
            {
                await HandleExpiredSessionAsync().ConfigureAwait(true);
                return;
            }

            PlayedErrorMessage = GetPlayedErrorMessage(result.Error);
        }
        finally
        {
            IsPlayedChanging = false;
        }
    }

    private async Task LoadSeasonsAsync(
        string? selectedSeasonId,
        string? initialEpisodeId,
        long requestGeneration,
        CancellationToken cancellationToken)
    {
        if (!IsSeriesDetail
            || string.IsNullOrWhiteSpace(currentItemId)
            || !IsCurrentMediaLoad(requestGeneration, cancellationToken))
        {
            return;
        }

        var session = currentSessionService.CurrentSession;
        if (session is null)
        {
            navigationService.NavigateTo(AppPage.Login);
            showLoginError("请先登录");
            return;
        }

        IsSeasonsLoading = true;
        SeasonErrorMessage = null;
        EpisodeErrorMessage = null;
        if (Seasons.Count == 0)
        {
            SelectedSeason = null;
            Episodes = Array.Empty<EpisodeInfoViewModel>();
        }
        SeasonInfoViewModel? defaultSeason = null;

        try
        {
            var result = await seriesService
                .LoadSeasonsAsync(session, currentItemId, cancellationToken)
                .ConfigureAwait(true);

            if (!IsCurrentMediaLoad(requestGeneration, cancellationToken))
            {
                return;
            }

            if (!result.IsSuccess)
            {
                await HandleSeriesLoadFailureAsync(result.Error, isSeasonFailure: true).ConfigureAwait(true);
                return;
            }

            Seasons = result.Seasons
                .OrderBy(season => season.IndexNumber ?? int.MaxValue)
                .ThenBy(season => season.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(season => new SeasonInfoViewModel(season))
                .ToArray();

            if (Seasons.Count == 0)
            {
                SelectedSeason = null;
                SelectedEpisode = null;
                Episodes = Array.Empty<EpisodeInfoViewModel>();
                return;
            }

            defaultSeason = FindSeason(selectedSeasonId)
                ?? Seasons.FirstOrDefault(season => season.HasResumeOrUnwatched)
                ?? Seasons
                    .Where(season => season.IndexNumber is null or > 0)
                    .OrderBy(season => season.IndexNumber ?? int.MaxValue)
                    .FirstOrDefault()
                ?? Seasons.OrderBy(season => season.IndexNumber ?? int.MaxValue).First();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (IsCurrentMediaLoad(requestGeneration, cancellationToken))
            {
                IsSeasonsLoading = false;
            }
        }

        if (defaultSeason is not null && IsCurrentMediaLoad(requestGeneration, cancellationToken))
        {
            await SelectSeasonAsync(
                    defaultSeason,
                    initialEpisodeId,
                    requestGeneration,
                    cancellationToken)
                .ConfigureAwait(true);
        }
    }

    private void NavigateBack()
    {
        if (backTarget is null)
        {
            navigationService.NavigateTo(returnPage, returnPage == AppPage.Person ? personReturnTarget : null);
            return;
        }

        var target = backTarget;
        backTarget = null;

        if (target.Page == AppPage.Detail)
        {
            navigationService.NavigateTo(
                AppPage.Detail,
                new DetailNavigationParameter(
                    target.ItemId,
                    target.ReturnPage,
                    SelectedSeasonId: target.SelectedSeasonId,
                    FocusedEpisodeId: target.FocusedEpisodeId,
                    PersonReturnTarget: target.PersonReturnTarget));
            return;
        }

        navigationService.NavigateTo(target.Page, target.ItemId);
    }

    private SeasonInfoViewModel? FindSeason(string? seasonId)
    {
        if (string.IsNullOrWhiteSpace(seasonId))
        {
            return null;
        }

        return Seasons.FirstOrDefault(
            season => string.Equals(season.Id, seasonId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task SelectSeasonAsync(SeasonInfoViewModel season)
    {
        ArgumentNullException.ThrowIfNull(season);
        requestedEpisodeId = null;
        await SelectSeasonAsync(
                season,
                null,
                mediaLoadGeneration,
                mediaLoadCancellation?.Token ?? CancellationToken.None)
            .ConfigureAwait(true);
    }

    private async Task SelectSeasonAsync(
        SeasonInfoViewModel season,
        string? initialEpisodeId,
        long requestGeneration,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentMediaLoad(requestGeneration, cancellationToken))
        {
            return;
        }

        var isChangingSeason = !string.Equals(
            SelectedSeason?.Id,
            season.Id,
            StringComparison.OrdinalIgnoreCase);
        SelectedSeason = season;
        requestedEpisodeId = initialEpisodeId;
        if (isChangingSeason)
        {
            SelectedEpisode = null;
            Episodes = Array.Empty<EpisodeInfoViewModel>();
        }

        await LoadEpisodesAsync(
                season,
                initialEpisodeId,
                requestGeneration,
                cancellationToken)
            .ConfigureAwait(true);
    }

    private async Task LoadEpisodesAsync(
        SeasonInfoViewModel season,
        string? initialEpisodeId,
        long requestGeneration,
        CancellationToken mediaCancellationToken)
    {
        if (!IsSeriesDetail
            || string.IsNullOrWhiteSpace(currentItemId)
            || !IsCurrentMediaLoad(requestGeneration, mediaCancellationToken))
        {
            return;
        }

        var episodeRequestGeneration = BeginEpisodeLoad(mediaCancellationToken);
        var cancellationToken = episodeLoadCancellation!.Token;
        var seriesId = currentItemId;

        var session = currentSessionService.CurrentSession;
        if (session is null)
        {
            navigationService.NavigateTo(AppPage.Login);
            showLoginError("请先登录");
            return;
        }

        IsEpisodesLoading = true;
        EpisodeErrorMessage = null;

        try
        {
            var result = await seriesService
                .LoadEpisodesAsync(session, seriesId, season.Id, cancellationToken)
                .ConfigureAwait(true);

            if (!IsCurrentEpisodeLoad(
                    requestGeneration,
                    episodeRequestGeneration,
                    cancellationToken))
            {
                return;
            }

            if (!result.IsSuccess)
            {
                await HandleSeriesLoadFailureAsync(result.Error, isSeasonFailure: false).ConfigureAwait(true);
                return;
            }

            var loadedEpisodes = result.Episodes
                .OrderBy(episode => episode.SeasonIndex ?? int.MaxValue)
                .ThenBy(episode => episode.EpisodeIndex ?? int.MaxValue)
                .ThenBy(episode => episode.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(episode =>
                {
                    var viewModel = new EpisodeInfoViewModel(episode);
                    ApplyPlaybackReturnState(viewModel);
                    return viewModel;
                })
                .ToArray();
            Episodes = loadedEpisodes;
            SelectedEpisode = ChooseDefaultEpisode(loadedEpisodes, initialEpisodeId);
            requestedEpisodeId = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (IsCurrentEpisodeLoad(
                    requestGeneration,
                    episodeRequestGeneration,
                    cancellationToken))
            {
                IsEpisodesLoading = false;
            }
        }
    }

    private async Task RetrySimilarAsync()
    {
        if (CanRetrySimilar())
        {
            await LoadSimilarAsync(
                    mediaLoadGeneration,
                    mediaLoadCancellation?.Token ?? CancellationToken.None)
                .ConfigureAwait(true);
        }
    }

    private async Task LoadSimilarAsync(long requestGeneration, CancellationToken cancellationToken)
    {
        if (!IsSupplementarySectionVisible
            || string.IsNullOrWhiteSpace(currentItemId)
            || !IsCurrentMediaLoad(requestGeneration, cancellationToken))
        {
            return;
        }

        var session = currentSessionService.CurrentSession;
        if (session is null)
        {
            navigationService.NavigateTo(AppPage.Login);
            showLoginError("请先登录");
            return;
        }

        var itemId = currentItemId;
        IsSimilarLoading = true;
        SimilarErrorMessage = null;
        try
        {
            var result = await similarMediaService
                .LoadSimilarAsync(session, itemId, Detail!.Type, cancellationToken)
                .ConfigureAwait(true);
            if (!IsCurrentMediaLoad(requestGeneration, cancellationToken)
                || !string.Equals(currentItemId, itemId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (result.IsSuccess)
            {
                SimilarItems = result.Items
                    .Select(item => new SimilarMediaItemViewModel(item))
                    .ToArray();
                return;
            }

            if (result.Error == SimilarMediaLoadError.Unauthorized)
            {
                await HandleExpiredSessionAsync().ConfigureAwait(true);
                return;
            }

            if (result.Error != SimilarMediaLoadError.Cancelled)
            {
                SimilarErrorMessage = GetSimilarErrorMessage(result.Error);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer detail request owns all visible state.
        }
        finally
        {
            if (IsCurrentMediaLoad(requestGeneration, cancellationToken)
                && string.Equals(currentItemId, itemId, StringComparison.OrdinalIgnoreCase))
            {
                IsSimilarLoading = false;
            }
        }
    }

    private async Task HandleSeriesLoadFailureAsync(SeriesLoadError error, bool isSeasonFailure)
    {
        if (error == SeriesLoadError.Unauthorized)
        {
            await HandleExpiredSessionAsync().ConfigureAwait(true);
            return;
        }

        if (isSeasonFailure)
        {
            SeasonErrorMessage = GetSeriesErrorMessage(error, "剧集");
            return;
        }

        EpisodeErrorMessage = GetSeriesErrorMessage(error, "单集");
    }

    private async Task HandleExpiredSessionAsync()
    {
        await ExpiredSessionRecovery
            .ClearAsync(authSessionStore, currentSessionService, "media detail")
            .ConfigureAwait(true);
        navigationService.NavigateTo(AppPage.Login);
        showLoginError(SessionExpiredMessage);
    }

    private void StartRefreshingLocalSearchIndex(AuthSession session)
    {
        if (localMediaSearchIndex is null)
        {
            return;
        }

        _ = RefreshLocalSearchIndexAsync(session);
    }

    private async Task RefreshLocalSearchIndexAsync(AuthSession session)
    {
        try
        {
            await localMediaSearchIndex!
                .RefreshAsync(session, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to refresh local media search index: {exception.Message}");
        }
    }

    private async Task PreparePlaybackAsync(long startPositionTicks)
    {
        if (Detail is null || !IsPlayableMedia)
        {
            return;
        }

        var request = new PlaybackStartRequest(
            Detail.Id,
            Title,
            Math.Max(0, startPositionTicks),
            Detail.Type,
            Detail.Year);
        await PreparePlaybackAsync(request).ConfigureAwait(true);
    }

    private async Task PreparePlaybackAsync(PlaybackStartRequest request)
    {
        if (IsPreparingPlayback || Detail is null)
        {
            return;
        }

        var session = currentSessionService.CurrentSession;
        if (session is null)
        {
            navigationService.NavigateTo(AppPage.Login);
            showLoginError("请先登录");
            return;
        }

        lastPlaybackRequest = request;
        IsPreparingPlayback = true;
        PlaybackErrorMessage = null;

        try
        {
            var result = await playbackService
                .PreparePlaybackAsync(session, request, CancellationToken.None)
                .ConfigureAwait(true);

            if (result.IsSuccess && result.PlaybackInfo is not null)
            {
                navigationService.NavigateTo(
                    AppPage.Player,
                    new PlayerNavigationParameter(
                        result.PlaybackInfo,
                        CreateCurrentDetailParameter(),
                        Detail.LogoUrl));
                return;
            }

            if (result.Error == PlaybackLoadError.Unauthorized)
            {
                await HandleExpiredSessionAsync().ConfigureAwait(true);
                return;
            }

            PlaybackErrorMessage = GetPlaybackErrorMessage(result.Error);
        }
        finally
        {
            IsPreparingPlayback = false;
        }
    }

    private void NotifyCommandsCanExecuteChanged()
    {
        NotifyPersonalMediaCommands();
        if (RetryCommand is AsyncRelayCommand retryCommand)
        {
            retryCommand.NotifyCanExecuteChanged();
        }

        if (RetrySeasonsCommand is AsyncRelayCommand retrySeasonsCommand)
        {
            retrySeasonsCommand.NotifyCanExecuteChanged();
        }

        if (RetryEpisodesCommand is AsyncRelayCommand retryEpisodesCommand)
        {
            retryEpisodesCommand.NotifyCanExecuteChanged();
        }

        if (RetrySimilarCommand is AsyncRelayCommand retrySimilarCommand)
        {
            retrySimilarCommand.NotifyCanExecuteChanged();
        }

        if (RetryPlaybackCommand is AsyncRelayCommand retryPlaybackCommand)
        {
            retryPlaybackCommand.NotifyCanExecuteChanged();
        }

        if (PlayCommand is AsyncRelayCommand playCommand)
        {
            playCommand.NotifyCanExecuteChanged();
        }

        if (ContinuePlayCommand is AsyncRelayCommand continuePlayCommand)
        {
            continuePlayCommand.NotifyCanExecuteChanged();
        }

        if (RestartPlayCommand is AsyncRelayCommand restartPlayCommand)
        {
            restartPlayCommand.NotifyCanExecuteChanged();
        }

        if (SeriesPlayCommand is AsyncRelayCommand seriesPlayCommand)
        {
            seriesPlayCommand.NotifyCanExecuteChanged();
        }

        if (PlayEpisodeCommand is AsyncRelayCommand playEpisodeCommand)
        {
            playEpisodeCommand.NotifyCanExecuteChanged();
        }

        if (ToggleFavoriteCommand is AsyncRelayCommand toggleFavoriteCommand)
        {
            toggleFavoriteCommand.NotifyCanExecuteChanged();
        }

        if (TogglePlayedCommand is AsyncRelayCommand togglePlayedCommand)
        {
            togglePlayedCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnDetailPropertiesChanged()
    {
        OnPropertyChanged(nameof(IsContentVisible));
        OnPropertyChanged(nameof(IsSeriesDetail));
        OnPropertyChanged(nameof(IsSupplementarySectionVisible));
        OnSeriesStateChanged();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(YearText));
        OnPropertyChanged(nameof(TypeText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(OverviewText));
        OnPropertyChanged(nameof(GenresText));
        OnPropertyChanged(nameof(RatingText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteButtonText));
        OnPropertyChanged(nameof(IsFavoriteErrorVisible));
        OnPropertyChanged(nameof(IsPlayed));
        OnPropertyChanged(nameof(PlayedButtonText));
        OnPropertyChanged(nameof(IsPlayedErrorVisible));
        OnPlaybackAvailabilityChanged();
        OnPropertyChanged(nameof(PosterUrl));
        OnPropertyChanged(nameof(BackdropUrl));
        OnPropertyChanged(nameof(SelectedEpisodeHeroUrl));
        OnPropertyChanged(nameof(HeroBackdropUrl));
        OnPropertyChanged(nameof(People));
        OnPropertyChanged(nameof(ArtworkUrls));
        OnPropertyChanged(nameof(IsPeopleSectionVisible));
        OnPropertyChanged(nameof(IsArtworkSectionVisible));
        OnSimilarStateChanged();
        NotifyCommandsCanExecuteChanged();
    }

    private void OnPlaybackAvailabilityChanged()
    {
        OnPropertyChanged(nameof(IsPlayableMedia));
        OnPropertyChanged(nameof(IsPlaybackControlsVisible));
        OnPropertyChanged(nameof(IsStandardPlaybackPanelVisible));
        OnPropertyChanged(nameof(IsStandardProgressVisible));
        OnPropertyChanged(nameof(IsPlaybackUnavailableVisible));
        OnPropertyChanged(nameof(IsNonSeriesPlaybackUnavailableVisible));
        OnPropertyChanged(nameof(PlaybackUnavailableText));
        OnPropertyChanged(nameof(IsContinuePlaybackVisible));
        OnSeriesPlaybackTargetChanged();
    }

    private void OnSeriesStateChanged()
    {
        OnPropertyChanged(nameof(IsSeriesSectionVisible));
        OnPropertyChanged(nameof(HasSeasonError));
        OnPropertyChanged(nameof(HasEpisodeError));
        OnPropertyChanged(nameof(IsSeasonErrorVisible));
        OnPropertyChanged(nameof(IsEpisodeErrorVisible));
        OnPropertyChanged(nameof(IsInitialSeasonsLoading));
        OnPropertyChanged(nameof(IsInitialEpisodesLoading));
        OnPropertyChanged(nameof(IsRefreshingSeasons));
        OnPropertyChanged(nameof(IsRefreshingEpisodes));
        OnPropertyChanged(nameof(IsSeasonRefreshErrorVisible));
        OnPropertyChanged(nameof(IsEpisodeRefreshErrorVisible));
        OnPropertyChanged(nameof(IsSeasonsEmpty));
        OnPropertyChanged(nameof(IsEpisodesEmpty));
        OnPropertyChanged(nameof(IsSeasonsListVisible));
        OnPropertyChanged(nameof(IsEpisodesListVisible));
        OnSeriesPlaybackTargetChanged();
        NotifyCommandsCanExecuteChanged();
    }

    private void OnSeriesPlaybackTargetChanged()
    {
        OnPropertyChanged(nameof(IsSeriesActionPanelVisible));
        OnPropertyChanged(nameof(IsSeriesPrimaryButtonVisible));
        OnPropertyChanged(nameof(SeriesPrimaryButtonText));
        OnPropertyChanged(nameof(SeriesPlaybackTargetText));
        OnPropertyChanged(nameof(IsSeriesPlaybackTargetVisible));
        OnPropertyChanged(nameof(IsSeriesResumeProgressVisible));
        OnPropertyChanged(nameof(SeriesResumeProgressText));
        OnPropertyChanged(nameof(SeriesResumePositionText));
        OnPropertyChanged(nameof(SeriesResumeProgressValue));
    }

    private void ClearSeriesState()
    {
        SeasonErrorMessage = null;
        EpisodeErrorMessage = null;
        Seasons = Array.Empty<SeasonInfoViewModel>();
        SelectedSeason = null;
        SelectedEpisode = null;
        Episodes = Array.Empty<EpisodeInfoViewModel>();
        IsSeasonsLoading = false;
        IsEpisodesLoading = false;
    }

    private void ClearSimilarState()
    {
        SimilarErrorMessage = null;
        SimilarItems = Array.Empty<SimilarMediaItemViewModel>();
        IsSimilarLoading = false;
    }

    private void OnSimilarStateChanged()
    {
        OnPropertyChanged(nameof(HasSimilarError));
        OnPropertyChanged(nameof(IsSimilarInitialLoading));
        OnPropertyChanged(nameof(IsSimilarEmpty));
        OnPropertyChanged(nameof(IsSimilarErrorVisible));
        OnPropertyChanged(nameof(IsSimilarItemsVisible));
        OnPropertyChanged(nameof(IsSimilarSectionVisible));
        if (RetrySimilarCommand is AsyncRelayCommand retrySimilarCommand)
        {
            retrySimilarCommand.NotifyCanExecuteChanged();
        }
    }

    private long BeginMediaLoad()
    {
        mediaLoadGeneration++;
        mediaLoadCancellation?.Cancel();
        mediaLoadCancellation?.Dispose();
        mediaLoadCancellation = new CancellationTokenSource();

        episodeLoadGeneration++;
        episodeLoadCancellation?.Cancel();
        episodeLoadCancellation?.Dispose();
        episodeLoadCancellation = null;
        IsSeasonsLoading = false;
        IsEpisodesLoading = false;
        return mediaLoadGeneration;
    }

    private long BeginEpisodeLoad(CancellationToken mediaCancellationToken)
    {
        episodeLoadGeneration++;
        episodeLoadCancellation?.Cancel();
        episodeLoadCancellation?.Dispose();
        episodeLoadCancellation = CancellationTokenSource.CreateLinkedTokenSource(mediaCancellationToken);
        IsEpisodesLoading = false;
        return episodeLoadGeneration;
    }

    private bool IsCurrentMediaLoad(long requestGeneration, CancellationToken cancellationToken)
    {
        return requestGeneration == mediaLoadGeneration
            && !cancellationToken.IsCancellationRequested;
    }

    private bool IsCurrentEpisodeLoad(
        long requestGeneration,
        long episodeRequestGeneration,
        CancellationToken cancellationToken)
    {
        return IsCurrentMediaLoad(requestGeneration, cancellationToken)
            && episodeRequestGeneration == episodeLoadGeneration;
    }

    private static string GetErrorMessage(MediaDetailLoadError error)
    {
        return error switch
        {
            MediaDetailLoadError.Forbidden => "没有权限查看此媒体",
            MediaDetailLoadError.NotFound => "媒体不存在或已不可用",
            MediaDetailLoadError.ServerTimeout => "详情加载超时，请稍后重试",
            MediaDetailLoadError.ServerUnreachable => "无法加载详情，请检查网络或服务器",
            MediaDetailLoadError.Cancelled => "详情加载已取消",
            MediaDetailLoadError.InvalidResponse => "详情数据无法识别，请稍后重试",
            _ => "详情加载失败，请稍后重试"
        };
    }

    private static string GetSeriesErrorMessage(SeriesLoadError error, string label)
    {
        return error switch
        {
            SeriesLoadError.Forbidden => $"没有权限加载{label}",
            SeriesLoadError.NotFound => $"{label}不存在或已不可用",
            SeriesLoadError.ServerTimeout => $"{label}加载超时，请稍后重试",
            SeriesLoadError.ServerUnreachable => $"无法加载{label}，请检查网络或服务器",
            SeriesLoadError.Cancelled => $"{label}加载已取消",
            SeriesLoadError.InvalidResponse => $"{label}数据无法识别，请稍后重试",
            _ => $"{label}加载失败，请稍后重试"
        };
    }

    private static string GetPlaybackErrorMessage(PlaybackLoadError error)
    {
        return error switch
        {
            PlaybackLoadError.Forbidden => "没有权限播放此媒体",
            PlaybackLoadError.NoPlayableMediaSource => "未找到可播放的媒体源",
            PlaybackLoadError.NotFound => "媒体不存在或已不可用",
            PlaybackLoadError.ServerTimeout => "播放准备超时，请稍后重试",
            PlaybackLoadError.ServerUnreachable => "无法准备播放，请检查网络或服务器",
            PlaybackLoadError.Cancelled => "播放准备已取消",
            PlaybackLoadError.InvalidResponse => "播放信息无法识别，请稍后重试",
            _ => "播放准备失败，请稍后重试"
        };
    }

    private static string GetSimilarErrorMessage(SimilarMediaLoadError error)
    {
        return error switch
        {
            SimilarMediaLoadError.Forbidden => "没有权限加载类似作品",
            SimilarMediaLoadError.NotFound => "类似作品暂时不可用",
            SimilarMediaLoadError.ServerTimeout => "类似作品加载超时，请稍后重试",
            SimilarMediaLoadError.ServerUnreachable => "无法加载类似作品，请检查网络或服务器",
            SimilarMediaLoadError.InvalidResponse => "类似作品数据无法识别，请稍后重试",
            _ => "类似作品加载失败，请稍后重试"
        };
    }

    private static string GetFavoriteErrorMessage(ItemUserDataError error)
    {
        return error switch
        {
            ItemUserDataError.Forbidden => "没有权限更新收藏",
            ItemUserDataError.NotFound => "媒体不存在或已不可用",
            ItemUserDataError.ServerTimeout => "收藏更新超时，请稍后重试",
            ItemUserDataError.ServerUnreachable => "无法更新收藏，请检查网络或服务器",
            ItemUserDataError.Cancelled => "收藏更新已取消",
            _ => "收藏更新失败，请稍后重试"
        };
    }

    private static string GetPlayedErrorMessage(ItemUserDataError error)
    {
        return error switch
        {
            ItemUserDataError.Forbidden => "没有权限更新观看状态",
            ItemUserDataError.NotFound => "媒体不存在或已不可用",
            ItemUserDataError.ServerTimeout => "观看状态更新超时，请稍后重试",
            ItemUserDataError.ServerUnreachable => "无法更新观看状态，请检查网络或服务器",
            ItemUserDataError.Cancelled => "观看状态更新已取消",
            _ => "观看状态更新失败，请稍后重试"
        };
    }

    private DetailNavigationParameter CreateCurrentDetailParameter()
    {
        return new DetailNavigationParameter(
            currentItemId,
            returnPage,
            backTarget,
            SelectedSeason?.Id,
            SelectedEpisode?.Id,
            fallbackItemId,
            playbackReturnState,
            personReturnTarget);
    }

    private void UpdatePlaybackReturnState(DetailNavigationParameter parameter)
    {
        if (parameter.PlaybackState is { } state
            && (string.Equals(state.ItemId, parameter.ItemId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(state.ItemId, parameter.FocusedEpisodeId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(state.ItemId, parameter.FallbackItemId, StringComparison.OrdinalIgnoreCase)))
        {
            playbackReturnState = state;
            PlaybackSyncWarningMessage = state.IsSynchronized
                ? null
                : "播放进度已保存在当前页面，但暂未同步到服务器";
            return;
        }

        playbackReturnState = null;
        PlaybackSyncWarningMessage = null;
    }

    private void ApplyPlaybackReturnStateToCurrentView()
    {
        if (Detail is not null)
        {
            Detail = MergePlaybackReturnState(Detail);
        }

        var episodeChanged = false;
        foreach (var episode in Episodes)
        {
            episodeChanged |= ApplyPlaybackReturnState(episode);
        }

        if (episodeChanged)
        {
            OnSeriesPlaybackTargetChanged();
        }
    }

    private MediaDetail MergePlaybackReturnState(MediaDetail source)
    {
        if (playbackReturnState is not { } state
            || !string.Equals(state.ItemId, source.Id, StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        return source with
        {
            ResumePositionTicks = state.IsPlayed ? 0 : state.PositionTicks,
            PlayedPercentage = state.IsPlayed ? 100d : state.PlayedPercentage,
            IsPlayed = state.IsPlayed
        };
    }

    private bool ApplyPlaybackReturnState(EpisodeInfoViewModel episode)
    {
        if (playbackReturnState is not { } state
            || !string.Equals(state.ItemId, episode.Id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        episode.ApplyPlaybackState(
            state.PositionTicks,
            state.PlayedPercentage,
            state.IsPlayed);
        return true;
    }

    private SeriesPlaybackTarget? GetSeriesPlaybackTarget()
    {
        if (!IsSeriesDetail || Episodes.Count == 0)
        {
            return null;
        }

        var orderedEpisodes = OrderEpisodesForPlayback(Episodes).ToArray();
        var preferredResumeEpisode = orderedEpisodes.FirstOrDefault(episode =>
            string.Equals(
                episode.Id,
                preferredSeriesPlaybackEpisodeId,
                StringComparison.OrdinalIgnoreCase)
            && HasResumeProgress(episode));
        if (preferredResumeEpisode is not null)
        {
            return new SeriesPlaybackTarget(
                preferredResumeEpisode,
                GetResumePositionTicks(preferredResumeEpisode),
                SeriesPlaybackIntent.Resume);
        }

        var resumeEpisode = orderedEpisodes.FirstOrDefault(HasResumeProgress);
        if (resumeEpisode is not null)
        {
            return new SeriesPlaybackTarget(
                resumeEpisode,
                GetResumePositionTicks(resumeEpisode),
                SeriesPlaybackIntent.Resume);
        }

        var normalEpisodes = orderedEpisodes.Any(episode => episode.SeasonIndex is > 0)
            ? orderedEpisodes.Where(episode => episode.SeasonIndex is > 0).ToArray()
            : orderedEpisodes;
        var firstUnfinishedEpisode = normalEpisodes.FirstOrDefault(episode => !IsEpisodeCompleted(episode));
        if (firstUnfinishedEpisode is not null)
        {
            return new SeriesPlaybackTarget(
                firstUnfinishedEpisode,
                0,
                orderedEpisodes.Any(HasWatchHistory)
                    ? SeriesPlaybackIntent.Next
                    : SeriesPlaybackIntent.First);
        }

        var firstEpisode = normalEpisodes.FirstOrDefault();
        return firstEpisode is null
            ? null
            : new SeriesPlaybackTarget(firstEpisode, 0, SeriesPlaybackIntent.Replay);
    }

    private static EpisodeInfoViewModel? ChooseDefaultEpisode(
        IReadOnlyList<EpisodeInfoViewModel> loadedEpisodes,
        string? initialEpisodeId)
    {
        return loadedEpisodes.FirstOrDefault(episode => string.Equals(
                episode.Id,
                initialEpisodeId,
                StringComparison.OrdinalIgnoreCase))
            ?? loadedEpisodes.FirstOrDefault(HasPartialProgress)
            ?? loadedEpisodes.FirstOrDefault(episode => !IsEpisodeCompleted(episode))
            ?? loadedEpisodes.FirstOrDefault();
    }

    private static IEnumerable<EpisodeInfoViewModel> OrderEpisodesForPlayback(IEnumerable<EpisodeInfoViewModel> source)
    {
        return source
            .OrderBy(episode => episode.SeasonIndex is 0 ? int.MaxValue - 1 : episode.SeasonIndex ?? int.MaxValue)
            .ThenBy(episode => episode.EpisodeIndex ?? int.MaxValue)
            .ThenBy(episode => episode.Title, StringComparer.CurrentCultureIgnoreCase);
    }

    private static bool HasResumeProgress(EpisodeInfoViewModel episode)
    {
        return GetResumePositionTicks(episode) > 0 && !IsEpisodeCompleted(episode);
    }

    private static bool HasPartialProgress(EpisodeInfoViewModel episode)
    {
        return !IsEpisodeCompleted(episode)
            && (episode.ResumePositionTicks.GetValueOrDefault() > 0
                || episode.PlayedPercentage is > 0 and < EpisodeCompletionThreshold);
    }

    private static bool HasWatchHistory(EpisodeInfoViewModel episode)
    {
        return episode.IsPlayed
            || episode.ResumePositionTicks.GetValueOrDefault() > 0
            || episode.PlayedPercentage.GetValueOrDefault() > 0;
    }

    private static bool IsEpisodeCompleted(EpisodeInfoViewModel episode)
    {
        return episode.IsPlayed
            || episode.PlayedPercentage.GetValueOrDefault() >= EpisodeCompletionThreshold;
    }

    private static long GetResumePositionTicks(EpisodeInfoViewModel episode)
    {
        var resumeTicks = episode.ResumePositionTicks.GetValueOrDefault();
        if (resumeTicks > 0)
        {
            return resumeTicks;
        }

        var runTimeTicks = episode.RunTimeTicks.GetValueOrDefault();
        var playedPercentage = episode.PlayedPercentage.GetValueOrDefault();
        if (runTimeTicks <= 0 || playedPercentage <= 0 || playedPercentage >= EpisodeCompletionThreshold)
        {
            return 0;
        }

        return Math.Max(0, (long)Math.Round(runTimeTicks * playedPercentage / 100d));
    }

    private static string FormatPosition(long ticks)
    {
        var timeSpan = TimeSpan.FromTicks(Math.Max(0, ticks));
        return timeSpan.TotalHours >= 1
            ? $"{(int)timeSpan.TotalHours}:{timeSpan.Minutes:00}:{timeSpan.Seconds:00}"
            : $"{timeSpan.Minutes:00}:{timeSpan.Seconds:00}";
    }

    private bool CanPlaySeriesTarget()
    {
        return IsSeriesPrimaryButtonVisible && !IsPreparingPlayback;
    }

    private bool CanStartPlayback()
    {
        return Detail is not null && IsContentVisible && IsPlayableMedia && !IsPreparingPlayback;
    }

    private bool CanContinuePlayback()
    {
        return CanStartPlayback() && Detail?.ResumePositionTicks is > 0;
    }

    private bool CanRetryPlayback()
    {
        return !IsPreparingPlayback && lastPlaybackRequest is not null;
    }

    private bool CanRetrySimilar()
    {
        return IsSupplementarySectionVisible
            && !IsSimilarLoading
            && HasSimilarError
            && !string.IsNullOrWhiteSpace(currentItemId);
    }

    private bool CanToggleFavorite()
    {
        return Detail is not null && IsContentVisible && !IsFavoriteChanging;
    }

    private bool CanTogglePlayed()
    {
        return Detail is not null && IsContentVisible && !IsPlayedChanging;
    }

    private static string GetTypeLabel(string? type)
    {
        return type?.ToLowerInvariant() switch
        {
            "movie" => "电影",
            "series" => "电视剧",
            "episode" => "剧集",
            "season" => "季",
            "video" => "视频",
            "boxset" => "合集",
            "playlist" => "播放列表",
            null or "" => "未知类型",
            _ => type!
        };
    }

    private static bool IsPlayableType(string? type)
    {
        return type?.ToLowerInvariant() switch
        {
            "movie" => true,
            "episode" => true,
            "video" => true,
            _ => false
        };
    }

    private static string FormatDuration(long? runTimeTicks)
    {
        var ticks = runTimeTicks.GetValueOrDefault();
        if (ticks <= 0)
        {
            return "未知时长";
        }

        var timeSpan = TimeSpan.FromTicks(ticks);
        if (timeSpan.TotalHours >= 1)
        {
            return $"{(int)timeSpan.TotalHours} 小时 {timeSpan.Minutes} 分钟";
        }

        return $"{Math.Max(1, (int)Math.Round(timeSpan.TotalMinutes))} 分钟";
    }

    private enum SeriesPlaybackIntent
    {
        First,
        Resume,
        Next,
        Replay
    }

    private sealed record SeriesPlaybackTarget(
        EpisodeInfoViewModel Episode,
        long StartPositionTicks,
        SeriesPlaybackIntent Intent)
    {
        public string ButtonText => Intent switch
        {
            SeriesPlaybackIntent.Resume => "继续播放",
            SeriesPlaybackIntent.Next => "播放下一集",
            SeriesPlaybackIntent.Replay => "从第 1 集重播",
            _ => "播放第 1 集"
        };

        public string TargetText => Intent switch
        {
            SeriesPlaybackIntent.Resume => $"继续观看 {Episode.NumberText} · {Episode.Title}",
            SeriesPlaybackIntent.Next => $"即将播放 {Episode.NumberText} · {Episode.Title}",
            SeriesPlaybackIntent.Replay => $"从头重播 {Episode.NumberText} · {Episode.Title}",
            _ => $"即将播放 {Episode.NumberText} · {Episode.Title}"
        };
    }
}
