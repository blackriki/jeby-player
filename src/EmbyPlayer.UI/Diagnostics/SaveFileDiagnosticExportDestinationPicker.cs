using Microsoft.Win32;

namespace EmbyPlayer.UI.Diagnostics;

internal sealed class SaveFileDiagnosticExportDestinationPicker : IDiagnosticExportDestinationPicker
{
    public string? PickDestinationPath()
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".zip",
            FileName = $"JebyPlayer-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            OverwritePrompt = true,
            Title = "导出 Jeby Player 诊断日志"
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
