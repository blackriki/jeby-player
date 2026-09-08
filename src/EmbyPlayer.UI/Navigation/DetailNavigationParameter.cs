namespace EmbyPlayer.UI.Navigation;

public sealed record DetailNavigationParameter(
    string? ItemId,
    AppPage? ReturnPage = null,
    DetailBackTarget? BackTarget = null,
    string? SelectedSeasonId = null,
    string? FocusedEpisodeId = null,
    string? FallbackItemId = null,
    PlaybackReturnState? PlaybackState = null,
    PersonNavigationParameter? PersonReturnTarget = null);

public sealed record PlaybackReturnState(
    string ItemId,
    long PositionTicks,
    long? RunTimeTicks,
    double? PlayedPercentage,
    bool IsPlayed,
    bool IsSynchronized);
