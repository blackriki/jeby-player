# Product screenshots

These PNG files are rendered from the actual WPF application pages using isolated test services. They are not UI mockups. Media titles, the Demo account, and the geometric poster illustrations are synthetic release examples; no real server, credentials, viewing history, or third-party movie artwork is included.

- `home.png`: home recommendations, libraries, and continue watching.
- `details.png`: movie details and playback actions.
- `settings.png`: playback preferences and keyboard shortcuts.

The original geometric illustrations are drawn by `ReleaseArtwork` in `tests/EmbyPlayer.UI.Tests/ReleaseScreenshotTests.cs` and distributed under the project's GPL-3.0-or-later license. The screenshot capture uses real XAML, view models, image controls, and application styles; it does not run a network request or start MPV. Screenshots demonstrate the interface, not real-server playback verification.

To regenerate on Windows from the repository root:

```powershell
$env:JEBY_CAPTURE_SCREENSHOTS = '1'
dotnet test tests/EmbyPlayer.UI.Tests/EmbyPlayer.UI.Tests.csproj -c Release --filter FullyQualifiedName~CapturePublicProductScreenshots
Remove-Item Env:JEBY_CAPTURE_SCREENSHOTS
```

The capture creates and closes its own isolated WPF window and writes only the three PNG files in this directory. Review every regenerated screenshot before publishing.
