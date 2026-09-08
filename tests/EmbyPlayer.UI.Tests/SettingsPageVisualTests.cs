using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class SettingsPageVisualTests
{
    [TestMethod]
    public async Task FrozenSettingsDesign_UsesSharedStyleDictionary()
    {
        var root = FindRepositoryRoot();
        var appXaml = await File.ReadAllTextAsync(Path.Combine(root, "src", "EmbyPlayer.App", "App.xaml"));
        var pageXaml = await ReadSettingsPageAsync(root);
        var pageCodeBehind = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "SettingsPage.xaml.cs"));
        var stylesXaml = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Styles",
            "SettingsPageStyles.xaml"));

        StringAssert.Contains(appXaml, "/Styles/SettingsPageStyles.xaml");
        foreach (var styleKey in new[]
                 {
                     "SettingsSidebarItem",
                     "SettingsSearchBox",
                     "SettingsSurface",
                     "SettingsToggle",
                     "SettingsSlider",
                     "SettingsComboBox",
                     "SettingsButton",
                     "SettingsDangerButton",
                     "SettingsStatusBadge"
                 })
        {
            StringAssert.Contains(stylesXaml, $"x:Key=\"{styleKey}\"");
            StringAssert.Contains(pageXaml, $"{{StaticResource {styleKey}}}");
        }

        StringAssert.Contains(stylesXaml, "x:Key=\"SettingsSearchResultButton\"");
        StringAssert.Contains(pageCodeBehind, "FindResource(\"SettingsSearchResultButton\")");
    }

    [TestMethod]
    public async Task MediaLibraryScan_IsInServerAccountPanel_NotUnsavedBar()
    {
        var pageXaml = await ReadSettingsPageAsync(FindRepositoryRoot());
        var accountPanel = pageXaml.IndexOf("x:Name=\"AccountPanel\"", StringComparison.Ordinal);
        var localDataPanel = pageXaml.IndexOf("x:Name=\"LocalDataPanel\"", StringComparison.Ordinal);
        var unsavedBar = pageXaml.IndexOf("x:Name=\"UnsavedBar\"", StringComparison.Ordinal);
        var scanCommand = pageXaml.IndexOf("Command=\"{Binding ScanMediaLibraryCommand}\"", StringComparison.Ordinal);

        Assert.IsTrue(accountPanel >= 0 && scanCommand > accountPanel && scanCommand < localDataPanel);
        Assert.IsTrue(scanCommand < unsavedBar);
    }

    [TestMethod]
    public async Task DiagnosticExport_IsAnActionableLocalDataCommand()
    {
        var root = FindRepositoryRoot();
        var pageXaml = await ReadSettingsPageAsync(root);
        var codeBehind = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "SettingsPage.xaml.cs"));
        var localDataPanel = pageXaml.IndexOf("x:Name=\"LocalDataPanel\"", StringComparison.Ordinal);
        var aboutPanel = pageXaml.IndexOf("x:Name=\"AboutPanel\"", StringComparison.Ordinal);
        var exportCommand = pageXaml.IndexOf(
            "Command=\"{Binding ExportDiagnosticLogsCommand}\"",
            StringComparison.Ordinal);

        Assert.IsTrue(localDataPanel >= 0 && exportCommand > localDataPanel && exportCommand < aboutPanel);
        StringAssert.Contains(pageXaml, "Text=\"{Binding DiagnosticExportButtonText, Mode=OneWay}\"");
        StringAssert.Contains(pageXaml, "Text=\"{Binding DiagnosticExportMessage, Mode=OneWay}\"");
        StringAssert.Contains(pageXaml, "AutomationProperties.Name=\"导出诊断日志\"");
        StringAssert.Contains(codeBehind, "new(\"导出诊断日志\"");
    }

    [TestMethod]
    public async Task LocalDataCards_UseAccurateCopyWithoutFabricatedTelemetry()
    {
        var root = FindRepositoryRoot();
        var pageXaml = await ReadSettingsPageAsync(root);
        var codeBehind = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "SettingsPage.xaml.cs"));
        var localDataPanel = pageXaml.IndexOf("x:Name=\"LocalDataPanel\"", StringComparison.Ordinal);
        Assert.IsTrue(localDataPanel >= 0);
        var searchHistoryCard = pageXaml.IndexOf(
            "<TextBlock Text=\"搜索历史\"",
            localDataPanel,
            StringComparison.Ordinal);
        var searchIndexCard = pageXaml.IndexOf(
            "<TextBlock Text=\"缓存与索引\"",
            localDataPanel,
            StringComparison.Ordinal);
        var aboutPanel = pageXaml.IndexOf("x:Name=\"AboutPanel\"", StringComparison.Ordinal);

        Assert.IsTrue(searchHistoryCard > localDataPanel && searchIndexCard > searchHistoryCard);
        Assert.IsTrue(aboutPanel > searchIndexCard);
        var searchHistorySection = pageXaml[searchHistoryCard..searchIndexCard];
        StringAssert.Contains(
            searchHistorySection,
            "Text=\"最近搜索记录保存在本机，可在搜索页管理或清除。\"");
        Assert.IsFalse(
            searchHistorySection.Contains("当前保存 8 条最近搜索。", StringComparison.Ordinal));
        Assert.IsFalse(
            searchHistorySection.Contains("清除功能尚未开放", StringComparison.Ordinal));
        Assert.IsFalse(
            searchHistorySection.Contains("AutomationProperties.Name=\"清除搜索历史\"", StringComparison.Ordinal));
        Assert.IsFalse(
            searchHistorySection.Contains("Text=\"清除记录\"", StringComparison.Ordinal));

        var searchIndexSection = pageXaml[searchIndexCard..aboutPanel];
        StringAssert.Contains(searchIndexSection, "Text=\"本地搜索索引\"");
        StringAssert.Contains(
            searchIndexSection,
            "Text=\"索引保存在本机并加载到内存用于搜索。清理会同时移除磁盘文件和内存索引。\"");
        StringAssert.Contains(searchIndexSection, "Text=\"{Binding ImageUsageText, Mode=OneWay}\"");
        StringAssert.Contains(searchIndexSection, "Text=\"{Binding SearchIndexUsageText, Mode=OneWay}\"");
        foreach (var fabricatedTelemetry in new[]
                 {
                     "1,248",
                     "索引正常",
                     "最近更新",
                     "今天 14:36"
                 })
        {
            Assert.IsFalse(
                searchIndexSection.Contains(fabricatedTelemetry, StringComparison.Ordinal),
                $"Search-index card must not present fabricated telemetry: {fabricatedTelemetry}");
        }

        StringAssert.Contains(
            codeBehind,
            "new(\"搜索历史\", \"最近搜索记录保存在本机，可在搜索页管理或清除。\", \"LocalData\")");
        StringAssert.Contains(
            codeBehind,
            "new(\"本地搜索索引\", \"查看索引磁盘占用、清理或重建当前账户索引。\", \"LocalData\")");
        Assert.IsFalse(codeBehind.Contains("管理最近搜索。", StringComparison.Ordinal));
        Assert.IsFalse(codeBehind.Contains("清除功能尚未开放", StringComparison.Ordinal));
        Assert.IsFalse(
            codeBehind.Contains("查看或重建本地标题索引。", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OrdinarySurface_HasNoTopLineAndRowsOnlyDivideAfterFirst()
    {
        var root = FindRepositoryRoot();
        var pageXaml = await ReadSettingsPageAsync(root);
        var stylesXaml = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Styles",
            "SettingsPageStyles.xaml"));

        StringAssert.Contains(stylesXaml, "x:Key=\"SettingsFirstRow\"");
        StringAssert.Contains(stylesXaml, "<Setter Property=\"BorderThickness\" Value=\"0\" />");
        StringAssert.Contains(stylesXaml, "x:Key=\"SettingsRow\"");
        StringAssert.Contains(stylesXaml, "<Setter Property=\"BorderThickness\" Value=\"0,1,0,0\" />");
        StringAssert.Contains(pageXaml, "Style=\"{StaticResource SettingsFirstRow}\"");
        Assert.IsFalse(pageXaml.Contains("<CheckBox", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SettingsComboBox_SelectedValueUsesSharedLightForeground()
    {
        var root = FindRepositoryRoot();
        var stylesXaml = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Styles",
            "SettingsPageStyles.xaml"));

        StringAssert.Contains(stylesXaml, "<Setter Property=\"Foreground\" Value=\"#F4F7FB\" />");
        StringAssert.Contains(
            stylesXaml,
            "TextElement.Foreground=\"{Binding Foreground, RelativeSource={RelativeSource AncestorType=ComboBox}}\"");
    }

    [TestMethod]
    public async Task FrozenSettingsDesign_UsesExactSidebarAndLocalDataIcons()
    {
        var root = FindRepositoryRoot();
        var pageXaml = await ReadSettingsPageAsync(root);
        var stylesXaml = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Styles",
            "SettingsPageStyles.xaml"));

        foreach (var iconKey in new[]
                 {
                     "Icon.Play",
                     "Icon.Subtitle",
                     "Icon.Server",
                     "Icon.Database",
                     "Icon.Info",
                     "Icon.Refresh"
                 })
        {
            StringAssert.Contains(pageXaml, $"{{StaticResource {iconKey}}}");
        }

        StringAssert.Contains(stylesXaml, "<Setter Property=\"Size\" Value=\"20\" />");
        Assert.IsFalse(pageXaml.Contains("{StaticResource Icon.History}", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task LocalDataActions_KeepHtmlButtonHeightAndAboutDividerFlow()
    {
        var root = FindRepositoryRoot();
        var pageXaml = await ReadSettingsPageAsync(root);

        Assert.IsTrue(
            CountOccurrences(pageXaml, "VerticalAlignment=\"Center\"") >= 2,
            "Local-data action buttons must opt out of Grid stretch.");
        StringAssert.Contains(pageXaml, "<Border MinHeight=\"196\"");
        StringAssert.Contains(pageXaml, "<Grid.RowDefinitions>");
        StringAssert.Contains(pageXaml, "<Border Grid.Row=\"1\"");
        Assert.IsFalse(pageXaml.Contains("Margin=\"0,86,0,0\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AccountActionsAndVolumeSlider_MatchFrozenHtmlVisuals()
    {
        var root = FindRepositoryRoot();
        var pageXaml = await ReadSettingsPageAsync(root);
        var stylesXaml = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Styles",
            "SettingsPageStyles.xaml"));

        Assert.AreEqual(3, CountOccurrences(pageXaml, "{StaticResource Icon.Server}"));
        StringAssert.Contains(pageXaml, "{StaticResource Icon.Logout}");
        StringAssert.Contains(pageXaml, "{StaticResource SettingsButtonIcon}");
        StringAssert.Contains(stylesXaml, "x:Key=\"SettingsButtonIcon\"");
        StringAssert.Contains(stylesXaml, "<Setter Property=\"Size\" Value=\"20\" />");
        StringAssert.Contains(stylesXaml, "x:Key=\"SettingsSliderThumb\"");
        StringAssert.Contains(stylesXaml, "<Setter Property=\"Width\" Value=\"24\" />");
        StringAssert.Contains(stylesXaml, "<Setter Property=\"Height\" Value=\"24\" />");
        StringAssert.Contains(stylesXaml, "<Grid x:Name=\"ThumbVisual\"");
        StringAssert.Contains(pageXaml, "<controls:SettingsSlider x:Name=\"DefaultVolumeSlider\"");
        StringAssert.Contains(stylesXaml, "TargetType=\"{x:Type controls:SettingsSlider}\"");
        StringAssert.Contains(stylesXaml, "<Setter Property=\"IsMoveToPointEnabled\" Value=\"False\" />");
        StringAssert.Contains(stylesXaml, "<Grid Background=\"Transparent\">");
        StringAssert.Contains(stylesXaml, "Height=\"4\"");
        StringAssert.Contains(stylesXaml, "x:Key=\"SettingsAccentHoverBrush\"");
        StringAssert.Contains(stylesXaml, "x:Key=\"SettingsAccentPressedBrush\"");
        StringAssert.Contains(stylesXaml, "<Condition Property=\"Background\" Value=\"{StaticResource SettingsAccentBrush}\" />");
        StringAssert.Contains(stylesXaml, "<Setter Property=\"Foreground\" Value=\"#071008\" />");
        StringAssert.Contains(stylesXaml, "x:Key=\"SettingsHeaderBackButton\"");
        StringAssert.Contains(pageXaml, "Command=\"{Binding NavigateHomeCommand}\"");
        StringAssert.Contains(pageXaml, "AutomationProperties.Name=\"返回首页\"");
        Assert.IsFalse(stylesXaml.Contains("<Setter Property=\"Width\" Value=\"18\" />", StringComparison.Ordinal));
        Assert.IsFalse(stylesXaml.Contains("<Setter Property=\"Height\" Value=\"18\" />", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PageCodeBehind_KeepsSettingsPersistenceInViewModel()
    {
        var root = FindRepositoryRoot();
        var pageXaml = await ReadSettingsPageAsync(root);
        var codeBehind = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "SettingsPage.xaml.cs"));

        foreach (var forbidden in new[]
                 {
                     "SavePlayerPreferencesAsync",
                     "IAppSettingsService",
                     "AppShellViewModel",
                     "NavigateCommand"
                 })
        {
            Assert.IsFalse(
                pageXaml.Contains(forbidden, StringComparison.Ordinal)
                || codeBehind.Contains(forbidden, StringComparison.Ordinal),
                $"Settings preview must not use real behavior: {forbidden}");
        }

        Assert.IsFalse(codeBehind.Contains("DispatcherTimer", StringComparison.Ordinal));
        Assert.IsFalse(codeBehind.Contains("MarkDirty()", StringComparison.Ordinal));
        Assert.IsFalse(codeBehind.Contains("ShowToast(", StringComparison.Ordinal));
        StringAssert.Contains(pageXaml, "IsChecked=\"{Binding AutoPlayNextEpisode, Mode=TwoWay}\"");
        StringAssert.Contains(pageXaml, "Command=\"{Binding RetrySaveCommand}\"");
        StringAssert.Contains(pageXaml, "Command=\"{Binding RequestLogoutCommand}\"");
        StringAssert.Contains(pageXaml, "Command=\"{Binding RequestSwitchServerCommand}\"");
    }

    [TestMethod]
    public async Task AccountConfirmation_ReusesDialogDefaultsToCancelAndSupportsEscape()
    {
        var root = FindRepositoryRoot();
        var pageXaml = await ReadSettingsPageAsync(root);
        var codeBehind = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "SettingsPage.xaml.cs"));

        StringAssert.Contains(pageXaml, "x:Name=\"DialogOverlay\"");
        StringAssert.Contains(pageXaml, "x:Name=\"AccountActionCancelButton\"");
        StringAssert.Contains(pageXaml, "Command=\"{Binding ConfirmAccountActionCommand}\"");
        StringAssert.Contains(pageXaml, "Style=\"{StaticResource SettingsDangerButton}\"");
        StringAssert.Contains(pageXaml, "AutomationProperties.Name=\"取消账户操作\"");
        StringAssert.Contains(pageXaml, "AutomationProperties.HelpText=\"确认后清除当前登录状态和服务器地址，并返回服务器连接页面。\"");
        StringAssert.Contains(codeBehind, "AccountActionCancelButton.Focus()");
        StringAssert.Contains(codeBehind, "e.Key == Key.Escape && viewModel.IsAnyDialogOpen");
        StringAssert.Contains(codeBehind, "viewModel.CancelDialogCommand.Execute(null)");
    }

    [TestMethod]
    public async Task PlayerPage_DoesNotUseSettingsPageVisualComponents()
    {
        var root = FindRepositoryRoot();
        var playerPage = await File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "PlayerPage.xaml"));

        foreach (var settingsVisual in new[]
                 {
                     "SettingsToggle",
                     "SettingsActionButton",
                     "SettingsDangerActionButton",
                     "SettingsStatusBadge",
                     "SettingsConfirmDialog",
                     "SettingsToast"
                 })
        {
            Assert.IsFalse(playerPage.Contains(settingsVisual, StringComparison.Ordinal));
        }
    }

    private static Task<string> ReadSettingsPageAsync(string root)
    {
        return File.ReadAllTextAsync(Path.Combine(
            root,
            "src",
            "EmbyPlayer.UI",
            "Pages",
            "SettingsPage.xaml"));
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
