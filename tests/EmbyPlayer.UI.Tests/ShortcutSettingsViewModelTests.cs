using System.Windows.Input;
using EmbyPlayer.Core.Settings;
using EmbyPlayer.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EmbyPlayer.UI.Tests;

[TestClass]
public sealed class ShortcutSettingsViewModelTests
{
    [TestMethod]
    public void Capture_RequiresExplicitActivationAndKeepsConflictVisibleUntilValidCombination()
    {
        var viewModel = new ShortcutSettingsViewModel(() => true);
        Assert.IsFalse(viewModel.Capture(Key.K, ModifierKeys.Control));
        var row = viewModel.Rows.Single(row => row.Action == PlayerShortcutAction.ToggleMute);
        viewModel.StartCapture(row);
        Assert.IsTrue(viewModel.Capture(Key.LeftCtrl, ModifierKeys.Control));
        Assert.IsTrue(row.IsCapturing);
        viewModel.Capture(Key.Space, ModifierKeys.None);
        Assert.IsTrue(row.HasError);
        StringAssert.Contains(row.ErrorMessage, "播放 / 暂停");
        Assert.AreEqual("M", viewModel.Bindings.ToggleMute);
        viewModel.Capture(Key.K, ModifierKeys.Control | ModifierKeys.Shift);
        Assert.AreEqual("Ctrl+Shift+K", viewModel.Bindings.ToggleMute);
        Assert.IsFalse(viewModel.HasError);
        Assert.IsNull(viewModel.CapturingRow);
    }

    [TestMethod]
    public void Capture_RejectsSystemChordsAndEscapeCancelsWithoutChangingDraft()
    {
        var viewModel = new ShortcutSettingsViewModel(() => true);
        viewModel.StartCapture(viewModel.Rows[0]);
        viewModel.Capture(Key.F4, ModifierKeys.Alt);
        Assert.IsTrue(viewModel.HasError);
        viewModel.Capture(Key.L, ModifierKeys.Windows);
        Assert.IsTrue(viewModel.HasError);
        viewModel.Capture(Key.Escape, ModifierKeys.None);
        Assert.IsNull(viewModel.CapturingRow);
        Assert.AreEqual(PlayerShortcutBindings.Default, viewModel.Bindings);
    }

    [TestMethod]
    public void ClearAndRestore_NotifyRealChangesAndSavingDisablesEditing()
    {
        var canEdit = true;
        var viewModel = new ShortcutSettingsViewModel(() => canEdit);
        var changes = 0;
        viewModel.Changed += (_, _) => changes++;
        viewModel.ClearCommand.Execute(viewModel.Rows[0]);
        Assert.AreEqual(string.Empty, viewModel.Bindings.TogglePlayPause);
        viewModel.RestoreDefaultsCommand.Execute(null);
        Assert.AreEqual(PlayerShortcutBindings.Default, viewModel.Bindings);
        Assert.AreEqual(2, changes);
        canEdit = false;
        Assert.IsFalse(viewModel.CaptureCommand.CanExecute(null));
        Assert.IsFalse(viewModel.TryAssign(PlayerShortcutAction.ToggleMute, "K"));
        Assert.AreEqual(PlayerShortcutBindings.Default, viewModel.Bindings);
    }

    [DataTestMethod]
    [DataRow(Key.Enter, "Enter")]
    [DataRow(Key.PageUp, "PageUp")]
    [DataRow(Key.PageDown, "PageDown")]
    public void KeyAliases_ProduceStableStoredNames(Key key, string expected)
    {
        Assert.AreEqual(expected, PlayerShortcutInput.Format(key, ModifierKeys.None));
    }
}
