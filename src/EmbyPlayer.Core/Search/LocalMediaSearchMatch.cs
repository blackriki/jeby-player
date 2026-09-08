namespace EmbyPlayer.Core.Search;

public sealed record LocalMediaSearchMatch(
    LocalMediaSearchItem Item,
    int Rank,
    SearchMatchSource MatchSource = SearchMatchSource.Unknown);
