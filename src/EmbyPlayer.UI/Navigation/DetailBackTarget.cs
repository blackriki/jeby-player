namespace EmbyPlayer.UI.Navigation;

public sealed record DetailBackTarget(
    AppPage Page,
    string? ItemId = null,
    AppPage? ReturnPage = null,
    string? SelectedSeasonId = null,
    string? FocusedEpisodeId = null,
    PersonNavigationParameter? PersonReturnTarget = null);
