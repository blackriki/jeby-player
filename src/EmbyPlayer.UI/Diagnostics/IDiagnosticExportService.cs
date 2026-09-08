namespace EmbyPlayer.UI.Diagnostics;

internal interface IDiagnosticExportService
{
    Task<DiagnosticExportResult> ExportAsync(
        string destinationPath,
        CancellationToken cancellationToken);
}

internal sealed record DiagnosticExportResult(IReadOnlyList<string> IncludedLogNames)
{
    public int IncludedLogCount => IncludedLogNames.Count;
}
