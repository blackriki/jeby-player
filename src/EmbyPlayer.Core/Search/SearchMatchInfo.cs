namespace EmbyPlayer.Core.Search;

public sealed record SearchMatchInfo(
    SearchMatchSource Source,
    string? SeriesName)
{
    public bool IsDirectTitleMatch => Source is
        SearchMatchSource.Name or
        SearchMatchSource.OriginalTitle or
        SearchMatchSource.SortName;

    public bool IsSeriesNameOnlyMatch => Source == SearchMatchSource.SeriesName;
}
