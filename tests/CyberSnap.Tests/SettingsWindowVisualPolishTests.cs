using System.Reflection;
using System.Text.RegularExpressions;
using CyberSnap.Services;
using CyberSnap.UI;
using Xunit;

namespace CyberSnap.Tests;

public sealed class SettingsWindowVisualPolishTests
{
    [Fact]
    public void TopLevelSettingsPagesAllowHorizontalOverflowAtMinimumSize()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));
        var code = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml.cs"));

        Assert.Contains("Width=\"960\"", xaml);
        Assert.Contains("MinHeight=\"560\" MinWidth=\"860\"", xaml);
        Assert.Contains("<ColumnDefinition Width=\"150\"/>", xaml);
        Assert.Contains("Padding=\"10,14,8,18\"", xaml);
        Assert.Contains("Margin=\"18,10,18,0\"", xaml);
        Assert.Contains("FontSize=\"20\"", xaml);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"52\"/>", xaml);
        AssertSettingsPageAllowsHorizontalOverflow(xaml, "HotkeysPanel");
        AssertSettingsPageDisablesHorizontalOverflow(xaml, "CapturePanel");
        AssertSettingsPageAllowsHorizontalOverflow(xaml, "RecordingPanel");
        AssertSettingsPageAllowsHorizontalOverflow(xaml, "OcrPanel");
        AssertSettingsPageAllowsHorizontalOverflow(xaml, "SettingsPanel");
        AssertSettingsPageAllowsHorizontalOverflow(xaml, "ToastPanel");
        AssertSettingsPageAllowsHorizontalOverflow(xaml, "AboutPanel");
        Assert.Contains("Padding=\"18,42,18,18\"", xaml);
        Assert.DoesNotContain("Padding=\"24,52,24,24\"", xaml);
        Assert.DoesNotContain("Padding=\"32,52,32,24\"", xaml);
        Assert.DoesNotContain("MinWidth=\"220\"", xaml);
        Assert.DoesNotContain("MinWidth=\"210\"", xaml);
        Assert.DoesNotContain("MinWidth=\"190\"", xaml);
        Assert.DoesNotContain("MinWidth=\"180\" MaxWidth=\"300\"", xaml);
        Assert.DoesNotContain("Width=\"356\"", xaml);

        var fitBlock = GetMethodBlock(code, "private void EnsureSettingsWindowFitsWorkArea()");
        Assert.Contains("SystemParameters.WorkArea", fitBlock);
        Assert.Contains("MinWidth = Math.Min(MinWidth, maxWidth);", fitBlock);
        Assert.Contains("if (Width > maxWidth)", fitBlock);
        Assert.Contains("Left = Math.Min(Math.Max(Left, minLeft), Math.Max(minLeft, maxLeft));", fitBlock);
        Assert.Contains("Loaded += (_, _) => EnsureSettingsWindowFitsWorkArea();", code);
    }

    [Fact]
    public void UiScaleFallbackSelectsNormalScale()
    {
        var appearanceCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Appearance.cs"));
        var selectBlock = GetMethodBlock(appearanceCode, "private void SelectUiScale(double scale)");

        Assert.Contains("SelectComboByTag(UiScaleCombo, \"1.0\");", selectBlock);
        Assert.DoesNotContain("UiScaleCombo.SelectedIndex = 2;", selectBlock);
    }

    [Fact]
    public void SettingsControlsUseSharedSizingSystem()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));

        Assert.Contains("x:Key=\"SettingsControlHeight\">30</sys:Double>", xaml);
        Assert.Contains("x:Key=\"SettingsControlPadding\">10,5</Thickness>", xaml);
        Assert.Contains("x:Key=\"SettingsControlFontSize\">12</sys:Double>", xaml);
        Assert.Contains("x:Key=\"SettingsControlMinWidth\">88</sys:Double>", xaml);
        Assert.Contains("<Style TargetType=\"ComboBox\">", xaml);
        Assert.Contains("<Style TargetType=\"ComboBoxItem\">", xaml);
        Assert.Contains("ThemeInputBackgroundBrush", xaml);
        Assert.Contains("<Setter Property=\"Height\" Value=\"{StaticResource SettingsControlHeight}\"/>", xaml);
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"{StaticResource SettingsControlMinWidth}\"/>", xaml);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"{StaticResource SettingsControlPadding}\"/>", xaml);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"{StaticResource SettingsControlFontSize}\"/>", xaml);
        Assert.DoesNotContain("FontSize=\"11.5\" Padding=\"8,4\"", xaml);
        Assert.DoesNotContain("FontSize=\"11\" Padding=\"7,4\"", xaml);
        Assert.DoesNotContain("MinWidth=\"64\"", xaml);
        Assert.Contains("<ControlTemplate TargetType=\"ComboBox\">", xaml);
        Assert.Contains("x:Name=\"PART_Popup\"", xaml);
        Assert.Contains("Background=\"{DynamicResource ThemeCardBrush}\"", xaml);
        Assert.DoesNotContain("Background=\"White\"", xaml);
    }

    [Fact]
    public void SettingsTogglesUseWinUiSwitchSizing()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));
        var checkBoxStyle = GetXamlElementBlock(xaml, "<Style TargetType=\"CheckBox\">", "</Style>");

        Assert.Contains("<Setter Property=\"Width\" Value=\"42\"/>", checkBoxStyle);
        Assert.Contains("<Setter Property=\"Height\" Value=\"20\"/>", checkBoxStyle);
        Assert.Contains("Width=\"40\"", checkBoxStyle);
        Assert.Contains("Height=\"20\"", checkBoxStyle);
        Assert.Contains("CornerRadius=\"10\"", checkBoxStyle);
        Assert.Contains("Width=\"12\"", checkBoxStyle);
        Assert.Contains("Height=\"12\"", checkBoxStyle);
        Assert.Contains("Margin=\"4,0\"", checkBoxStyle);
        Assert.Contains("<Trigger Property=\"IsKeyboardFocused\" Value=\"True\">", checkBoxStyle);
    }

    [Fact]
    public void SettingsRowsKeepNormalTwoColumnLayoutAtMinimumSize()
    {
        var code = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml.cs"));

        var appearanceCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Appearance.cs"));

        Assert.DoesNotContain("ScheduleResponsiveSettingRowsUpdate", code);
        Assert.DoesNotContain("ScheduleResponsiveSettingRowsUpdate", appearanceCode);
        Assert.DoesNotContain("UpdateResponsiveSettingRows", code);
        Assert.DoesNotContain("ShouldCompactSettingRows", code);
        Assert.DoesNotContain("ResponsiveSettingControlState", code);
    }

    [Fact]
    public void SettingsTextInputsAndActionButtonsHaveAccessibleLabels()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));

        AssertNamedControlHasLabel(xaml, "FileNameTemplateBox", "<TextBox", "File name pattern", "Use tokens to build capture file names.");
        AssertSettingsActionButton(xaml, "OpenSourceLocalInstallBtn", "Install open-source local translator", "Install or remove the open-source local translation runtime", "OpenSourceLocalInstallBtn_Click");
        AssertSettingsActionButton(xaml, "ArgosInstallBtn", "Install Argos Translate", "Install or remove the Argos local translation runtime", "ArgosInstallBtn_Click");
        AssertSettingsActionButton(xaml, "ResetImageIndexesBtn", "Reset image search cache", "Reset the image search index cache", "ResetImageIndexesBtn_Click");
        AssertSettingsActionButton(xaml, "ResetToastButtonsBtn", "Reset toast button layout", "Restore the default toast button layout", "ResetToastButtonsBtn_Click");

        foreach (Match match in Regex.Matches(xaml, @"<TextBox\b[^>]*x:Name=""[^""]+""[^>]*>", RegexOptions.Singleline))
        {
            Assert.Contains("AutomationProperties.Name=", match.Value);
            Assert.Contains("ToolTip=", match.Value);
        }

        foreach (Match match in Regex.Matches(xaml, @"<Button\b[^>]*x:Name=""[^""]+""[^>]*>", RegexOptions.Singleline))
        {
            Assert.Contains("AutomationProperties.Name=", match.Value);
            Assert.Contains("ToolTip=", match.Value);
            Assert.Contains("Cursor=\"Hand\"", match.Value);
        }
    }

    [Fact]
    public void ToastLayoutDesignerControlsAreKeyboardAccessible()
    {
        var toastCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Toast.cs"));
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));

        AssertDynamicStatusTextBlock(xaml, "ToastLayoutSelectionText", "Toast layout selection", isLive: true);
        var selectionTag = GetOpeningTag(xaml, xaml.IndexOf("x:Name=\"ToastLayoutSelectionText\"", StringComparison.Ordinal), "<TextBlock");
        Assert.Contains("AutomationProperties.HelpText=\"{Binding Text, RelativeSource={RelativeSource Self}}\"", selectionTag);

        var designerBlock = GetMethodBlock(toastCode, "private void RefreshToastButtonLayoutDesigner()");
        Assert.Contains("ToastLayoutSelectionText.Text = selectionText;", designerBlock);
        Assert.Contains("ToastLayoutSelectionText.ToolTip = selectionText;", designerBlock);
        Assert.Contains("AutomationProperties.SetHelpText(ToastLayoutSelectionText, selectionText);", designerBlock);

        var buttonBlock = GetMethodBlock(toastCode, "private void UpdateToastLayoutButton(Border border, ToastButtonKind button)");
        Assert.Contains("border.Focusable = true;", buttonBlock);
        Assert.Contains("AutomationProperties.SetName(border, $\"{label} toast button\");", buttonBlock);
        Assert.Contains("AutomationProperties.SetHelpText(border, \"Press Enter or Space to move the selected button here.\");", buttonBlock);
        Assert.Contains("border.KeyDown -= ToastLayoutButton_KeyDown;", buttonBlock);
        Assert.Contains("border.KeyDown += ToastLayoutButton_KeyDown;", buttonBlock);

        var slotBlock = GetMethodBlock(toastCode, "private void UpdateToastLayoutSlot(Border slotBorder, ToastButtonSlot slot)");
        Assert.Contains("slotBorder.Focusable = true;", slotBlock);
        Assert.Contains("AutomationProperties.SetName(slotBorder, $\"{label} toast slot\");", slotBlock);
        Assert.Contains("AutomationProperties.SetHelpText(slotBorder, \"Press Enter or Space to place the selected toast button here.\");", slotBlock);
        Assert.Contains("slotBorder.KeyDown -= ToastLayoutSlot_KeyDown;", slotBlock);
        Assert.Contains("slotBorder.KeyDown += ToastLayoutSlot_KeyDown;", slotBlock);

        var shelfBlock = GetMethodBlock(toastCode, "private void RefreshToastHiddenShelf()");
        Assert.Contains("ToastHiddenShelf.Focusable = true;", shelfBlock);
        Assert.Contains("AutomationProperties.SetName(ToastHiddenShelf, \"Hidden toast button shelf\");", shelfBlock);
        Assert.Contains("AutomationProperties.SetHelpText(ToastHiddenShelf, \"Press Enter or Space to hide the selected toast button.\");", shelfBlock);
        Assert.Contains("ToastHiddenShelf.KeyDown -= ToastHiddenShelf_KeyDown;", shelfBlock);
        Assert.Contains("ToastHiddenShelf.KeyDown += ToastHiddenShelf_KeyDown;", shelfBlock);

        var hiddenChipBlock = GetMethodBlock(toastCode, "private Border CreateHiddenToastButtonChip(ToastButtonKind button)");
        Assert.Contains("Focusable = true,", hiddenChipBlock);
        Assert.Contains("ToolTip = $\"Select hidden {label} toast button\",", hiddenChipBlock);
        Assert.Contains("AutomationProperties.SetName(chip, $\"Hidden {label} toast button\");", hiddenChipBlock);
        Assert.Contains("AutomationProperties.SetHelpText(chip, \"Press Enter or Space to select this hidden button, then choose a slot.\");", hiddenChipBlock);
        Assert.Contains("chip.KeyDown += ToastHiddenButton_KeyDown;", hiddenChipBlock);

        Assert.Contains("private void ToastLayoutButton_KeyDown(object sender, KeyEventArgs e)", toastCode);
        Assert.Contains("private void ToastLayoutSlot_KeyDown(object sender, KeyEventArgs e)", toastCode);
        Assert.Contains("private void ToastHiddenShelf_KeyDown(object sender, KeyEventArgs e)", toastCode);
        Assert.Contains("private void ToastHiddenButton_KeyDown(object sender, KeyEventArgs e)", toastCode);
        Assert.Contains("=> e.Key is Key.Enter or Key.Space;", toastCode);
    }

    [Fact]
    public void FileNameTemplateInputUsesResponsiveWidth()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));

        AssertTextBoxUsesResponsiveWidth(xaml, "FileNameTemplateBox", "145", "300");
        AssertNamedControlHasLabel(xaml, "FileNameTemplateBox", "<TextBox", "File name pattern", "Use tokens to build capture file names.");
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", GetOpeningTag(xaml, xaml.IndexOf("x:Name=\"FileNameTemplateBox\"", StringComparison.Ordinal), "<TextBox"));
    }

    [Fact]
    public void FileNameTokenChipsUseWrapFriendlySpacing()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));
        var settingsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml.cs"));

        Assert.Contains("<WrapPanel x:Name=\"FileNameTokenPanel\" Margin=\"0,8,0,-6\"/>", xaml);

        var tokenBlock = GetMethodBlock(settingsCode, "private void LoadFileNameTokenButtons()");
        Assert.Contains("MinHeight = 28", tokenBlock);
        Assert.Contains("Margin = new Thickness(0, 0, 6, 6),", tokenBlock);
        Assert.DoesNotContain("new Thickness(6, 0, 0, 0)", tokenBlock);
    }

    [Fact]
    public void FileNameTemplateTokenButtonsAreAccessibleAndEasyToClick()
    {
        var settingsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml.cs"));
        var tokenButtonBlock = GetMethodBlock(settingsCode, "private void LoadFileNameTokenButtons()");

        Assert.Contains("ToolTip = $\"Insert {label} token\"", tokenButtonBlock);
        Assert.Contains("MinHeight = 28", tokenButtonBlock);
        Assert.Contains("Padding = new Thickness(9, 4, 9, 4)", tokenButtonBlock);
        Assert.Contains("Cursor = System.Windows.Input.Cursors.Hand", tokenButtonBlock);
        Assert.Contains("AutomationProperties.SetName(button, $\"Insert {label} token\");", tokenButtonBlock);
        Assert.Contains("AutomationProperties.SetHelpText(button, token);", tokenButtonBlock);
        Assert.DoesNotContain("ToolTip = label", tokenButtonBlock);
        Assert.DoesNotContain("Padding = new Thickness(8, 3, 8, 3)", tokenButtonBlock);
    }

    [Fact]
    public void SaveDirectoryControlsUseCompactReadableLayout()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));
        var settingsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml.cs"));

        AssertTextBoxUsesResponsiveWidth(xaml, "SaveDirBox", "145", "360");

        var saveDirIndex = xaml.IndexOf("x:Name=\"SaveDirBox\"", StringComparison.Ordinal);
        var saveDirTag = GetOpeningTag(xaml, saveDirIndex, "<TextBox");
        Assert.Contains("IsReadOnly=\"True\"", saveDirTag);
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", saveDirTag);
        Assert.Contains("ToolTip=\"{Binding Text, RelativeSource={RelativeSource Self}}\"", saveDirTag);
        Assert.Contains("AutomationProperties.Name=\"Current save folder\"", saveDirTag);

        var appearanceCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Appearance.cs"));
        Assert.Contains("SetSaveDirectoryPath(s.SaveDirectory);", appearanceCode);

        var browseIndex = xaml.IndexOf("x:Name=\"BrowseSaveDirBtn\"", StringComparison.Ordinal);
        Assert.True(browseIndex >= 0, "Could not find BrowseSaveDirBtn.");
        var browseTag = GetOpeningTag(xaml, browseIndex, "<Button");
        Assert.Contains("ToolTip=\"Choose save folder\"", browseTag);
        Assert.Contains("AutomationProperties.Name=\"Choose save folder\"", browseTag);
        Assert.Contains("Cursor=\"Hand\"", browseTag);
        Assert.Contains("Click=\"BrowseButton_Click\"", browseTag);

    }

    [Fact]
    public void HistoryLoadFailureEmptyStateOffersRetryAction()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "HistoryWindow.xaml"));
        var historyCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.History.cs"));

        Assert.Contains("x:Name=\"HistoryEmptyRetryButton\"", xaml);
        Assert.Contains("Content=\"Retry\"", xaml);
        Assert.Contains("Click=\"HistoryEmptyRetryButton_Click\"", xaml);
        AssertDynamicStatusTextBlock(xaml, "HistoryEmptyTitle", "History empty state title", isLive: true);
        AssertDynamicStatusTextBlock(xaml, "HistoryEmptyLabel", "History empty state detail", isLive: true);
        var retryTag = GetOpeningTag(xaml, xaml.IndexOf("x:Name=\"HistoryEmptyRetryButton\"", StringComparison.Ordinal), "<Button");
        Assert.Contains("AutomationProperties.HelpText=\"Retry loading history\"", retryTag);
        Assert.Contains("HistoryEmptyRetryButton.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed;", historyCode);
        Assert.Contains("HistoryEmptyRetryButton.Visibility = Visibility.Collapsed;", historyCode);
        var emptyStateBlock = GetMethodBlock(historyCode, "private void ShowHistoryEmptyState(string title, string detail, bool showRetry = false)");
        Assert.Contains("HistoryEmptyTitle.ToolTip = title;", emptyStateBlock);
        Assert.Contains("AutomationProperties.SetHelpText(HistoryEmptyTitle, title);", emptyStateBlock);
        Assert.Contains("HistoryEmptyLabel.ToolTip = detail;", emptyStateBlock);
        Assert.Contains("AutomationProperties.SetHelpText(HistoryEmptyLabel, detail);", emptyStateBlock);
        Assert.Contains("ShowHistoryEmptyState(\"Couldn't load captures\", \"Retry loading history. If it still fails, check the app log.\", showRetry: true);", historyCode);
    }

    [Fact]
    public void HistoryServiceChangeCallbackFailuresOfferRetryState()
    {
        var settingsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "HistoryWindow.xaml.cs"));

        var changedBlock = GetMethodBlock(settingsCode, "private void HistoryService_Changed()");
        Assert.Contains("_ = Dispatcher.BeginInvoke(() =>", changedBlock);
        Assert.Contains("try", changedBlock);
        Assert.Contains("InvalidateHistoryCategoryCaches();", changedBlock);
        Assert.Contains("_pendingHistoryDataRefresh = true;", changedBlock);
        Assert.Contains("QueueHistoryRefresh(reloadFromDisk: false);", changedBlock);
        Assert.Contains("catch (Exception ex)", changedBlock);
        Assert.Contains("AppDiagnostics.LogError(\"history.history-service-changed\", ex);", changedBlock);
        Assert.Contains("_pendingHistoryDataRefresh = false;", changedBlock);
        Assert.Contains("_pendingHistoryUiRefresh = false;", changedBlock);
        Assert.Contains("_pendingHistoryDiskRefresh = false;", changedBlock);
        Assert.Contains("_historyRefreshTimer.Stop();", changedBlock);
        Assert.Contains("ShowHistoryEmptyState(\"Couldn't refresh history\", \"Retry loading history. If it still fails, check the app log.\", showRetry: true);", changedBlock);
    }

    [Fact]
    public void SettingsImportExportActionsLeaveInlineStatus()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));
        var preferencesCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Preferences.cs"));

        Assert.Contains("x:Name=\"SettingsImportExportStatusText\"", xaml);
        AssertNamedTextBlockUsesStyle(xaml, "SettingsImportExportStatusText", "SettingsHelperText");
        var statusTag = GetOpeningTag(xaml, xaml.IndexOf("x:Name=\"SettingsImportExportStatusText\"", StringComparison.Ordinal), "<TextBlock");
        Assert.Contains("Visibility=\"Collapsed\"", statusTag);
        Assert.Contains("ToolTip=\"{Binding Text, RelativeSource={RelativeSource Self}}\"", statusTag);
        Assert.Contains("AutomationProperties.Name=\"Settings import and export status\"", statusTag);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", statusTag);
        AssertSettingsActionButton(xaml, "ExportSettingsBtn", "Export settings", "Export redacted settings to a JSON file", "ExportSettingsButton_Click");
        AssertSettingsActionButton(xaml, "ImportSettingsBtn", "Import settings", "Import settings from a JSON file", "ImportSettingsButton_Click");

        var exportBlock = GetMethodBlock(preferencesCode, "private void ExportSettingsButton_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("SetSettingsImportExportStatus($\"Settings exported to {Path.GetFileName(dlg.FileName)}.\");", exportBlock);
        Assert.Contains("ShowSettingsExportFailed(ex);", exportBlock);

        var exportFailureBlock = GetMethodBlock(preferencesCode, "private void ShowSettingsExportFailed(Exception ex)");
        Assert.Contains("SetSettingsImportExportStatus(\"Export failed. Choose another folder and try again.\");", exportFailureBlock);
        Assert.Contains("CyberSnap could not write the settings export. Choose another folder and try again.", exportFailureBlock);
        Assert.Contains("{ex.Message}", exportFailureBlock);

        var importBlock = GetMethodBlock(preferencesCode, "private void ImportSettingsButton_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("SetSettingsImportExportStatus(\"Import failed: invalid settings file.\");", importBlock);
        Assert.Contains("SetSettingsImportExportStatus(\"Settings imported and applied.\");", importBlock);
        Assert.Contains("AppSettings? previous = null;", preferencesCode);
        Assert.Contains("previous = _settingsService.Settings;", importBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.import\", ex);", importBlock);
        Assert.Contains("_settingsService.Settings = previous;", importBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.import-rollback\", rollbackEx);", importBlock);
        Assert.Contains("RestoreSettingsUiAfterFailedReset();", importBlock);
        Assert.Contains("ShowSettingsImportFailed(previous is not null, ex);", importBlock);

        var importFailureBlock = GetMethodBlock(preferencesCode, "private void ShowSettingsImportFailed(bool restoredPrevious, Exception ex)");
        Assert.Contains("SetSettingsImportExportStatus(restoredPrevious", importFailureBlock);
        Assert.Contains("Import failed. Previous settings restored.", importFailureBlock);
        Assert.Contains("The imported settings were not saved. Previous settings were restored. Check the file and try again.", importFailureBlock);
        Assert.Contains("Import failed. Check the file and try again.", importFailureBlock);
        Assert.Contains("CyberSnap could not import settings. Check the file and try again.", importFailureBlock);
        Assert.Contains("ToastWindow.ShowError(\"Import failed\", message);", importFailureBlock);

        var statusBlock = GetMethodBlock(preferencesCode, "private void SetSettingsImportExportStatus(string message)");
        Assert.Contains("SettingsImportExportStatusText.Text = message;", statusBlock);
        Assert.Contains("Visibility.Collapsed", statusBlock);
        Assert.Contains("Visibility.Visible", statusBlock);
    }

    [Fact]
    public void ImageIndexResetLeavesInlineStatusAndLogsFailures()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));
        var settingsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml.cs"));
        var preferencesCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Preferences.cs"));

        Assert.Contains("x:Name=\"ResetImageIndexesBtn\"", xaml);
        Assert.Contains("x:Name=\"ImageIndexMaintenanceStatusText\"", xaml);
        AssertNamedTextBlockUsesStyle(xaml, "ImageIndexMaintenanceStatusText", "SettingsStatusText");
        var statusTag = GetOpeningTag(xaml, xaml.IndexOf("x:Name=\"ImageIndexMaintenanceStatusText\"", StringComparison.Ordinal), "<TextBlock");
        Assert.Contains("Visibility=\"Collapsed\"", statusTag);
        Assert.Contains("ToolTip=\"{Binding Text, RelativeSource={RelativeSource Self}}\"", statusTag);
        Assert.Contains("AutomationProperties.Name=\"Image index status\"", statusTag);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", statusTag);
        Assert.Contains("private bool ImageIndexResetInProgress { get; set; }", settingsCode);

        var resetBlock = GetMethodBlock(preferencesCode, "private void ResetImageIndexesBtn_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("if (ImageIndexResetInProgress)", resetBlock);
        Assert.Contains("SetImageIndexMaintenanceStatus(\"Image index reset is already running.\");", resetBlock);
        Assert.Contains("SetImageIndexMaintenanceStatus(\"Image index reset canceled. Existing search data was left in place.\");", resetBlock);
        Assert.Contains("ImageIndexResetInProgress = true;", resetBlock);
        Assert.Contains("ResetImageIndexesBtn.IsEnabled = false;", resetBlock);
        Assert.Contains("ResetImageIndexesBtn.Content = \"Resetting...\";", resetBlock);
        Assert.Contains("try", resetBlock);
        Assert.Contains("_imageSearchIndexService.ReindexAll(_historyService.ImageEntries, _settingsService.Settings.OcrLanguageTag);", resetBlock);
        Assert.Contains("SetImageIndexMaintenanceStatus(\"Image search index reset requested.\");", resetBlock);
        Assert.Contains("ToastWindow.Show(\"Image indexes reset\", \"Screenshot search will rebuild in the background.\");", resetBlock);
        Assert.Contains("catch (Exception ex)", resetBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.image-index-reset\", ex);", resetBlock);
        Assert.Contains("SetImageIndexMaintenanceStatus(\"Image index reset failed. Existing search data was left in place.\");", resetBlock);
        Assert.Contains("CyberSnap could not reset the image search index. Existing search data was left in place. Try again from Settings.", resetBlock);
        Assert.Contains("return;", resetBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.image-index-reset-history-refresh\", ex);", resetBlock);
        Assert.Contains("SetImageIndexMaintenanceStatus(\"Image index reset requested, but History did not refresh.\");", resetBlock);
        Assert.Contains("The image index reset was requested, but History did not refresh. Switch tabs or use Retry in History.", resetBlock);
        Assert.Contains("finally", resetBlock);
        Assert.Contains("ImageIndexResetInProgress = false;", resetBlock);
        Assert.Contains("ResetImageIndexesBtn.Content = \"Reset cache\";", resetBlock);
        Assert.Contains("ResetImageIndexesBtn.IsEnabled = true;", resetBlock);

        var statusBlock = GetMethodBlock(preferencesCode, "private void SetImageIndexMaintenanceStatus(string message)");
        Assert.Contains("ImageIndexMaintenanceStatusText.Text = message;", statusBlock);
        Assert.Contains("Visibility.Collapsed", statusBlock);
        Assert.Contains("Visibility.Visible", statusBlock);
    }

    [Fact]
    public void AutoIndexImagesSettingRollsBackAndReportsFailures()
    {
        var preferencesCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Preferences.cs"));

        Assert.Contains("private bool _suppressAutoIndexImagesChange;", preferencesCode);

        var autoIndexBlock = GetMethodBlock(preferencesCode, "private void AutoIndexImagesCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressAutoIndexImagesChange) return;", autoIndexBlock);
        Assert.Contains("var previous = _settingsService.Settings.AutoIndexImages;", autoIndexBlock);
        Assert.Contains("_settingsService.Settings.AutoIndexImages = enabled;", autoIndexBlock);
        Assert.Contains("_settingsService.Save();", autoIndexBlock);
        Assert.Contains("_imageSearchIndexService.RequestSync(_historyService.ImageEntries, _settingsService.Settings.OcrLanguageTag);", autoIndexBlock);
        Assert.Contains("SetImageIndexMaintenanceStatus(enabled", autoIndexBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.auto-index-images\", ex);", autoIndexBlock);
        Assert.Contains("_settingsService.Settings.AutoIndexImages = previous;", autoIndexBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.auto-index-images-rollback\", rollbackEx);", autoIndexBlock);
        Assert.Contains("_suppressAutoIndexImagesChange = true;", autoIndexBlock);
        Assert.Contains("AutoIndexImagesCheck.IsChecked = previous;", autoIndexBlock);
        Assert.Contains("_suppressAutoIndexImagesChange = false;", autoIndexBlock);
        Assert.Contains("SetImageIndexMaintenanceStatus(\"Automatic image indexing failed. Previous setting restored.\");", autoIndexBlock);
        Assert.Contains("The previous image indexing setting was restored. Try again from Settings.", autoIndexBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.auto-index-images-history-refresh\", ex);", autoIndexBlock);
        Assert.Contains("SetImageIndexMaintenanceStatus(\"Image indexing saved, but History did not refresh.\");", autoIndexBlock);
        Assert.Contains("The image indexing setting was saved, but History did not refresh. Switch tabs or use Retry in History.", autoIndexBlock);
    }

    [Fact]
    public void SettingsResetAndUninstallActionsLeaveRecoverableStatus()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));
        var preferencesCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Preferences.cs"));

        AssertSettingsActionButton(xaml, "ResetSettingsBtn", "Reset settings", "Reset all settings to defaults", "ResetSettingsButton_Click");
        AssertSettingsActionButton(xaml, "UninstallCyberSnapBtn", "Uninstall CyberSnap", "Start the CyberSnap uninstall flow", "UninstallButton_Click");

        var resetBlock = GetMethodBlock(preferencesCode, "private void ResetSettingsButton_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("SetSettingsImportExportStatus(\"Reset canceled. Existing settings kept.\");", resetBlock);
        Assert.True(
            CountOccurrences(resetBlock, "SetSettingsImportExportStatus(\"Reset canceled. Existing settings kept.\");") >= 3,
            "Each reset confirmation cancel path should leave durable inline status.");
        Assert.Contains("var previous = _settingsService.Settings;", resetBlock);
        Assert.Contains("_settingsService.Settings = new AppSettings();", resetBlock);
        Assert.Contains("_settingsService.Save();", resetBlock);
        Assert.Contains("SetSettingsImportExportStatus(\"Settings reset to defaults.\");", resetBlock);
        Assert.Contains("ToastWindow.Show(\"Settings reset\", \"Defaults have been applied.\");", resetBlock);
        Assert.Contains("catch (Exception ex)", resetBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.reset\", ex);", resetBlock);
        Assert.Contains("_settingsService.Settings = previous;", resetBlock);
        Assert.Contains("RestoreSettingsUiAfterFailedReset();", resetBlock);
        Assert.Contains("ShowSettingsResetFailed(ex);", resetBlock);

        var resetFailureBlock = GetMethodBlock(preferencesCode, "private void ShowSettingsResetFailed(Exception ex)");
        Assert.Contains("SetSettingsImportExportStatus(\"Reset failed. Previous settings restored.\");", resetFailureBlock);
        Assert.Contains("ToastWindow.ShowError(", resetFailureBlock);
        Assert.Contains("\"Reset failed\"", resetFailureBlock);
        Assert.Contains("Defaults were not saved. Previous settings were restored. Try again after checking file permissions.", resetFailureBlock);

        var uninstallBlock = GetMethodBlock(preferencesCode, "private void UninstallButton_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("var uninstall = UninstallRequested;", uninstallBlock);
        Assert.Contains("if (uninstall is null)", uninstallBlock);
        Assert.Contains("SetSettingsImportExportStatus(\"Uninstall is not available from this window.\");", uninstallBlock);
        Assert.Contains("ToastWindow.ShowError(\"Uninstall unavailable\", \"Restart CyberSnap and try again.\");", uninstallBlock);
        Assert.Contains("SetSettingsImportExportStatus(\"Starting uninstall...\");", uninstallBlock);
        Assert.Contains("uninstall.Invoke();", uninstallBlock);
        Assert.Contains("catch (Exception ex)", uninstallBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.uninstall\", ex);", uninstallBlock);
        Assert.Contains("ShowSettingsUninstallFailed(ex);", uninstallBlock);

        var uninstallCanceledBlock = GetMethodBlock(preferencesCode, "public void ShowUninstallCanceledStatus()");
        Assert.Contains("SetSettingsImportExportStatus(\"Uninstall canceled. CyberSnap was left installed.\");", uninstallCanceledBlock);

        var uninstallFailureBlock = GetMethodBlock(preferencesCode, "private void ShowSettingsUninstallFailed(Exception ex)");
        Assert.Contains("SetSettingsImportExportStatus(\"Uninstall failed. Restart CyberSnap and try again.\");", uninstallFailureBlock);
        Assert.Contains("CyberSnap could not start uninstall. Restart CyberSnap and try again from Settings.", uninstallFailureBlock);
        Assert.Contains("{ex.Message}", uninstallFailureBlock);

        var restoreBlock = GetMethodBlock(preferencesCode, "private void RestoreSettingsUiAfterFailedReset()");
        Assert.Contains("LoadSettings();", restoreBlock);
        Assert.Contains("PopulateToolToggles();", restoreBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.reset.restore\", restoreEx);", restoreBlock);
    }

    [Fact]
    public void StartWithWindowsSettingRevertsAndReportsRegistryFailures()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));
        var settingsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml.cs"));

        Assert.Contains("private bool _suppressStartWithWindowsChange;", settingsCode);
        Assert.Contains("x:Name=\"StartupPreferenceStatusText\"", xaml);
        AssertNamedTextBlockUsesStyle(xaml, "StartupPreferenceStatusText", "SettingsStatusText");
        var startupStatusTag = GetOpeningTag(xaml, xaml.IndexOf("x:Name=\"StartupPreferenceStatusText\"", StringComparison.Ordinal), "<TextBlock");
        Assert.Contains("ToolTip=\"{Binding Text, RelativeSource={RelativeSource Self}}\"", startupStatusTag);
        Assert.Contains("AutomationProperties.Name=\"Startup preference status\"", startupStatusTag);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", startupStatusTag);

        var startWithWindowsBlock = GetMethodBlock(settingsCode, "private void StartWithWindowsCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("bool previous = _settingsService.Settings.StartWithWindows;", startWithWindowsBlock);
        Assert.Contains("UninstallService.SetStartupEntry(on);", startWithWindowsBlock);
        Assert.Contains("_settingsService.Settings.StartWithWindows = on;", startWithWindowsBlock);
        Assert.Contains("_settingsService.Save();", startWithWindowsBlock);
        Assert.Contains("SetStartupPreferenceStatus(string.Empty);", startWithWindowsBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.start-with-windows\", ex);", startWithWindowsBlock);
        Assert.Contains("UninstallService.SetStartupEntry(previous);", startWithWindowsBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.start-with-windows-rollback\", rollbackEx);", startWithWindowsBlock);
        Assert.Contains("_settingsService.Settings.StartWithWindows = previous;", startWithWindowsBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.start-with-windows-save-rollback\", rollbackEx);", startWithWindowsBlock);
        Assert.Contains("StartWithWindowsCheck.IsChecked = previous;", startWithWindowsBlock);
        Assert.Contains("ShowStartupPreferenceFailed(ex);", startWithWindowsBlock);

        var startupFailureBlock = GetMethodBlock(settingsCode, "private void ShowStartupPreferenceFailed(Exception ex)");
        Assert.Contains("SetStartupPreferenceStatus(\"Startup setting change was not saved. Previous setting restored.\");", startupFailureBlock);
        Assert.Contains("ToastWindow.ShowError(", startupFailureBlock);
        Assert.Contains("\"Startup setting failed\"", startupFailureBlock);
        Assert.Contains("The previous startup setting was restored. Check Settings -> About and try again.", startupFailureBlock);

        var registryCallIndex = startWithWindowsBlock.IndexOf("UninstallService.SetStartupEntry(on);", StringComparison.Ordinal);
        var saveIndex = startWithWindowsBlock.IndexOf("_settingsService.Save();", StringComparison.Ordinal);
        Assert.True(registryCallIndex >= 0 && saveIndex > registryCallIndex, "Startup registry change should happen before saving the setting.");

        var startupStatusBlock = GetMethodBlock(settingsCode, "private void SetStartupPreferenceStatus(string message)");
        Assert.Contains("StartupPreferenceStatusText.Text = message;", startupStatusBlock);
        Assert.Contains("Visibility.Collapsed", startupStatusBlock);
        Assert.Contains("Visibility.Visible", startupStatusBlock);
    }

    [Fact]
    public void OcrAndTranslationPreferencesRollBackAndLeaveInlineStatus()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));
        var settingsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml.cs"));
        var ocrCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Ocr.cs"));

        Assert.Contains("private bool _suppressOcrPreferenceChange;", settingsCode);
        Assert.Contains("x:Name=\"OcrPreferenceStatusText\"", xaml);
        Assert.Contains("x:Name=\"OcrAutoCopyCheck\"", xaml);
        Assert.Contains("Checked=\"OcrAutoCopyCheck_Changed\" Unchecked=\"OcrAutoCopyCheck_Changed\"", xaml);
        Assert.Contains("_settingsService.Settings.OcrAutoCopyToClipboard", ocrCode);
        AssertNamedTextBlockUsesStyle(xaml, "OcrPreferenceStatusText", "SettingsStatusText");
        Assert.Contains("x:Name=\"TranslationPreferenceStatusText\"", xaml);
        AssertNamedTextBlockUsesStyle(xaml, "TranslationPreferenceStatusText", "SettingsStatusText");
        AssertDynamicStatusTextBlock(xaml, "OcrLanguageStatusText", "OCR language availability", isLive: true);
        AssertDynamicStatusTextBlock(xaml, "OcrPreferenceStatusText", "OCR preference status", isLive: true);
        AssertDynamicStatusTextBlock(xaml, "TranslationPreferenceStatusText", "Translation preference status", isLive: true);
        AssertDynamicStatusTextBlock(xaml, "OpenSourceLocalStatusText", "Open-source local translation status", isLive: true);
        AssertDynamicStatusTextBlock(xaml, "ArgosStatusText", "Argos translation status", isLive: true);
        Assert.Contains("CreateOcrLanguageItem(", ocrCode);
        Assert.Contains("\"Auto OCR language\"", ocrCode);
        Assert.Contains("Use the Windows system language for text recognition when available.", ocrCode);
        Assert.Contains("{tag} OCR language", ocrCode);
        Assert.Contains("Use {tag} for text recognition.", ocrCode);
        Assert.Contains("CreateTranslationLanguageItem(", ocrCode);
        Assert.Contains("{name} source language", ocrCode);
        Assert.Contains("Use {name} as the default translation source.", ocrCode);
        Assert.Contains("{toName} target language", ocrCode);
        Assert.Contains("Use {toName} as the default translation target.", ocrCode);
        Assert.Contains("GoogleApiKeyBox", xaml);
        Assert.Contains("OpenSourceLocalStatusText", xaml);
        Assert.Contains("ArgosStatusText", xaml);

        var dynamicOcrItemBlock = GetMethodBlock(ocrCode, "private static ComboBoxItem CreateOcrLanguageItem(string text, string tag, string automationName, string helpText)");
        Assert.Contains("ToolTip = helpText", dynamicOcrItemBlock);
        Assert.Contains("AutomationProperties.SetName(item, automationName);", dynamicOcrItemBlock);
        Assert.Contains("AutomationProperties.SetHelpText(item, helpText);", dynamicOcrItemBlock);

        var dynamicTranslationItemBlock = GetMethodBlock(ocrCode, "private static ComboBoxItem CreateTranslationLanguageItem(string text, string tag, string automationName, string helpText)");
        Assert.Contains("ToolTip = helpText", dynamicTranslationItemBlock);
        Assert.Contains("AutomationProperties.SetName(item, automationName);", dynamicTranslationItemBlock);
        Assert.Contains("AutomationProperties.SetHelpText(item, helpText);", dynamicTranslationItemBlock);

        var loadBlock = GetMethodBlock(ocrCode, "private void LoadOcrTab()");
        Assert.Contains("_suppressOcrPreferenceChange = true;", loadBlock);
        Assert.Contains("LoadOcrLanguageOptions();", loadBlock);
        Assert.Contains("LoadTranslateLanguageCombos();", loadBlock);
        Assert.Contains("GoogleApiKeyBox.Password = _settingsService.Settings.GoogleTranslateApiKey ?? \"\";", loadBlock);
        Assert.Contains("_suppressOcrPreferenceChange = false;", loadBlock);

        var ocrLanguageBlock = GetMethodBlock(ocrCode, "private void OcrLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressOcrPreferenceChange) return;", ocrLanguageBlock);
        Assert.Contains("var previous = _settingsService.Settings.OcrLanguageTag;", ocrLanguageBlock);
        Assert.Contains("\"settings.ocr-language\"", ocrLanguageBlock);
        Assert.Contains("value => _settingsService.Settings.OcrLanguageTag = value", ocrLanguageBlock);
        Assert.Contains("value => SelectComboByTag(OcrLanguageCombo, value)", ocrLanguageBlock);
        Assert.Contains("SetOcrPreferenceStatus", ocrLanguageBlock);

        var translateFromBlock = GetMethodBlock(ocrCode, "private void TranslateFromCombo_Changed(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressOcrPreferenceChange) return;", translateFromBlock);
        Assert.Contains("var previous = _settingsService.Settings.OcrDefaultTranslateFrom;", translateFromBlock);
        Assert.Contains("var selected = TranslationService.ResolveSourceLanguage(item.Tag as string);", translateFromBlock);
        Assert.Contains("\"settings.translation-source-language\"", translateFromBlock);
        Assert.Contains("value => _settingsService.Settings.OcrDefaultTranslateFrom = value", translateFromBlock);
        Assert.Contains("value => SelectComboByTag(TranslateFromCombo, value)", translateFromBlock);
        Assert.Contains("SetTranslationPreferenceStatus", translateFromBlock);
        Assert.Contains("_ => UpdateTranslationModelUi()", translateFromBlock);

        var translateToBlock = GetMethodBlock(ocrCode, "private void TranslateToCombo_Changed(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressOcrPreferenceChange) return;", translateToBlock);
        Assert.Contains("var previous = _settingsService.Settings.OcrDefaultTranslateTo;", translateToBlock);
        Assert.Contains("\"settings.translation-target-language\"", translateToBlock);
        Assert.Contains("value => _settingsService.Settings.OcrDefaultTranslateTo = value", translateToBlock);
        Assert.Contains("value => SelectComboByTag(TranslateToCombo, value)", translateToBlock);

        var modelBlock = GetMethodBlock(ocrCode, "private void EngineRow_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)");
        Assert.Contains("var previous = _settingsService.Settings.TranslationModel;", modelBlock);
        Assert.Contains("\"settings.translation-engine\"", modelBlock);
        Assert.Contains("value => _settingsService.Settings.TranslationModel = value", modelBlock);
        Assert.Contains("SetTranslationPreferenceStatus", modelBlock);
        Assert.Contains("_ => UpdateTranslationModelUi()", modelBlock);

        var openSourceInstallBlock = GetMethodBlock(ocrCode, "private void OpenSourceLocalInstallBtn_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("BackgroundRuntimeJobService.Start(", openSourceInstallBlock);
        Assert.Contains("OpenSourceLocalTranslationJobKey", openSourceInstallBlock);
        Assert.Contains("TranslationModel.OpenSourceLocal", openSourceInstallBlock);

        var argosInstallBlock = GetMethodBlock(ocrCode, "private void ArgosInstallBtn_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("BackgroundRuntimeJobService.Start(", argosInstallBlock);
        Assert.Contains("ArgosTranslationJobKey", argosInstallBlock);
        Assert.Contains("TranslationModel.Argos", argosInstallBlock);

        var googleKeyBlock = GetMethodBlock(ocrCode, "private void GoogleApiKeyBox_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressOcrPreferenceChange) return;", googleKeyBlock);
        Assert.Contains("var previous = _settingsService.Settings.GoogleTranslateApiKey;", googleKeyBlock);
        Assert.Contains("\"settings.google-translate-api-key\"", googleKeyBlock);
        Assert.Contains("value => _settingsService.Settings.GoogleTranslateApiKey = value", googleKeyBlock);
        Assert.Contains("value => GoogleApiKeyBox.Password = value ?? \"\"", googleKeyBlock);
        Assert.Contains("TranslationService.SetGoogleApiKey(value);", googleKeyBlock);
        Assert.Contains("UpdateTranslationModelUi();", googleKeyBlock);

        var helperBlock = GetMethodBlock(ocrCode, "private void UpdateOcrPreference<T>(");
        Assert.Contains("setValue(current);", helperBlock);
        Assert.Contains("_settingsService.Save();", helperBlock);
        Assert.Contains("setStatus(string.Empty);", helperBlock);
        Assert.Contains("applyRuntime?.Invoke(current);", helperBlock);
        Assert.Contains("AppDiagnostics.LogError(diagnosticKey, ex);", helperBlock);
        Assert.Contains("setValue(previous);", helperBlock);
        Assert.Contains("AppDiagnostics.LogError($\"{diagnosticKey}-rollback\", rollbackEx);", helperBlock);
        Assert.Contains("_suppressOcrPreferenceChange = true;", helperBlock);
        Assert.Contains("restoreUi(previous);", helperBlock);
        Assert.Contains("_suppressOcrPreferenceChange = false;", helperBlock);
        Assert.Contains("applyRuntime?.Invoke(previous);", helperBlock);
        Assert.Contains("setStatus($\"{label} change was not saved. Previous setting restored.\");", helperBlock);
        Assert.Contains("ToastWindow.ShowError(", helperBlock);
        Assert.Contains("The previous OCR setting was restored. Check Settings -> OCR and try again.", helperBlock);

        var ocrStatusBlock = GetMethodBlock(ocrCode, "private void SetOcrPreferenceStatus(string message)");
        Assert.Contains("OcrPreferenceStatusText.Text = message;", ocrStatusBlock);
        Assert.Contains("Visibility.Collapsed", ocrStatusBlock);
        Assert.Contains("Visibility.Visible", ocrStatusBlock);

        var translationStatusBlock = GetMethodBlock(ocrCode, "private void SetTranslationPreferenceStatus(string message)");
        Assert.Contains("TranslationPreferenceStatusText.Text = message;", translationStatusBlock);
        Assert.Contains("Visibility.Collapsed", translationStatusBlock);
        Assert.Contains("Visibility.Visible", translationStatusBlock);
    }

    [Fact]
    public void CaptureAndSavePreferencesRollBackAndLeaveInlineStatus()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));
        var settingsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml.cs"));
        var preferencesCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Preferences.cs"));
        var recordingCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Recording.cs"));

        Assert.Contains("private bool _suppressCaptureSavePreferenceChange;", settingsCode);
        Assert.Contains("private bool _suppressHistoryPreferenceChange;", settingsCode);
        Assert.Contains("x:Name=\"CaptureSavePreferenceStatusText\"", xaml);
        AssertNamedTextBlockUsesStyle(xaml, "CaptureSavePreferenceStatusText", "SettingsStatusText");
        Assert.Contains("x:Name=\"HistoryPreferenceStatusText\"", xaml);
        AssertNamedTextBlockUsesStyle(xaml, "HistoryPreferenceStatusText", "SettingsStatusText");
        AssertNamedControlHasLabel(xaml, "ShowCursorCheck", "<CheckBox", "Show cursor in captures and recordings", "Include the pointer in saved media");
        AssertNamedControlHasLabel(xaml, "CrosshairGuidesCheck", "<CheckBox", "Show crosshair guides", "Show alignment guides while selecting");
        AssertNamedControlHasLabel(xaml, "ShowCaptureMagnifierCheck", "<CheckBox", "Show pixel magnifier while selecting", "Zoom the cursor area during selection");
        AssertNamedControlHasLabel(xaml, "OverlayAllMonitorsCheck", "<CheckBox", "Span selection overlay across all monitors", "Use one overlay across the full virtual desktop");
        AssertNamedControlHasLabel(xaml, "AnnotationStrokeShadowCheck", "<CheckBox", "Annotation stroke and shadow", "Keep annotations readable on mixed backgrounds");
        AssertNamedControlHasLabel(xaml, "SaveToFileCheck", "<CheckBox", "Save screenshots to file", "Write screenshots to the configured save folder");
        AssertNamedControlHasLabel(xaml, "AskFileNameCheck", "<CheckBox", "Ask for file name every time", "Prompt for a file name before each saved capture");
        AssertNamedControlHasLabel(xaml, "MonthlyFoldersCheck", "<CheckBox", "Create monthly subfolders", "Organize captures into year-month folders");
        AssertNamedControlHasLabel(xaml, "SaveHistoryCheck", "<CheckBox", "Save capture history", "Keep captures available in the History page");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "DefaultCaptureModeCombo", "Rectangle", "Drag from corner to corner.", "Rectangle selection", "Start captures with the standard rectangular selection tool.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "DefaultCaptureModeCombo", "Center", "Drag outward from a center point.", "Center selection", "Start captures with a center-based selection tool.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "DefaultCaptureModeCombo", "Freeform", "Draw an irregular capture outline.", "Freeform selection", "Start captures with the freeform selection tool.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CenterAspectRatioCombo", "Free", "Do not lock the center selection ratio.", "Free center ratio", "Let center selection resize freely without an aspect-ratio lock.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CenterAspectRatioCombo", "Square", "Lock center selection to a square.", "Square center ratio", "Keep center selection width and height equal.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CenterAspectRatioCombo", "16:9", "Lock center selection to widescreen landscape.", "16:9 center ratio", "Keep center selection in a 16 to 9 landscape ratio.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CenterAspectRatioCombo", "4:3", "Lock center selection to standard landscape.", "4:3 center ratio", "Keep center selection in a 4 to 3 landscape ratio.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CenterAspectRatioCombo", "3:2", "Lock center selection to photo landscape.", "3:2 center ratio", "Keep center selection in a 3 to 2 landscape ratio.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CenterAspectRatioCombo", "9:16", "Lock center selection to vertical portrait.", "9:16 center ratio", "Keep center selection in a 9 to 16 portrait ratio.");
        AssertNamedControlHasLabel(xaml, "WindowDetectionCheck", "<CheckBox", "Window detection", "Detect windows when hovering before selection.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CaptureDockSideCombo", "Top", "Show the capture toolbar along the top edge.", "Toolbar top", "Place the capture toolbar at the top of the screen.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CaptureDockSideCombo", "Bottom", "Show the capture toolbar along the bottom edge.", "Toolbar bottom", "Place the capture toolbar at the bottom of the screen.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "ScrollingCaptureModeCombo", "Automatic", "Let CyberSnap collect scrolling frames automatically.", "Automatic scrolling capture", "Automatically collect frames while scrolling capture is active.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "ScrollingCaptureModeCombo", "Autoscroll", "Automatically scroll and capture the window content.", "Autoscroll capture", "Automatically scroll the target window and stitch frames until the end is reached.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "AfterCaptureCombo", "Copy to clipboard", "Copy the capture without opening a preview.", "Copy after capture", "Copy the saved capture to the clipboard and skip the preview window.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "AfterCaptureCombo", "Preview + Copy", "Open a preview and copy the capture.", "Preview and copy after capture", "Open the preview window and also copy the saved capture to the clipboard.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "AfterCaptureCombo", "Preview only", "Open a preview without copying.", "Preview only after capture", "Open the preview window without copying the saved capture to the clipboard.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CaptureDelayCombo", "None", "Open capture immediately.", "No capture delay", "Start the capture overlay immediately after choosing capture.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CaptureDelayCombo", "3 seconds", "Wait 3 seconds before capture.", "3 second capture delay", "Wait 3 seconds before opening the capture overlay.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CaptureDelayCombo", "5 seconds", "Wait 5 seconds before capture.", "5 second capture delay", "Wait 5 seconds before opening the capture overlay.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CaptureDelayCombo", "10 seconds", "Wait 10 seconds before capture.", "10 second capture delay", "Wait 10 seconds before opening the capture overlay.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CaptureFormatCombo", "PNG", "Save lossless images with transparency.", "PNG image format", "Save captures as lossless PNG files, including transparency when available.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CaptureFormatCombo", "JPG", "Save smaller photos without transparency.", "JPG image format", "Save captures as compressed JPG files for smaller image sizes.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CaptureFormatCombo", "BMP", "Save uncompressed bitmap images.", "BMP image format", "Save captures as uncompressed BMP files for maximum compatibility.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "JpegQualityCombo", "100 - Best", "Use maximum JPG quality.", "100 JPG quality", "Save JPG captures at maximum quality with the largest file size.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "JpegQualityCombo", "90 - High", "Use high JPG quality.", "90 JPG quality", "Save JPG captures at high quality with moderate compression.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "JpegQualityCombo", "85 - Balanced", "Balance JPG quality and file size.", "85 JPG quality", "Save JPG captures with balanced quality and file size.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "JpegQualityCombo", "75 - Smaller", "Use smaller JPG files.", "75 JPG quality", "Save JPG captures with stronger compression for smaller files.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "JpegQualityCombo", "60 - Tiny", "Use the smallest JPG files.", "60 JPG quality", "Save JPG captures with heavy compression for tiny files.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CaptureSizeCombo", "Original", "Keep the original capture size.", "Original capture size", "Save captures without resizing the longest edge.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CaptureSizeCombo", "2160p", "Limit captures to 2160p.", "2160p max image size", "Resize oversized captures so the longest edge is at most 2160 pixels.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CaptureSizeCombo", "1440p", "Limit captures to 1440p.", "1440p max image size", "Resize oversized captures so the longest edge is at most 1440 pixels.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CaptureSizeCombo", "1080p", "Limit captures to 1080p.", "1080p max image size", "Resize oversized captures so the longest edge is at most 1080 pixels.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CaptureSizeCombo", "720p", "Limit captures to 720p.", "720p max image size", "Resize oversized captures so the longest edge is at most 720 pixels.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "CaptureSizeCombo", "480p", "Limit captures to 480p.", "480p max image size", "Resize oversized captures so the longest edge is at most 480 pixels.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "HistoryRetentionCombo", "Never", "Keep history until manually cleared", "Never auto-clear history", "Keep saved capture history until you clear it manually.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "HistoryRetentionCombo", "1 day", "Keep history for 1 day", "Keep history for 1 day", "Automatically remove capture history older than 1 day.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "HistoryRetentionCombo", "7 days", "Keep history for 7 days", "Keep history for 7 days", "Automatically remove capture history older than 7 days.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "HistoryRetentionCombo", "30 days", "Keep history for 30 days", "Keep history for 30 days", "Automatically remove capture history older than 30 days.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "HistoryRetentionCombo", "3 months", "Keep history for 3 months", "Keep history for 3 months", "Automatically remove capture history older than 3 months.");

        var defaultCaptureBlock = GetMethodBlock(settingsCode, "private void DefaultCaptureModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;", defaultCaptureBlock);
        Assert.Contains("var previous = _settingsService.Settings.DefaultCaptureMode;", defaultCaptureBlock);
        Assert.Contains("\"settings.default-capture-mode\"", defaultCaptureBlock);
        Assert.Contains("value => _settingsService.Settings.DefaultCaptureMode = value", defaultCaptureBlock);
        Assert.Contains("DefaultCaptureModeCombo.SelectedIndex = value switch", defaultCaptureBlock);
        Assert.Contains("notifyHotkeyChanged: true", defaultCaptureBlock);

        var afterCaptureBlock = GetMethodBlock(settingsCode, "private void AfterCaptureCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("var previous = _settingsService.Settings.AfterCapture;", afterCaptureBlock);
        Assert.Contains("\"settings.after-capture\"", afterCaptureBlock);
        Assert.Contains("AfterCaptureCombo.SelectedIndex = value switch", afterCaptureBlock);

        var aspectBlock = GetMethodBlock(settingsCode, "private void CenterAspectRatioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("var previous = _settingsService.Settings.CenterSelectionAspectRatio;", aspectBlock);
        Assert.Contains("var selectedIndex = Math.Clamp(CenterAspectRatioCombo.SelectedIndex, 0, 5);", aspectBlock);
        Assert.Contains("\"settings.center-aspect-ratio\"", aspectBlock);
        Assert.Contains("() => CenterAspectRatioCombo.SelectedIndex = selectedIndex", aspectBlock);

        var saveToFileBlock = GetMethodBlock(settingsCode, "private void SaveToFileCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("var previous = _settingsService.Settings.SaveToFile;", saveToFileBlock);
        Assert.Contains("\"settings.save-to-file\"", saveToFileBlock);
        Assert.Contains("SaveToFileCheck.IsChecked = value;", saveToFileBlock);
        Assert.Contains("SaveDirPanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;", saveToFileBlock);
        Assert.Contains("() => SaveDirPanel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed", saveToFileBlock);

        var askNameBlock = GetMethodBlock(settingsCode, "private void AskFileNameCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("var previous = _settingsService.Settings.AskForFileNameOnSave;", askNameBlock);
        Assert.Contains("\"settings.ask-file-name\"", askNameBlock);
        Assert.Contains("value => AskFileNameCheck.IsChecked = value", askNameBlock);

        var templateBlock = GetMethodBlock(settingsCode, "private void FileNameTemplateBox_TextChanged(object sender, TextChangedEventArgs e)");
        Assert.Contains("var previous = _settingsService.Settings.FileNameTemplate;", templateBlock);
        Assert.Contains("\"settings.file-name-template\"", templateBlock);
        Assert.Contains("FileNameTemplateBox.Text = value;", templateBlock);
        Assert.Contains("UpdateFileNameTemplatePreview(value);", templateBlock);
        Assert.Contains("() => UpdateFileNameTemplatePreview(template)", templateBlock);

        var monthlyFoldersBlock = GetMethodBlock(settingsCode, "private void MonthlyFoldersCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;", monthlyFoldersBlock);
        Assert.Contains("var previous = _settingsService.Settings.SaveInMonthlyFolders;", monthlyFoldersBlock);
        Assert.Contains("\"settings.monthly-folders\"", monthlyFoldersBlock);
        Assert.Contains("value => MonthlyFoldersCheck.IsChecked = value", monthlyFoldersBlock);

        var browseBlock = GetMethodBlock(settingsCode, "private void BrowseButton_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("var previous = _settingsService.Settings.SaveDirectory;", browseBlock);
        Assert.Contains("var selectedPath = dlg.SelectedPath;", browseBlock);
        Assert.Contains("\"settings.save-directory\"", browseBlock);
        Assert.Contains("value => _settingsService.Settings.SaveDirectory = value", browseBlock);
        Assert.Contains("SetSaveDirectoryPath,", browseBlock);
        Assert.Contains("() => SetSaveDirectoryPath(selectedPath)", browseBlock);

        var captureFormatBlock = GetMethodBlock(settingsCode, "private void CaptureFormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;", captureFormatBlock);
        Assert.Contains("var previous = _settingsService.Settings.CaptureImageFormat;", captureFormatBlock);
        Assert.Contains("\"settings.capture-format\"", captureFormatBlock);
        Assert.Contains("_historyService.CaptureImageFormat = value;", captureFormatBlock);
        Assert.Contains("UpdateCaptureFormatControls();", captureFormatBlock);
        Assert.Contains("_historyService.CaptureImageFormat = selected;", captureFormatBlock);

        var jpegQualityBlock = GetMethodBlock(settingsCode, "private void JpegQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;", jpegQualityBlock);
        Assert.Contains("var previous = _settingsService.Settings.JpegQuality;", jpegQualityBlock);
        Assert.Contains("var selectedIndex = JpegQualityCombo.SelectedIndex;", jpegQualityBlock);
        Assert.Contains("\"settings.jpeg-quality\"", jpegQualityBlock);
        Assert.Contains("_historyService.JpegQuality = value;", jpegQualityBlock);
        Assert.Contains("_historyService.JpegQuality = quality;", jpegQualityBlock);

        var captureSizeBlock = GetMethodBlock(settingsCode, "private void CaptureSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;", captureSizeBlock);
        Assert.Contains("var previous = _settingsService.Settings.CaptureMaxLongEdge;", captureSizeBlock);
        Assert.Contains("\"settings.capture-size\"", captureSizeBlock);
        Assert.Contains("value => CaptureSizeCombo.SelectedIndex = value switch", captureSizeBlock);
        Assert.Contains("() => CaptureSizeCombo.SelectedIndex = selectedIndex", captureSizeBlock);

        var dockBlock = GetMethodBlock(preferencesCode, "private void CaptureDockSideCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;", dockBlock);
        Assert.Contains("var previous = _settingsService.Settings.CaptureDockSide;", dockBlock);
        Assert.Contains("\"settings.capture-dock-side\"", dockBlock);
        Assert.Contains("value => _settingsService.Settings.CaptureDockSide = value", dockBlock);
        Assert.Contains("value => CaptureDockSideCombo.SelectedIndex = (int)value", dockBlock);

        var scrollingBlock = GetMethodBlock(preferencesCode, "private void ScrollingCaptureModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;", scrollingBlock);
        Assert.Contains("var previous = _settingsService.Settings.ScrollingCaptureMode;", scrollingBlock);
        Assert.Contains("\"settings.scrolling-capture-mode\"", scrollingBlock);
        Assert.Contains("value => _settingsService.Settings.ScrollingCaptureMode = value", scrollingBlock);
        Assert.Contains("value => ScrollingCaptureModeCombo.SelectedIndex = value == ScrollingCaptureMode.AssistAutoscroll ? 1 : 0", scrollingBlock);

        var windowDetectionBlock = GetMethodBlock(preferencesCode, "private void WindowDetectionCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;", windowDetectionBlock);
        Assert.Contains("var previous = (Mode: _settingsService.Settings.WindowDetection, DetectWindows: _settingsService.Settings.DetectWindows);", windowDetectionBlock);
        Assert.Contains("\"settings.window-detection\"", windowDetectionBlock);
        Assert.Contains("_settingsService.Settings.WindowDetection = value.Mode;", windowDetectionBlock);
        Assert.Contains("_settingsService.Settings.DetectWindows = value.DetectWindows;", windowDetectionBlock);
        Assert.Contains("value => WindowDetectionCheck.IsChecked = value.DetectWindows", windowDetectionBlock);

        var delayBlock = GetMethodBlock(preferencesCode, "private void CaptureDelayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;", delayBlock);
        Assert.Contains("var previous = _settingsService.Settings.CaptureDelaySeconds;", delayBlock);
        Assert.Contains("\"settings.capture-delay\"", delayBlock);
        Assert.Contains("value => _settingsService.Settings.CaptureDelaySeconds = value", delayBlock);
        Assert.Contains("value => CaptureDelayCombo.SelectedIndex = value switch { 3 => 1, 5 => 2, 10 => 3, _ => 0 }", delayBlock);

        var crosshairBlock = GetMethodBlock(preferencesCode, "private void CrosshairGuidesCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;", crosshairBlock);
        Assert.Contains("var previous = _settingsService.Settings.ShowCrosshairGuides;", crosshairBlock);
        Assert.Contains("\"settings.crosshair-guides\"", crosshairBlock);
        Assert.Contains("value => _settingsService.Settings.ShowCrosshairGuides = value", crosshairBlock);
        Assert.Contains("value => CrosshairGuidesCheck.IsChecked = value", crosshairBlock);

        var magnifierBlock = GetMethodBlock(preferencesCode, "private void ShowCaptureMagnifierCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;", magnifierBlock);
        Assert.Contains("var previous = _settingsService.Settings.ShowCaptureMagnifier;", magnifierBlock);
        Assert.Contains("\"settings.capture-magnifier\"", magnifierBlock);
        Assert.Contains("value => _settingsService.Settings.ShowCaptureMagnifier = value", magnifierBlock);
        Assert.Contains("value => ShowCaptureMagnifierCheck.IsChecked = value", magnifierBlock);

        var overlayBlock = GetMethodBlock(preferencesCode, "private void OverlayAllMonitorsCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;", overlayBlock);
        Assert.Contains("var previous = _settingsService.Settings.OverlayCaptureAllMonitors;", overlayBlock);
        Assert.Contains("\"settings.overlay-all-monitors\"", overlayBlock);
        Assert.Contains("value => _settingsService.Settings.OverlayCaptureAllMonitors = value", overlayBlock);
        Assert.Contains("value => OverlayAllMonitorsCheck.IsChecked = value", overlayBlock);

        var showCursorBlock = GetMethodBlock(preferencesCode, "private void ShowCursorCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;", showCursorBlock);
        Assert.Contains("var previous = _settingsService.Settings.ShowCursor;", showCursorBlock);
        Assert.Contains("\"settings.show-cursor\"", showCursorBlock);
        Assert.Contains("value => _settingsService.Settings.ShowCursor = value", showCursorBlock);
        Assert.Contains("ShowCursorCheck.IsChecked = value;", showCursorBlock);
        Assert.Contains("RecordShowCursorCheck.IsChecked = value;", showCursorBlock);
        Assert.Contains("if (RecordShowCursorCheck.IsChecked != selected)", showCursorBlock);

        var annotationContrastBlock = GetMethodBlock(preferencesCode, "private void AnnotationStrokeShadowCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;", annotationContrastBlock);
        Assert.Contains("var previous = _settingsService.Settings.AnnotationStrokeShadow;", annotationContrastBlock);
        Assert.Contains("\"settings.annotation-stroke-shadow\"", annotationContrastBlock);
        Assert.Contains("value => _settingsService.Settings.AnnotationStrokeShadow = value", annotationContrastBlock);
        Assert.Contains("value => AnnotationStrokeShadowCheck.IsChecked = value", annotationContrastBlock);

        var recordShowCursorBlock = GetMethodBlock(recordingCode, "private void RecordShowCursorCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressCaptureSavePreferenceChange) return;", recordShowCursorBlock);
        Assert.Contains("var previous = _settingsService.Settings.ShowCursor;", recordShowCursorBlock);
        Assert.Contains("\"settings.record-show-cursor\"", recordShowCursorBlock);
        Assert.Contains("value => _settingsService.Settings.ShowCursor = value", recordShowCursorBlock);
        Assert.Contains("RecordShowCursorCheck.IsChecked = value;", recordShowCursorBlock);
        Assert.Contains("ShowCursorCheck.IsChecked = value;", recordShowCursorBlock);
        Assert.Contains("if (ShowCursorCheck.IsChecked != selected)", recordShowCursorBlock);

        var saveHistoryBlock = GetMethodBlock(recordingCode, "private void SaveHistoryCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressHistoryPreferenceChange) return;", saveHistoryBlock);
        Assert.Contains("var previous = _settingsService.Settings.SaveHistory;", saveHistoryBlock);
        Assert.Contains("\"settings.save-history\"", saveHistoryBlock);
        Assert.Contains("value => _settingsService.Settings.SaveHistory = value", saveHistoryBlock);
        Assert.Contains("value => SaveHistoryCheck.IsChecked = value", saveHistoryBlock);

        var retentionBlock = GetMethodBlock(recordingCode, "private void HistoryRetentionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressHistoryPreferenceChange) return;", retentionBlock);
        Assert.Contains("var previous = _settingsService.Settings.HistoryRetention;", retentionBlock);
        Assert.Contains("var selected = (HistoryRetentionPeriod)Math.Clamp(HistoryRetentionCombo.SelectedIndex, 0, 4);", retentionBlock);
        Assert.Contains("\"settings.history-retention\"", retentionBlock);
        Assert.Contains("value => _settingsService.Settings.HistoryRetention = value", retentionBlock);
        Assert.Contains("HistoryRetentionCombo.SelectedIndex = (int)value;", retentionBlock);
        Assert.Contains("_historyService.RetentionPeriod = value;", retentionBlock);
        Assert.Contains("value => _historyService.PruneByRetention(value)", retentionBlock);

        var historyHelperBlock = GetMethodBlock(recordingCode, "private void UpdateHistoryPreference<T>(");
        Assert.Contains("setValue(current);", historyHelperBlock);
        Assert.Contains("_settingsService.Save();", historyHelperBlock);
        Assert.Contains("applySuccess?.Invoke(current);", historyHelperBlock);
        Assert.Contains("SetHistoryPreferenceStatus(string.Empty);", historyHelperBlock);
        Assert.Contains("AppDiagnostics.LogError(diagnosticKey, ex);", historyHelperBlock);
        Assert.Contains("setValue(previous);", historyHelperBlock);
        Assert.Contains("AppDiagnostics.LogError($\"{diagnosticKey}-rollback\", rollbackEx);", historyHelperBlock);
        Assert.Contains("_suppressHistoryPreferenceChange = true;", historyHelperBlock);
        Assert.Contains("restoreUi(previous);", historyHelperBlock);
        Assert.Contains("_suppressHistoryPreferenceChange = false;", historyHelperBlock);
        Assert.Contains("SetHistoryPreferenceStatus($\"{label} change was not saved. Previous setting restored.\");", historyHelperBlock);
        Assert.Contains("ToastWindow.ShowError(", historyHelperBlock);
        Assert.Contains("The previous history setting was restored. Check Settings -> Recording and try again.", historyHelperBlock);

        var historyStatusBlock = GetMethodBlock(recordingCode, "private void SetHistoryPreferenceStatus(string message)");
        Assert.Contains("HistoryPreferenceStatusText.Text = message;", historyStatusBlock);
        Assert.Contains("Visibility.Collapsed", historyStatusBlock);
        Assert.Contains("Visibility.Visible", historyStatusBlock);

        var helperBlock = GetMethodBlock(settingsCode, "private void UpdateCaptureSavePreference<T>(");
        Assert.Contains("setValue(current);", helperBlock);
        Assert.Contains("_suppressCaptureSavePreferenceChange = true;", helperBlock);
        Assert.Contains("applyCurrentUi();", helperBlock);
        Assert.Contains("_settingsService.Save();", helperBlock);
        Assert.Contains("SetCaptureSavePreferenceStatus(string.Empty);", helperBlock);
        Assert.Contains("HotkeyChanged?.Invoke();", helperBlock);
        Assert.Contains("AppDiagnostics.LogError(diagnosticKey, ex);", helperBlock);
        Assert.Contains("setValue(previous);", helperBlock);
        Assert.Contains("AppDiagnostics.LogError($\"{diagnosticKey}-rollback\", rollbackEx);", helperBlock);
        Assert.Contains("restoreUi(previous);", helperBlock);
        Assert.Contains("ShowCaptureSavePreferenceFailed(label, ex);", helperBlock);

        var failureBlock = GetMethodBlock(settingsCode, "private void ShowCaptureSavePreferenceFailed(string label, Exception ex)");
        Assert.Contains("SetCaptureSavePreferenceStatus($\"{label} change was not saved. Previous setting restored.\");", failureBlock);
        Assert.Contains("ToastWindow.ShowError(", failureBlock);
        Assert.Contains("$\"{label} failed\"", failureBlock);
        Assert.Contains("The previous capture setting was restored. Check Settings -> Capture and try again.", failureBlock);

        var statusBlock = GetMethodBlock(settingsCode, "private void SetCaptureSavePreferenceStatus(string message)");
        Assert.Contains("CaptureSavePreferenceStatusText.Text = message;", statusBlock);
        Assert.Contains("Visibility.Collapsed", statusBlock);
        Assert.Contains("Visibility.Visible", statusBlock);
    }

    [Fact]
    public void GeneralPreferencesRollBackAndLeaveInlineStatus()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));
        var settingsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml.cs"));
        var preferencesCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Preferences.cs"));
        var appearanceCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Appearance.cs"));
        var recordingCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Recording.cs"));

        Assert.Contains("private bool _suppressGeneralPreferenceChange;", settingsCode);
        Assert.Contains("x:Name=\"GeneralPreferenceStatusText\"", xaml);
        AssertNamedTextBlockUsesStyle(xaml, "GeneralPreferenceStatusText", "SettingsStatusText");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "UiScaleCombo", "80%", "Scale CyberSnap UI to 80%.", "80 percent UI scale", "Make CyberSnap windows, toasts, and capture controls smaller.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "UiScaleCombo", "90%", "Scale CyberSnap UI to 90%.", "90 percent UI scale", "Make CyberSnap windows, toasts, and capture controls slightly smaller.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "UiScaleCombo", "100%", "Use normal CyberSnap UI scale.", "100 percent UI scale", "Use the default CyberSnap window, toast, and capture-control size.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "UiScaleCombo", "110%", "Scale CyberSnap UI to 110%.", "110 percent UI scale", "Make CyberSnap windows, toasts, and capture controls slightly larger.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "UiScaleCombo", "120%", "Scale CyberSnap UI to 120%.", "120 percent UI scale", "Make CyberSnap windows, toasts, and capture controls larger.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "UiScaleCombo", "130%", "Scale CyberSnap UI to 130%.", "130 percent UI scale", "Make CyberSnap windows, toasts, and capture controls much larger.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "UiScaleCombo", "140%", "Scale CyberSnap UI to 140%.", "140 percent UI scale", "Make CyberSnap windows, toasts, and capture controls extra large.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "SoundPackCombo", "Default", "Use the default notification sounds.", "Default sound pack", "Use CyberSnap's standard notification sounds.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "SoundPackCombo", "Soft", "Use quieter notification sounds.", "Soft sound pack", "Use softer notification sounds for captures and previews.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "SoundPackCombo", "Retro", "Use retro notification sounds.", "Retro sound pack", "Use retro-style notification sounds for captures and previews.");
        Assert.Contains("AutomationProperties.SetName(autoLanguageItem, \"Auto interface language\");", appearanceCode);
        Assert.Contains("AutomationProperties.SetHelpText(autoLanguageItem, \"Use the Windows language when CyberSnap has app translations for it.\");", appearanceCode);
        Assert.Contains("ToolTip = available", appearanceCode);
        Assert.Contains("? $\"Use {label} for the CyberSnap interface.\"", appearanceCode);
        Assert.Contains("AutomationProperties.SetName(item, $\"{label} interface language\");", appearanceCode);
        Assert.Contains("? $\"Use {label} for CyberSnap menus, settings, and prompts.\"", appearanceCode);

        var uiScaleBlock = GetMethodBlock(preferencesCode, "private void UiScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressGeneralPreferenceChange) return;", uiScaleBlock);
        Assert.Contains("var previous = _settingsService.Settings.UiScale;", uiScaleBlock);
        Assert.Contains("\"settings.ui-scale\"", uiScaleBlock);
        Assert.Contains("value => _settingsService.Settings.UiScale = value", uiScaleBlock);
        Assert.Contains("SelectUiScale", uiScaleBlock);
        Assert.Contains("UiScale.Set(value);", uiScaleBlock);
        Assert.Contains("ApplyThemeColors();", uiScaleBlock);

        var badgesBlock = GetMethodBlock(appearanceCode, "private void ShowToolNumberBadgesCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressGeneralPreferenceChange) return;", badgesBlock);
        Assert.Contains("var previous = _settingsService.Settings.ShowToolNumberBadges;", badgesBlock);
        Assert.Contains("\"settings.tool-number-badges\"", badgesBlock);
        Assert.Contains("value => _settingsService.Settings.ShowToolNumberBadges = value", badgesBlock);
        Assert.Contains("value => ShowToolNumberBadgesCheck.IsChecked = value", badgesBlock);

        var languageBlock = GetMethodBlock(appearanceCode, "private void InterfaceLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressGeneralPreferenceChange) return;", languageBlock);
        Assert.Contains("var previous = _settingsService.Settings.InterfaceLanguage;", languageBlock);
        Assert.Contains("var normalized = LocalizationService.NormalizeLanguageSetting(languageCode);", languageBlock);
        Assert.Contains("\"settings.interface-language\"", languageBlock);
        Assert.Contains("value => _settingsService.Settings.InterfaceLanguage = value", languageBlock);
        Assert.Contains("SelectInterfaceLanguage", languageBlock);
        Assert.Contains("ApplyLocalization();", languageBlock);
        Assert.Contains("LocalizationChanged?.Invoke();", languageBlock);

        var muteBlock = GetMethodBlock(recordingCode, "private void MuteSoundsCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressGeneralPreferenceChange) return;", muteBlock);
        Assert.Contains("var previous = _settingsService.Settings.MuteSounds;", muteBlock);
        Assert.Contains("\"settings.mute-sounds\"", muteBlock);
        Assert.Contains("value => _settingsService.Settings.MuteSounds = value", muteBlock);
        Assert.Contains("value => MuteSoundsCheck.IsChecked = value", muteBlock);
        Assert.Contains("value => SoundService.Muted = value", muteBlock);

        var animationsBlock = GetMethodBlock(recordingCode, "private void DisableAnimationsCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressGeneralPreferenceChange) return;", animationsBlock);
        Assert.Contains("var previous = _settingsService.Settings.DisableAnimations;", animationsBlock);
        Assert.Contains("\"settings.disable-animations\"", animationsBlock);
        Assert.Contains("value => _settingsService.Settings.DisableAnimations = value", animationsBlock);
        Assert.Contains("value => DisableAnimationsCheck.IsChecked = value", animationsBlock);
        Assert.Contains("value => Motion.Disabled = value", animationsBlock);

        var soundPackBlock = GetMethodBlock(recordingCode, "private void SoundPackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressGeneralPreferenceChange) return;", soundPackBlock);
        Assert.Contains("var previous = _settingsService.Settings.SoundPack;", soundPackBlock);
        Assert.Contains("var selected = (SoundPack)Math.Clamp(SoundPackCombo.SelectedIndex, 0, 2);", soundPackBlock);
        Assert.Contains("\"settings.sound-pack\"", soundPackBlock);
        Assert.Contains("value => _settingsService.Settings.SoundPack = value", soundPackBlock);
        Assert.Contains("value => SoundPackCombo.SelectedIndex = (int)value", soundPackBlock);
        Assert.Contains("SoundService.SetPack(value);", soundPackBlock);
        Assert.Contains("SoundService.PlayCaptureSound();", soundPackBlock);

        var searchBarBlock = GetMethodBlock(preferencesCode, "private void ShowImageSearchBarCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressGeneralPreferenceChange) return;", searchBarBlock);
        Assert.Contains("var previous = _settingsService.Settings.ShowImageSearchBar;", searchBarBlock);
        Assert.Contains("\"settings.show-image-search-bar\"", searchBarBlock);
        Assert.Contains("value => _settingsService.Settings.ShowImageSearchBar = value", searchBarBlock);
        Assert.Contains("value => ShowImageSearchBarCheck.IsChecked = value", searchBarBlock);
        Assert.Contains("((App)Application.Current).RefreshHistoryWindowIfOpen();", searchBarBlock);

        var searchDiagnosticsBlock = GetMethodBlock(preferencesCode, "private void ShowImageSearchDiagnosticsCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressGeneralPreferenceChange) return;", searchDiagnosticsBlock);
        Assert.Contains("var previous = _settingsService.Settings.ShowImageSearchDiagnostics;", searchDiagnosticsBlock);
        Assert.Contains("\"settings.show-image-search-diagnostics\"", searchDiagnosticsBlock);
        Assert.Contains("value => _settingsService.Settings.ShowImageSearchDiagnostics = value", searchDiagnosticsBlock);
        Assert.Contains("value => ShowImageSearchDiagnosticsCheck.IsChecked = value", searchDiagnosticsBlock);
        Assert.Contains("((App)Application.Current).RefreshHistoryWindowIfOpen();", searchDiagnosticsBlock);

        var helperBlock = GetMethodBlock(preferencesCode, "private void UpdateGeneralPreference<T>(");
        Assert.Contains("setValue(current);", helperBlock);
        Assert.Contains("_settingsService.Save();", helperBlock);
        Assert.Contains("SetGeneralPreferenceStatus(string.Empty);", helperBlock);
        Assert.Contains("applyRuntime?.Invoke(current);", helperBlock);
        Assert.Contains("AppDiagnostics.LogError(diagnosticKey, ex);", helperBlock);
        Assert.Contains("setValue(previous);", helperBlock);
        Assert.Contains("AppDiagnostics.LogError($\"{diagnosticKey}-rollback\", rollbackEx);", helperBlock);
        Assert.Contains("_suppressGeneralPreferenceChange = true;", helperBlock);
        Assert.Contains("restoreUi(previous);", helperBlock);
        Assert.Contains("_suppressGeneralPreferenceChange = false;", helperBlock);
        Assert.Contains("applyRuntime?.Invoke(previous);", helperBlock);
        Assert.Contains("SetGeneralPreferenceStatus($\"{label} change was not saved. Previous setting restored.\");", helperBlock);
        Assert.Contains("ToastWindow.ShowError(", helperBlock);
        Assert.Contains("The previous general setting was restored. Check Settings -> General and try again.", helperBlock);

        var statusBlock = GetMethodBlock(preferencesCode, "private void SetGeneralPreferenceStatus(string message)");
        Assert.Contains("GeneralPreferenceStatusText.Text = message;", statusBlock);
        Assert.Contains("Visibility.Collapsed", statusBlock);
        Assert.Contains("Visibility.Visible", statusBlock);
    }

    [Fact]
    public void RecordingPreferencesRollBackAndLeaveInlineStatus()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));
        var settingsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml.cs"));
        var recordingCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Recording.cs"));

        Assert.Contains("private bool _suppressRecordingPreferenceChange;", settingsCode);
        Assert.Contains("x:Name=\"RecordingPreferenceStatusText\"", xaml);
        AssertNamedTextBlockUsesStyle(xaml, "RecordingPreferenceStatusText", "SettingsStatusText");
        AssertNamedControlHasLabel(xaml, "RecordShowCursorCheck", "<CheckBox", "Show cursor in recordings", "Include pointer movement in recorded output.");
        AssertNamedControlHasLabel(xaml, "RecordMicCheck", "<CheckBox", "Record microphone", "Capture audio from the selected input device.");
        AssertNamedControlHasLabel(xaml, "RecordDesktopAudioCheck", "<CheckBox", "Record desktop audio", "Capture system audio from the selected output device.");
        AssertNamedControlHasLabel(xaml, "RecordingFormatCombo", "<ComboBox", "Recording format", "Choose the video container for recordings");
        AssertNamedControlHasLabel(xaml, "RecordingQualityCombo", "<ComboBox", "Recording quality", "Set maximum recording resolution");
        AssertNamedControlHasLabel(xaml, "RecordingFpsCombo", "<ComboBox", "Recording FPS", "Choose how many frames are captured each second");
        AssertNamedControlHasLabel(xaml, "MicDeviceCombo", "<ComboBox", "Microphone input device", "Choose the microphone input device");
        AssertNamedControlHasLabel(xaml, "DesktopAudioDeviceCombo", "<ComboBox", "Desktop audio output device", "Choose the desktop audio output device");
        Assert.Contains("CreateAudioDeviceItem(", recordingCode);
        Assert.Contains("Microphone device {mic.Name}", recordingCode);
        Assert.Contains("Use {mic.Name} for microphone recording.", recordingCode);
        Assert.Contains("Desktop audio device {dev.Name}", recordingCode);
        Assert.Contains("Use {dev.Name} for desktop audio recording.", recordingCode);
        AssertComboBoxItemInNamedComboHasLabel(xaml, "RecordingFormatCombo", "GIF", "Save recordings as GIF animations", "GIF recording format", "Save recordings as animated GIF files.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "RecordingFormatCombo", "MP4", "Save recordings as MP4 videos", "MP4 recording format", "Save recordings as MP4 video files.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "RecordingFormatCombo", "WebM", "Save recordings as WebM videos", "WebM recording format", "Save recordings as WebM video files.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "RecordingFormatCombo", "MKV", "Save recordings as MKV videos", "MKV recording format", "Save recordings as MKV video files.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "RecordingQualityCombo", "Original", "Keep the original recording resolution", "Original recording resolution", "Record at the selected area's original resolution.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "RecordingQualityCombo", "1080p", "Limit recordings to 1080p", "1080p recording resolution", "Scale recordings down to 1080p when needed.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "RecordingQualityCombo", "720p", "Limit recordings to 720p", "720p recording resolution", "Scale recordings down to 720p when needed.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "RecordingQualityCombo", "480p", "Limit recordings to 480p", "480p recording resolution", "Scale recordings down to 480p when needed.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "RecordingFpsCombo", "15", "Capture 15 frames per second", "15 FPS", "Capture smoother-than-low-power recordings at 15 frames per second.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "RecordingFpsCombo", "24", "Capture 24 frames per second", "24 FPS", "Capture video-like motion at 24 frames per second.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "RecordingFpsCombo", "30", "Capture 30 frames per second", "30 FPS", "Capture smooth recordings at 30 frames per second.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "RecordingFpsCombo", "60", "Capture 60 frames per second", "60 FPS", "Capture very smooth recordings at 60 frames per second.");

        var formatBlock = GetMethodBlock(recordingCode, "private void RecordingFormatCombo_Changed(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressRecordingPreferenceChange) return;", formatBlock);
        Assert.Contains("var previous = _settingsService.Settings.RecordingFormat;", formatBlock);
        Assert.Contains("var selected = (RecordingFormat)Math.Clamp(RecordingFormatCombo.SelectedIndex, 0, 3);", formatBlock);
        Assert.Contains("\"settings.recording-format\"", formatBlock);
        Assert.Contains("value => _settingsService.Settings.RecordingFormat = value", formatBlock);
        Assert.Contains("RecordingFormatCombo.SelectedIndex = (int)value;", formatBlock);
        Assert.Contains("SelectRecordingFps(value == RecordingFormat.GIF", formatBlock);
        Assert.Contains("UpdateRecordingFormatVisibility();", formatBlock);

        var qualityBlock = GetMethodBlock(recordingCode, "private void RecordingQualityCombo_Changed(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressRecordingPreferenceChange) return;", qualityBlock);
        Assert.Contains("var previous = _settingsService.Settings.RecordingQuality;", qualityBlock);
        Assert.Contains("\"settings.recording-quality\"", qualityBlock);
        Assert.Contains("value => _settingsService.Settings.RecordingQuality = value", qualityBlock);
        Assert.Contains("value => RecordingQualityCombo.SelectedIndex = (int)value", qualityBlock);

        var fpsBlock = GetMethodBlock(recordingCode, "private void RecordingFpsCombo_Changed(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressRecordingPreferenceChange) return;", fpsBlock);
        Assert.Contains("var isGif = _settingsService.Settings.RecordingFormat == RecordingFormat.GIF;", fpsBlock);
        Assert.Contains("var previous = isGif ? _settingsService.Settings.GifFps : _settingsService.Settings.RecordingFps;", fpsBlock);
        Assert.Contains("\"settings.recording-fps\"", fpsBlock);
        Assert.Contains("_settingsService.Settings.GifFps = value;", fpsBlock);
        Assert.Contains("_settingsService.Settings.RecordingFps = value;", fpsBlock);
        Assert.Contains("SelectRecordingFps", fpsBlock);

        var audioDeviceItemBlock = GetMethodBlock(recordingCode, "private static ComboBoxItem CreateAudioDeviceItem(string name, string id, string automationName, string helpText)");
        Assert.Contains("ToolTip = helpText", audioDeviceItemBlock);
        Assert.Contains("AutomationProperties.SetName(item, automationName);", audioDeviceItemBlock);
        Assert.Contains("AutomationProperties.SetHelpText(item, helpText);", audioDeviceItemBlock);

        var micBlock = GetMethodBlock(recordingCode, "private void RecordMicCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressRecordingPreferenceChange) return;", micBlock);
        Assert.Contains("var previous = _settingsService.Settings.RecordMicrophone;", micBlock);
        Assert.Contains("\"settings.record-microphone\"", micBlock);
        Assert.Contains("value => _settingsService.Settings.RecordMicrophone = value", micBlock);
        Assert.Contains("value => RecordMicCheck.IsChecked = value", micBlock);

        var desktopAudioBlock = GetMethodBlock(recordingCode, "private void RecordDesktopAudioCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressRecordingPreferenceChange) return;", desktopAudioBlock);
        Assert.Contains("var previous = _settingsService.Settings.RecordDesktopAudio;", desktopAudioBlock);
        Assert.Contains("\"settings.record-desktop-audio\"", desktopAudioBlock);
        Assert.Contains("value => _settingsService.Settings.RecordDesktopAudio = value", desktopAudioBlock);
        Assert.Contains("value => RecordDesktopAudioCheck.IsChecked = value", desktopAudioBlock);

        var micDeviceBlock = GetMethodBlock(recordingCode, "private void MicDeviceCombo_Changed(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressRecordingPreferenceChange) return;", micDeviceBlock);
        Assert.Contains("var previous = _settingsService.Settings.MicrophoneDeviceId;", micDeviceBlock);
        Assert.Contains("var selected = item.Tag as string;", micDeviceBlock);
        Assert.Contains("\"settings.microphone-device\"", micDeviceBlock);
        Assert.Contains("value => _settingsService.Settings.MicrophoneDeviceId = value", micDeviceBlock);
        Assert.Contains("SelectMicDeviceById", micDeviceBlock);

        var desktopDeviceBlock = GetMethodBlock(recordingCode, "private void DesktopAudioDeviceCombo_Changed(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressRecordingPreferenceChange) return;", desktopDeviceBlock);
        Assert.Contains("var previous = _settingsService.Settings.DesktopAudioDeviceId;", desktopDeviceBlock);
        Assert.Contains("var selected = item.Tag as string;", desktopDeviceBlock);
        Assert.Contains("\"settings.desktop-audio-device\"", desktopDeviceBlock);
        Assert.Contains("value => _settingsService.Settings.DesktopAudioDeviceId = value", desktopDeviceBlock);
        Assert.Contains("SelectDesktopAudioDeviceById", desktopDeviceBlock);

        var helperBlock = GetMethodBlock(recordingCode, "private void UpdateRecordingPreference<T>(");
        Assert.Contains("setValue(current);", helperBlock);
        Assert.Contains("_settingsService.Save();", helperBlock);
        Assert.Contains("SetRecordingPreferenceStatus(string.Empty);", helperBlock);
        Assert.Contains("if (applySuccessUi != null)", helperBlock);
        Assert.Contains("_suppressRecordingPreferenceChange = true;", helperBlock);
        Assert.Contains("applySuccessUi(current);", helperBlock);
        Assert.Contains("_suppressRecordingPreferenceChange = false;", helperBlock);
        Assert.Contains("AppDiagnostics.LogError(diagnosticKey, ex);", helperBlock);
        Assert.Contains("setValue(previous);", helperBlock);
        Assert.Contains("AppDiagnostics.LogError($\"{diagnosticKey}-rollback\", rollbackEx);", helperBlock);
        Assert.Contains("restoreUi(previous);", helperBlock);
        Assert.Contains("SetRecordingPreferenceStatus($\"{label} change was not saved. Previous setting restored.\");", helperBlock);
        Assert.Contains("ToastWindow.ShowError(", helperBlock);
        Assert.Contains("The previous recording setting was restored. Check Settings -> Recording and try again.", helperBlock);

        var statusBlock = GetMethodBlock(recordingCode, "private void SetRecordingPreferenceStatus(string message)");
        Assert.Contains("RecordingPreferenceStatusText.Text = message;", statusBlock);
        Assert.Contains("Visibility.Collapsed", statusBlock);
        Assert.Contains("Visibility.Visible", statusBlock);

        var selectByTagBlock = GetMethodBlock(recordingCode, "private static void SelectComboItemByTag(System.Windows.Controls.ComboBox comboBox, string? tag)");
        Assert.Contains("comboBox.SelectedItem = item;", selectByTagBlock);
        Assert.Contains("comboBox.SelectedIndex = 0;", selectByTagBlock);
    }

    [Fact]
    public void ToastPreferencesRollBackAndLeaveInlineStatus()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml"));
        var settingsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml.cs"));
        var preferencesCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Preferences.cs"));

        Assert.Contains("private bool _suppressToastPreferenceChange;", settingsCode);
        Assert.Contains("x:Name=\"ToastPreferenceStatusText\"", xaml);
        AssertNamedTextBlockUsesStyle(xaml, "ToastPreferenceStatusText", "SettingsStatusText");
        AssertDynamicStatusTextBlock(xaml, "ToastPreferenceStatusText", "Toast preference status", isLive: true);
        var toastStatusTag = GetOpeningTag(xaml, xaml.IndexOf("x:Name=\"ToastPreferenceStatusText\"", StringComparison.Ordinal), "<TextBlock");
        Assert.Contains("AutomationProperties.HelpText=\"{Binding Text, RelativeSource={RelativeSource Self}}\"", toastStatusTag);
        AssertNamedControlHasLabel(xaml, "ToastSlotTopLeft", "<Border", "top-left toast slot", "Move selected toast button to top-left", "Press Enter or Space to place the selected toast button here.");
        AssertNamedControlHasLabel(xaml, "ToastSlotTopInnerLeft", "<Border", "top inner-left toast slot", "Move selected toast button to top inner-left", "Press Enter or Space to place the selected toast button here.");
        AssertNamedControlHasLabel(xaml, "ToastSlotTopInnerRight", "<Border", "top inner-right toast slot", "Move selected toast button to top inner-right", "Press Enter or Space to place the selected toast button here.");
        AssertNamedControlHasLabel(xaml, "ToastSlotTopRight", "<Border", "top-right toast slot", "Move selected toast button to top-right", "Press Enter or Space to place the selected toast button here.");
        AssertNamedControlHasLabel(xaml, "ToastSlotBottomLeft", "<Border", "bottom-left toast slot", "Move selected toast button to bottom-left", "Press Enter or Space to place the selected toast button here.");
        AssertNamedControlHasLabel(xaml, "ToastSlotBottomInnerLeft", "<Border", "bottom inner-left toast slot", "Move selected toast button to bottom inner-left", "Press Enter or Space to place the selected toast button here.");
        AssertNamedControlHasLabel(xaml, "ToastSlotBottomInnerRight", "<Border", "bottom inner-right toast slot", "Move selected toast button to bottom inner-right", "Press Enter or Space to place the selected toast button here.");
        AssertNamedControlHasLabel(xaml, "ToastSlotBottomRight", "<Border", "bottom-right toast slot", "Move selected toast button to bottom-right", "Press Enter or Space to place the selected toast button here.");
        AssertNamedControlHasLabel(xaml, "ToastLayoutCloseBtn", "<Border", "Close toast button", "Move the close toast button");
        AssertNamedControlHasLabel(xaml, "ToastLayoutPinBtn", "<Border", "Pin toast button", "Move the pin toast button");
        AssertNamedControlHasLabel(xaml, "ToastLayoutSaveBtn", "<Border", "Save toast button", "Move the save toast button");
        AssertNamedControlHasLabel(xaml, "ToastLayoutOfficeBtn", "<Border", "Office export toast button", "Move the office export toast button");
        AssertNamedControlHasLabel(xaml, "ToastLayoutAiRedirectBtn", "<Border", "AI redirect toast button", "Move the AI redirect toast button");
        AssertNamedControlHasLabel(xaml, "ToastLayoutDeleteBtn", "<Border", "Delete toast button", "Move the delete toast button");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "ToastPositionCombo", "Right", "Show previews near the right edge.", "Right toast position", "Place screenshot previews near the right edge of the screen.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "ToastPositionCombo", "Left", "Show previews near the left edge.", "Left toast position", "Place screenshot previews near the left edge of the screen.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "ToastPositionCombo", "Top Left", "Show previews in the top-left corner.", "Top-left toast position", "Place screenshot previews in the top-left corner of the screen.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "ToastPositionCombo", "Top Right", "Show previews in the top-right corner.", "Top-right toast position", "Place screenshot previews in the top-right corner of the screen.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "ToastDurationCombo", "1.5 seconds", "Hide previews after 1.5 seconds.", "1.5 second toast duration", "Keep screenshot previews visible for 1.5 seconds.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "ToastDurationCombo", "2 seconds", "Hide previews after 2 seconds.", "2 second toast duration", "Keep screenshot previews visible for 2 seconds.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "ToastDurationCombo", "2.5 seconds", "Hide previews after 2.5 seconds.", "2.5 second toast duration", "Keep screenshot previews visible for 2.5 seconds.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "ToastDurationCombo", "3 seconds", "Hide previews after 3 seconds.", "3 second toast duration", "Keep screenshot previews visible for 3 seconds.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "ToastDurationCombo", "4 seconds", "Hide previews after 4 seconds.", "4 second toast duration", "Keep screenshot previews visible for 4 seconds.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "ToastDurationCombo", "5 seconds", "Hide previews after 5 seconds.", "5 second toast duration", "Keep screenshot previews visible for 5 seconds.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "ToastFadeDurationCombo", "1 second", "Fade previews out over 1 second.", "1 second fade-out duration", "Dismiss screenshot previews with a 1 second fade-out animation.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "ToastFadeDurationCombo", "2 seconds", "Fade previews out over 2 seconds.", "2 second fade-out duration", "Dismiss screenshot previews with a 2 second fade-out animation.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "ToastFadeDurationCombo", "3 seconds", "Fade previews out over 3 seconds.", "3 second fade-out duration", "Dismiss screenshot previews with a 3 second fade-out animation.");
        AssertComboBoxItemInNamedComboHasLabel(xaml, "ToastFadeDurationCombo", "5 seconds", "Fade previews out over 5 seconds.", "5 second fade-out duration", "Dismiss screenshot previews with a 5 second fade-out animation.");

        var positionBlock = GetMethodBlock(preferencesCode, "private void ToastPositionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressToastPreferenceChange) return;", positionBlock);
        Assert.Contains("var previous = _settingsService.Settings.ToastPosition;", positionBlock);
        Assert.Contains("\"settings.toast-position\"", positionBlock);
        Assert.Contains("value => _settingsService.Settings.ToastPosition = value", positionBlock);
        Assert.Contains("ToastPositionCombo.SelectedIndex = (int)value", positionBlock);
        Assert.Contains("ToastWindow.SetPosition(value);", positionBlock);
        Assert.Contains("PreviewWindow.SetPosition(value);", positionBlock);

        var durationBlock = GetMethodBlock(preferencesCode, "private void ToastDurationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressToastPreferenceChange) return;", durationBlock);
        Assert.Contains("var previous = _settingsService.Settings.ToastDurationSeconds;", durationBlock);
        Assert.Contains("\"settings.toast-duration\"", durationBlock);
        Assert.Contains("value => _settingsService.Settings.ToastDurationSeconds = value", durationBlock);
        Assert.Contains("SelectToastDuration", durationBlock);
        Assert.Contains("ToastWindow.SetDuration", durationBlock);

        var fadeBlock = GetMethodBlock(preferencesCode, "private void ToastFadeOutCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressToastPreferenceChange) return;", fadeBlock);
        Assert.Contains("var previous = _settingsService.Settings.ToastFadeOutEnabled;", fadeBlock);
        Assert.Contains("\"settings.toast-fade-out\"", fadeBlock);
        Assert.Contains("ToastFadeOutCheck.IsChecked = value;", fadeBlock);
        Assert.Contains("SetToastFadeDurationVisibility(value);", fadeBlock);
        Assert.Contains("ToastWindow.SetFadeOutBehavior(value, _settingsService.Settings.ToastFadeOutSeconds)", fadeBlock);

        var fadeDurationBlock = GetMethodBlock(preferencesCode, "private void ToastFadeDurationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressToastPreferenceChange) return;", fadeDurationBlock);
        Assert.Contains("var previous = _settingsService.Settings.ToastFadeOutSeconds;", fadeDurationBlock);
        Assert.Contains("\"settings.toast-fade-duration\"", fadeDurationBlock);
        Assert.Contains("value => _settingsService.Settings.ToastFadeOutSeconds = value", fadeDurationBlock);
        Assert.Contains("SelectToastFadeDuration", fadeDurationBlock);
        Assert.Contains("ToastWindow.SetFadeOutBehavior(_settingsService.Settings.ToastFadeOutEnabled, value)", fadeDurationBlock);

        var autoPinBlock = GetMethodBlock(preferencesCode, "private void AutoPinPreviewsCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressToastPreferenceChange) return;", autoPinBlock);
        Assert.Contains("var previous = _settingsService.Settings.AutoPinPreviews;", autoPinBlock);
        Assert.Contains("\"settings.auto-pin-previews\"", autoPinBlock);
        Assert.Contains("value => _settingsService.Settings.AutoPinPreviews = value", autoPinBlock);
        Assert.Contains("value => AutoPinPreviewsCheck.IsChecked = value", autoPinBlock);

        var helperBlock = GetMethodBlock(preferencesCode, "private void UpdateToastPreference<T>(");
        Assert.Contains("setValue(current);", helperBlock);
        Assert.Contains("_settingsService.Save();", helperBlock);
        Assert.Contains("SetToastPreferenceStatus(string.Empty);", helperBlock);
        Assert.Contains("applySuccessUi?.Invoke(current);", helperBlock);
        Assert.Contains("applyRuntime?.Invoke(current);", helperBlock);
        Assert.Contains("AppDiagnostics.LogError(diagnosticKey, ex);", helperBlock);
        Assert.Contains("setValue(previous);", helperBlock);
        Assert.Contains("AppDiagnostics.LogError($\"{diagnosticKey}-rollback\", rollbackEx);", helperBlock);
        Assert.Contains("_suppressToastPreferenceChange = true;", helperBlock);
        Assert.Contains("restoreUi(previous);", helperBlock);
        Assert.Contains("_suppressToastPreferenceChange = false;", helperBlock);
        Assert.Contains("applyRuntime?.Invoke(previous);", helperBlock);
        Assert.Contains("SetToastPreferenceStatus($\"{label} change was not saved. Previous setting restored.\");", helperBlock);
        Assert.Contains("ToastWindow.ShowError(", helperBlock);
        Assert.Contains("The previous toast setting was restored. Check Settings -> Toasts and try again.", helperBlock);

        var statusBlock = GetMethodBlock(preferencesCode, "private void SetToastPreferenceStatus(string message)");
        Assert.Contains("ToastPreferenceStatusText.Text = message;", statusBlock);
        Assert.Contains("Visibility.Collapsed", statusBlock);
        Assert.Contains("Visibility.Visible", statusBlock);

        var visibilityBlock = GetMethodBlock(preferencesCode, "private void SetToastFadeDurationVisibility(bool enabled)");
        Assert.Contains("ToastFadeDurationSeparator.Visibility = visibility;", visibilityBlock);
        Assert.Contains("ToastFadeDurationRow.Visibility = visibility;", visibilityBlock);
    }

    [Fact]
    public void ImageHistoryCountsUseCurrentFileSizeFallback()
    {
        var searchCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.Search.cs"));
        var historyCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.History.cs"));

        Assert.Contains("private static long GetHistoryItemFileSize(HistoryItemVM item)", historyCode);
        Assert.Contains("item.Entry.FileSizeBytes > 0 ? item.Entry.FileSizeBytes : TryGetFileLength(item.Entry.FilePath)", historyCode);

        var immediateBlock = GetMethodBlock(searchCode, "private void ApplyImmediateImageFilter(string query, ImageSearchSourceOptions sources, bool exactMatch)");
        Assert.Contains("visibleBytes += GetHistoryItemFileSize(item);", immediateBlock);
        Assert.DoesNotContain("visibleBytes += item.Entry.FileSizeBytes;", immediateBlock);

        var indexedBlock = GetMethodBlock(searchCode, "private async Task ApplyIndexedImageSearchAsync(int version, string query, ImageSearchSourceOptions sources, CancellationToken cancellationToken)");
        Assert.Contains("visibleBytes += GetHistoryItemFileSize(item);", indexedBlock);
        Assert.DoesNotContain("visibleBytes += item.Entry.FileSizeBytes;", indexedBlock);

        var loadedCountBlock = GetMethodBlock(historyCode, "private void UpdateLoadedImageHistoryCountText()");
        Assert.Contains("visibleBytes += GetHistoryItemFileSize(item);", loadedCountBlock);
        Assert.DoesNotContain("visibleBytes += item.Entry.FileSizeBytes;", loadedCountBlock);
    }

    [Fact]
    public void IndexedImageSearchFailuresAreLogged()
    {
        var searchCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.Search.cs"));

        var indexedBlock = GetMethodBlock(searchCode, "private async Task ApplyIndexedImageSearchAsync(int version, string query, ImageSearchSourceOptions sources, CancellationToken cancellationToken)");
        Assert.Contains("catch (Exception ex)", indexedBlock);
        Assert.Contains("searchFailed = true;", indexedBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.image-search\", ex);", indexedBlock);
        Assert.Contains("SetImageSearchLoading(false, forceIndexed: true);", indexedBlock);
        Assert.Contains("HistorySearchStatusText.Text = \"Search failed\";", indexedBlock);
    }

    [Fact]
    public void ImageSearchIndexRequestFailuresAreLogged()
    {
        var historyCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.History.cs"));

        var loadBlock = GetMethodBlock(historyCode, "private async Task LoadHistoryAsync()");
        Assert.Contains("_imageSearchIndexService.RequestSync(entries, _settingsService.Settings.OcrLanguageTag);", loadBlock);
        Assert.Contains("catch (Exception ex)", loadBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.image-search-request\", ex);", loadBlock);
        Assert.DoesNotContain("catch { }", loadBlock);
    }

    [Fact]
    public void ImageSearchSourcePreferencesRollBackAndReportFailures()
    {
        var actionsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.Actions.cs"));

        var exactBlock = GetMethodBlock(actionsCode, "private void ImageSearchExactMatchCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressImageSearchSourceEvents)", exactBlock);
        Assert.Contains("var previous = _settingsService.Settings.ImageSearchExactMatch;", exactBlock);
        Assert.Contains("var selected = ImageSearchExactMatchCheck.IsChecked == true;", exactBlock);
        Assert.Contains("\"settings.image-search-exact-match\"", exactBlock);
        Assert.Contains("value => _settingsService.Settings.ImageSearchExactMatch = value", exactBlock);
        Assert.Contains("value => ImageSearchExactMatchCheck.IsChecked = value", exactBlock);

        var sourcesBlock = GetMethodBlock(actionsCode, "private void ImageSearchSourcesCheck_Changed(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!IsLoaded || _suppressImageSearchSourceEvents)", sourcesBlock);
        Assert.Contains("var previous = _settingsService.Settings.ImageSearchSources;", sourcesBlock);
        Assert.Contains("var selected = GetImageSearchSourcesFromUi();", sourcesBlock);
        Assert.Contains("\"settings.image-search-sources\"", sourcesBlock);
        Assert.Contains("value => _settingsService.Settings.ImageSearchSources = value", sourcesBlock);
        Assert.Contains("RestoreImageSearchSourceChecks", sourcesBlock);

        var helperBlock = GetMethodBlock(actionsCode, "private void UpdateImageSearchPreference<T>(");
        Assert.Contains("setValue(current);", helperBlock);
        Assert.Contains("_settingsService.Save();", helperBlock);
        Assert.Contains("UpdateImageSearchSourceSummary();", helperBlock);
        Assert.Contains("CancelImageSearchWork();", helperBlock);
        Assert.Contains("ApplyImageSearchFilter();", helperBlock);
        Assert.Contains("AppDiagnostics.LogError(diagnosticKey, ex);", helperBlock);
        Assert.Contains("setValue(previous);", helperBlock);
        Assert.Contains("AppDiagnostics.LogError($\"{diagnosticKey}-rollback\", rollbackEx);", helperBlock);
        Assert.Contains("_suppressImageSearchSourceEvents = true;", helperBlock);
        Assert.Contains("restoreUi(previous);", helperBlock);
        Assert.Contains("_suppressImageSearchSourceEvents = false;", helperBlock);
        Assert.Contains("HistorySearchStatusText.Text = $\"{label} change was not saved. Previous setting restored.\";", helperBlock);
        Assert.Contains("ToastWindow.ShowError(", helperBlock);
        Assert.Contains("The previous search setting was restored. Check Settings -> History and try again.", helperBlock);

        var restoreBlock = GetMethodBlock(actionsCode, "private void RestoreImageSearchSourceChecks(ImageSearchSourceOptions sources)");
        Assert.Contains("ImageSearchFileNameCheck.IsChecked = (sources & ImageSearchSourceOptions.FileName) != 0;", restoreBlock);
        Assert.Contains("ImageSearchOcrCheck.IsChecked = (sources & ImageSearchSourceOptions.Ocr) != 0;", restoreBlock);
    }

    [Fact]
    public void ImageSearchFilterMenuAndIndexActionHaveAccessibleLabels()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "HistoryWindow.xaml"));
        var actionsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.Actions.cs"));

        AssertNamedControlHasLabel(xaml, "ImageSearchFileNameCheck", "<MenuItem", "Search file names", "Search screenshot file names", "Include screenshot file names in History search results.");
        AssertNamedControlHasLabel(xaml, "ImageSearchOcrCheck", "<MenuItem", "Search OCR text", "Search recognized screenshot text", "Include recognized text from indexed screenshots in History search results.");
        AssertNamedControlHasLabel(xaml, "ImageSearchExactMatchCheck", "<MenuItem", "Exact match search", "Require exact phrase/token matches", "Only show History search results that match the exact phrase or token.");

        var actionBlock = GetMethodBlock(actionsCode, "private void UpdateImageSearchActionButtons()");
        Assert.Contains("UpdateReindexAllButtonLabel(status, \"Image search indexing is already running.\");", actionBlock);
        Assert.Contains("UpdateReindexAllButtonLabel(\"Refresh image search index\", \"Refresh the image search index for all screenshot history items.\");", actionBlock);
        Assert.Contains("UpdateReindexAllButtonLabel(\"Index remaining screenshots\", $\"Index {total - indexed} screenshots for History search.\");", actionBlock);
        Assert.Contains("UpdateReindexAllButtonLabel(\"Image search index complete\", \"All visible screenshot history items are indexed.\");", actionBlock);

        var labelBlock = GetMethodBlock(actionsCode, "private void UpdateReindexAllButtonLabel(string automationName, string helpText)");
        Assert.Contains("ReindexAllBtn.ToolTip = helpText;", labelBlock);
        Assert.Contains("AutomationProperties.SetName(ReindexAllBtn, automationName);", labelBlock);
        Assert.Contains("AutomationProperties.SetHelpText(ReindexAllBtn, helpText);", labelBlock);
    }

    [Fact]
    public void HistorySearchInputMetadataTracksCategory()
    {
        var actionsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.Actions.cs"));

        var block = GetMethodBlock(actionsCode, "private void UpdateImageSearchPlaceholderText()");
        Assert.Contains("placeholder = \"Search text captures\";", block);
        Assert.Contains("automationName = \"Text history search\";", block);
        Assert.Contains("helpText = \"Search saved OCR text captures.\";", block);
        Assert.Contains("placeholder = \"Search hex, RGB, or color names\";", block);
        Assert.Contains("automationName = \"Color history search\";", block);
        Assert.Contains("helpText = \"Search saved colors by hex value, RGB values, or color names.\";", block);
        Assert.Contains("placeholder = \"Search QR/barcode text, links, or formats\";", block);
        Assert.Contains("automationName = \"Code history search\";", block);
        Assert.Contains("helpText = \"Search saved QR and barcode text, links, or code formats.\";", block);
        Assert.Contains("placeholder = isIndexing", block);
        Assert.Contains("automationName = \"Screenshot history search\";", block);
        Assert.Contains("ImageSearchBox.ToolTip = helpText;", block);
        Assert.Contains("AutomationProperties.SetName(ImageSearchBox, automationName);", block);
        Assert.Contains("AutomationProperties.SetHelpText(ImageSearchBox, helpText);", block);
    }

    [Fact]
    public void VideoThumbnailGenerationDrainsAndLogsFfmpegFailures()
    {
        var mediaHistoryCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaHistory.cs"));

        Assert.Contains("private const int VideoThumbnailDiagnosticMaxLength = 220;", mediaHistoryCode);

        var createBlock = GetMethodBlock(mediaHistoryCode, "private static async Task<bool> TryCreateVideoThumbnailAsync(string ffmpeg, string videoPath, string thumbPath, string arguments)");
        Assert.Contains("RedirectStandardError = true", createBlock);
        Assert.Contains("var stderrTask = proc.StandardError.ReadToEndAsync();", createBlock);
        Assert.Contains("await proc.WaitForExitAsync();", createBlock);
        Assert.Contains("var stderr = await stderrTask;", createBlock);
        Assert.Contains("AppDiagnostics.LogWarning(", createBlock);
        Assert.Contains("\"history.video-thumb.ffmpeg\"", createBlock);
        Assert.Contains("exitCode={proc.ExitCode}", createBlock);
        Assert.Contains("TrimThumbnailDiagnostic(stderr)", createBlock);
        Assert.Contains("catch (Exception ex)", createBlock);
        Assert.Contains("Failed to run ffmpeg", createBlock);
        Assert.Contains("TryDeleteVideoThumbnailFile(thumbPath, \"previous ffmpeg output\");", createBlock);

        var trimBlock = GetMethodBlock(mediaHistoryCode, "private static string TrimThumbnailDiagnostic(string? message)");
        Assert.Contains("if (string.IsNullOrWhiteSpace(message))", trimBlock);
        Assert.Contains("message.ReplaceLineEndings(\" \").Trim();", trimBlock);
        Assert.Contains("VideoThumbnailDiagnosticMaxLength", trimBlock);
    }

    [Fact]
    public void VideoThumbnailGenerationRejectsBlankFallbacksAndLogsDeleteFailures()
    {
        var mediaHistoryCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaHistory.cs"));

        var ensureBlock = GetMethodBlock(mediaHistoryCode, "private static async Task<string> EnsureVideoThumbnailAsync(string videoPath, string thumbPath)");
        Assert.Contains("TryDeleteVideoThumbnailFile(thumbPath, \"stale video thumbnail\");", ensureBlock);
        Assert.Contains("var usableThumbnail = IsUsableVideoThumbnail(thumbPath);", ensureBlock);
        Assert.Contains("if (!usableThumbnail)", ensureBlock);
        Assert.Contains("TryDeleteVideoThumbnailFile(thumbPath, \"unusable video thumbnail\");", ensureBlock);
        Assert.Contains("var result = usableThumbnail ? thumbPath : videoPath;", ensureBlock);
        Assert.Contains("RememberFailedVideoThumbnail(videoPath);", ensureBlock);
        Assert.Contains("catch (Exception ex)", ensureBlock);

        var deleteBlock = GetMethodBlock(mediaHistoryCode, "private static void TryDeleteVideoThumbnailFile(string thumbPath, string reason)");
        Assert.Contains("File.Delete(thumbPath);", deleteBlock);
        Assert.Contains("catch (Exception ex)", deleteBlock);
        Assert.Contains("\"history.video-thumb.delete\"", deleteBlock);
        Assert.Contains("Failed to delete {reason}", deleteBlock);
    }

    [Fact]
    public void VideoThumbnailGenerationRejectsUnreadableCachedThumbnails()
    {
        var mediaHistoryCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaHistory.cs"));
        var mediaHelpersCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaHelpers.cs"));

        var ensureBlock = GetMethodBlock(mediaHistoryCode, "private static async Task<string> EnsureVideoThumbnailAsync(string videoPath, string thumbPath)");
        Assert.Contains("if (IsUsableVideoThumbnail(thumbPath))", ensureBlock);
        Assert.DoesNotContain("File.Exists(thumbPath) && !IsLikelyBlankVideoThumbnail(thumbPath)", ensureBlock);

        var createBlock = GetMethodBlock(mediaHistoryCode, "private static async Task<bool> TryCreateVideoThumbnailAsync(string ffmpeg, string videoPath, string thumbPath, string arguments)");
        Assert.Contains("proc.ExitCode == 0 && IsUsableVideoThumbnail(thumbPath)", createBlock);

        var usableBlock = GetMethodBlock(mediaHistoryCode, "private static bool IsUsableVideoThumbnail(string thumbPath)");
        Assert.Contains("File.Exists(thumbPath) && !IsLikelyBlankVideoThumbnail(thumbPath)", usableBlock);

        var blankBlock = GetMethodBlock(mediaHistoryCode, "private static bool IsLikelyBlankVideoThumbnail(string thumbPath)");
        Assert.Contains("catch (Exception ex)", blankBlock);
        Assert.Contains("\"history.video-thumb.read\"", blankBlock);
        Assert.Contains("Rejecting unreadable video thumbnail", blankBlock);
        Assert.Contains("return true;", blankBlock);

        var cachedPathBlock = GetMethodBlock(mediaHelpersCode, "private static string? GetExistingCachedThumbnailPath(string thumbPath, string sourcePath, HistoryKind kind)");
        Assert.Contains("if (IsUsableVideoThumbnail(thumbPath))", cachedPathBlock);
        Assert.Contains("TryDeleteVideoThumbnailFile(thumbPath, \"cached unusable video thumbnail\");", cachedPathBlock);
        Assert.DoesNotContain("return File.Exists(thumbPath) ? thumbPath : null;", cachedPathBlock);
    }

    [Fact]
    public void VideoThumbnailFailureCacheInvalidatesWhenFileChanges()
    {
        var mediaHistoryCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaHistory.cs"));

        Assert.Contains("Dictionary<string, (long Length, long LastWriteTicks)> FailedVideoThumbnailPaths", mediaHistoryCode);

        var hasFailedBlock = GetMethodBlock(mediaHistoryCode, "private static bool HasFailedVideoThumbnail(string videoPath)");
        Assert.Contains("var signature = GetVideoThumbnailFailureSignature(videoPath);", hasFailedBlock);
        Assert.Contains("FailedVideoThumbnailPaths.TryGetValue(videoPath, out var failedSignature)", hasFailedBlock);
        Assert.Contains("failedSignature == signature", hasFailedBlock);

        var rememberBlock = GetMethodBlock(mediaHistoryCode, "private static void RememberFailedVideoThumbnail(string videoPath)");
        Assert.Contains("var signature = GetVideoThumbnailFailureSignature(videoPath);", rememberBlock);
        Assert.Contains("FailedVideoThumbnailPaths[videoPath] = signature;", rememberBlock);

        var signatureBlock = GetMethodBlock(mediaHistoryCode, "private static (long Length, long LastWriteTicks) GetVideoThumbnailFailureSignature(string videoPath)");
        Assert.Contains("new FileInfo(videoPath)", signatureBlock);
        Assert.Contains("info.Length", signatureBlock);
        Assert.Contains("info.LastWriteTimeUtc.Ticks", signatureBlock);
        Assert.Contains("catch (Exception ex)", signatureBlock);
        Assert.Contains("\"history.video-thumb.signature\"", signatureBlock);
        Assert.Contains("Failed to read video signature", signatureBlock);
        Assert.Contains("return (0, 0);", signatureBlock);
    }

    [Fact]
    public void MediaHistoryMetadataFailuresAreLogged()
    {
        var mediaHistoryCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaHistory.cs"));

        var addInfoBlock = GetMethodBlock(mediaHistoryCode, "private static void AddMediaInfo(StackPanel panel, string fileName, string timeAgo, string filePath)");
        Assert.Contains("var sizeStr = TryGetMediaSizeText(filePath);", addInfoBlock);
        Assert.DoesNotContain("catch { }", addInfoBlock);

        var sizeTextBlock = GetMethodBlock(mediaHistoryCode, "private static string TryGetMediaSizeText(string filePath)");
        Assert.Contains("if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))", sizeTextBlock);
        Assert.Contains("return FormatStorageSize(new FileInfo(filePath).Length);", sizeTextBlock);
        Assert.Contains("catch (Exception ex)", sizeTextBlock);
        Assert.Contains("\"history.media-info.size\"", sizeTextBlock);
        Assert.Contains("Failed to read media size", sizeTextBlock);
        Assert.Contains("return \"\";", sizeTextBlock);
    }

    [Fact]
    public void ThumbnailBackgroundLoadersLogFailures()
    {
        var mediaHistoryCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaHistory.cs"));

        var loadBlock = GetMethodBlock(mediaHistoryCode, "private static void LoadThumbAsync(System.Windows.Controls.Image img, HistoryItemVM vm, string path, string? sourcePath)");
        Assert.Contains("catch (Exception ex)", loadBlock);
        Assert.Contains("\"history.thumb-load\"", loadBlock);
        Assert.Contains("Failed to load thumbnail", loadBlock);
        Assert.Contains("SettingsMediaCache.EndInflight(cacheKey);", loadBlock);

        var primeBlock = GetMethodBlock(mediaHistoryCode, "private static void PrimeThumbLoad(string cacheKey, string thumbPath, HistoryKind kind, Action<BitmapSource>? onReady = null, Action? onLoaded = null)");
        Assert.Contains("catch (Exception ex)", primeBlock);
        Assert.Contains("\"history.thumb-prime\"", primeBlock);
        Assert.Contains("Failed to prime thumbnail", primeBlock);
        Assert.Contains("SettingsMediaCache.EndInflight(cacheKey);", primeBlock);
    }

    [Fact]
    public void MediaThumbnailPreloadDoesNotTreatPlaceholdersAsLoadedThumbnails()
    {
        var mediaHistoryCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaHistory.cs"));

        var primeMediaBlock = GetMethodBlock(mediaHistoryCode, "private static void PrimeMediaThumbnailLoads(IEnumerable<HistoryItemVM> items)");
        Assert.Contains("item.ThumbnailLoaded && item.ThumbnailSource != null && !IsStaleHistoryPlaceholder(item.ThumbnailSource, item.Entry.Kind)", primeMediaBlock);

        var primeVmBlock = GetMethodBlock(mediaHistoryCode, "private static void PrimeThumbLoad(HistoryItemVM vm, Action? onLoaded = null)");
        Assert.Contains("vm.ThumbnailLoaded && vm.ThumbnailSource != null && !IsStaleHistoryPlaceholder(vm.ThumbnailSource, vm.Entry.Kind)", primeVmBlock);
        Assert.Contains("ApplyThumbnailToBoundImage(vm, vm.ThumbnailSource, animate: false);", primeVmBlock);
    }

    [Fact]
    public void ThumbnailCachePlaceholdersDoNotBlockRetry()
    {
        var mediaHistoryCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaHistory.cs"));

        var loadBlock = GetMethodBlock(mediaHistoryCode, "private static void LoadThumbAsync(System.Windows.Controls.Image img, HistoryItemVM vm, string path, string? sourcePath)");
        Assert.Contains("TryGetThumbFromCache(cacheKey, out var cached) && cached is not null && !IsStaleHistoryPlaceholder(cached, vm.Entry.Kind)", loadBlock);

        var primeBlock = GetMethodBlock(mediaHistoryCode, "private static void PrimeThumbLoad(string cacheKey, string thumbPath, HistoryKind kind, Action<BitmapSource>? onReady = null, Action? onLoaded = null)");
        Assert.Contains("TryGetThumbFromCache(cacheKey, out var cached) && cached is not null && !IsStaleHistoryPlaceholder(cached, kind)", primeBlock);
    }

    [Fact]
    public void VideoThumbnailWarmupLogsBackgroundFailures()
    {
        var mediaHistoryCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaHistory.cs"));

        var warmupBlock = GetMethodBlock(mediaHistoryCode, "private static void QueueMissingVideoThumbnailWarmup(IEnumerable<HistoryItemVM> items)");
        Assert.Contains("Task.Run(async () =>", warmupBlock);
        Assert.Contains("try", warmupBlock);
        Assert.Contains("Task.WhenAll(batch);", warmupBlock);
        Assert.Contains("Task.Delay(35);", warmupBlock);
        Assert.Contains("catch (Exception ex)", warmupBlock);
        Assert.Contains("\"history.video-thumb.warmup\"", warmupBlock);
        Assert.Contains("Failed to warm video thumbnails", warmupBlock);
    }

    [Fact]
    public void OrphanVideoThumbnailCleanupLogsFailures()
    {
        var mediaHistoryCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaHistory.cs"));

        var queueBlock = GetMethodBlock(mediaHistoryCode, "private void QueueOrphanVideoThumbnailCleanup(IEnumerable<HistoryItemVM> items)");
        Assert.Contains("Task.Run(() =>", queueBlock);
        Assert.Contains("try", queueBlock);
        Assert.Contains("CleanupOrphanVideoThumbnails(snapshot);", queueBlock);
        Assert.Contains("catch (Exception ex)", queueBlock);
        Assert.Contains("\"history.video-thumb.cleanup\"", queueBlock);
        Assert.Contains("Failed to clean orphan video thumbnails", queueBlock);

        var cleanupBlock = GetMethodBlock(mediaHistoryCode, "private void CleanupOrphanVideoThumbnails(IEnumerable<HistoryItemVM> items)");
        Assert.Contains("File.Delete(thumb);", cleanupBlock);
        Assert.Contains("catch (Exception ex)", cleanupBlock);
        Assert.Contains("\"history.video-thumb.cleanup\"", cleanupBlock);
        Assert.Contains("Failed to delete orphan video thumbnail", cleanupBlock);
    }

    [Fact]
    public void ThumbnailCacheReadWriteFailuresAreLogged()
    {
        var mediaHelpersCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaHelpers.cs"));

        var loadCachedBlock = GetMethodBlock(mediaHelpersCode, "private static bool TryLoadCachedThumbnailSource(string cacheKey, string thumbPath, string? sourcePath, HistoryKind kind, out BitmapSource? image)");
        Assert.Contains("\"history.thumb-cache.read\"", loadCachedBlock);
        Assert.Contains("TryDeleteThumbnailCacheFile(diskPath);", loadCachedBlock);

        var loadOrCreateBlock = GetMethodBlock(mediaHelpersCode, "private static BitmapSource? LoadOrCreateThumbnailSource(string loadPath, string sourcePath, HistoryKind kind)");
        Assert.Contains("\"history.thumb-cache.read\"", loadOrCreateBlock);
        Assert.Contains("TryDeleteThumbnailCacheFile(persistentPath);", loadOrCreateBlock);

        var pathBlock = GetMethodBlock(mediaHelpersCode, "private static string? GetPersistentThumbnailPath(string sourcePath, HistoryKind kind)");
        Assert.Contains("catch (Exception ex)", pathBlock);
        Assert.Contains("\"history.thumb-cache.path\"", pathBlock);

        var saveBlock = GetMethodBlock(mediaHelpersCode, "private static void SavePersistentThumbnail(BitmapSource bitmap, string sourcePath, HistoryKind kind)");
        Assert.Contains("catch (Exception ex)", saveBlock);
        Assert.Contains("\"history.thumb-cache.save\"", saveBlock);

        var deleteBlock = GetMethodBlock(mediaHelpersCode, "private static void TryDeleteThumbnailCacheFile(string thumbPath)");
        Assert.Contains("File.Delete(thumbPath);", deleteBlock);
        Assert.Contains("catch (Exception ex)", deleteBlock);
        Assert.Contains("\"history.thumb-cache.delete\"", deleteBlock);
    }

    [Fact]
    public void HistorySearchInputFailuresKeepFailureStatusVisible()
    {
        var actionCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.Actions.cs"));

        var textChangedBlock = GetMethodBlock(actionCode, "private void ImageSearchBox_TextChanged(object sender, TextChangedEventArgs e)");
        AssertSearchFailureStatusWrittenAfterLoadingStops(textChangedBlock);

        var keyDownBlock = GetMethodBlock(actionCode, "private void ImageSearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)");
        AssertSearchFailureStatusWrittenAfterLoadingStops(keyDownBlock);
    }

    [Fact]
    public void ImageSearchDispatcherFailuresKeepFailureStatusVisible()
    {
        var settingsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "HistoryWindow.xaml.cs"));
        var indexServiceCode = File.ReadAllText(RepoPath("src", "CyberSnap", "Services", "ImageSearchIndexService.Indexing.cs"));

        var indexChangedBlock = GetMethodBlock(settingsCode, "private void ImageSearchIndexService_Changed()");
        AssertSearchCallbackFailureStopsLoadingThenSetsStatus(indexChangedBlock);
        Assert.Contains("AppDiagnostics.LogError(\"history.image-search-index-changed\", ex);", indexChangedBlock);

        var statusChangedBlock = GetMethodBlock(settingsCode, "private void ImageSearchIndexService_StatusChanged(string status)");
        AssertSearchCallbackFailureStopsLoadingThenSetsStatus(statusChangedBlock);
        Assert.Contains("AppDiagnostics.LogError(\"history.image-search-status\", ex);", statusChangedBlock);

        var syncLoopBlock = GetMethodBlock(indexServiceCode, "private async Task RunSyncLoopSafelyAsync(CancellationToken cancellationToken)");
        Assert.Contains("AppDiagnostics.LogError(\"image-search.indexing\", ex);", syncLoopBlock);
        Assert.Contains("SetStatus(\"Indexing failed. Existing search data is still available.\");", syncLoopBlock);
        Assert.DoesNotContain("SetStatus($\"Indexing failed: {ex.Message}\");", syncLoopBlock);
    }

    [Fact]
    public void HistoryCodeCopyReportsClipboardFailures()
    {
        var codeHistoryCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.CodeHistory.cs"));
        var cardBlock = GetMethodBlock(codeHistoryCode, "private Border CreateCodeHistoryCard(CodeHistoryEntry entry)");

        Assert.Contains("copyBtn.Click += (_, _) =>", cardBlock);
        Assert.Contains("ClipboardService.CopyTextToClipboard(capturedText);", cardBlock);
        Assert.Contains("ToastWindow.Show(\"Copied\", \"Text copied\");", cardBlock);
        Assert.Contains("catch (Exception ex)", cardBlock);
        Assert.Contains("CyberSnap could not copy this QR/barcode history item. Try again from Settings -> History, or copy the visible decoded value manually.", cardBlock);
        Assert.Contains("void CopyCodeText()", cardBlock);

        var copyButtonIndex = cardBlock.IndexOf("copyBtn.Click += (_, _) =>", StringComparison.Ordinal);
        var cardClickIndex = cardBlock.IndexOf("card.MouseLeftButtonDown += (_, e) =>", StringComparison.Ordinal);
        Assert.True(cardClickIndex > copyButtonIndex, "Code card should keep separate copy button and card click handlers.");

        Assert.Contains("copyBtn.Click += (_, _) => CopyCodeText();", cardBlock);
        Assert.Contains("CopyCodeText();", cardBlock[cardClickIndex..]);
    }

    [Fact]
    public void HistoryCodeUrlOpenValidatesExternalTargets()
    {
        var codeHistoryCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.CodeHistory.cs"));
        var cardBlock = GetMethodBlock(codeHistoryCode, "private Border CreateCodeHistoryCard(CodeHistoryEntry entry)");

        var normalizeBlock = GetMethodBlock(codeHistoryCode, "private static bool TryNormalizeUrl(string text, out string url)");
        Assert.Contains("uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps", normalizeBlock);
        Assert.Contains("Uri.TryCreate(\"https://\" + trimmed, UriKind.Absolute, out var withScheme)", normalizeBlock);

        var openBlock = GetMethodBlock(codeHistoryCode, "private static bool TryOpenExternalUrl(string url)");
        Assert.Contains("openBtn.Click += (_, _) => TryOpenExternalUrl(url);", cardBlock);
        Assert.Contains("if (string.IsNullOrWhiteSpace(url))", openBlock);
        Assert.Contains("ToastWindow.ShowError(\"Open failed\", \"No code URL is available.\");", openBlock);
        Assert.Contains("return false;", openBlock);
        Assert.Contains("Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)", openBlock);
        Assert.Contains("uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps", openBlock);
        Assert.Contains("ToastWindow.ShowError(\"Open failed\", \"The code URL is not a valid web link.\");", openBlock);
        Assert.Contains("using var process = Process.Start(new ProcessStartInfo", openBlock);
        Assert.Contains("FileName = uri.AbsoluteUri", openBlock);
        Assert.Contains("if (process is null)", openBlock);
        Assert.Contains("ToastWindow.ShowError(\"Open failed\", \"Windows did not open the code URL. Copy it from Settings -> History and open it manually.\");", openBlock);
        Assert.Contains("return true;", openBlock);
        Assert.DoesNotContain("FileName = url", openBlock);
        Assert.Contains("CyberSnap could not open the code URL. Copy it from Settings -> History and open it manually.", openBlock);
    }

    [Fact]
    public void HistoryTextAndColorCopyReportClipboardFailures()
    {
        var textColorHistoryCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.TextColorHistory.cs"));

        var ocrBlock = GetMethodBlock(textColorHistoryCode, "private Border CreateOcrHistoryCard(OcrHistoryEntry entry)");
        Assert.Contains("copyBtn.Click += (_, _) =>", ocrBlock);
        Assert.Contains("ClipboardService.CopyTextToClipboard(capturedText);", ocrBlock);
        Assert.Contains("ToastWindow.Show(\"Copied\", \"Text copied\");", ocrBlock);
        Assert.Contains("catch (Exception ex)", ocrBlock);
        Assert.Contains("CyberSnap could not copy this text history item. Try again from Settings -> History, or copy the visible text manually.", ocrBlock);

        var colorBlock = GetMethodBlock(textColorHistoryCode, "private Border CreateColorHistoryCard(ColorHistoryEntry entry)");
        Assert.Contains("TryParseHexColor(entry.Hex, out var r, out var g, out var b)", colorBlock);
        Assert.Contains("var displayHex = FormatColorHexForDisplay(entry.Hex);", colorBlock);
        Assert.Contains("Text = displayHex", colorBlock);
        Assert.DoesNotContain("Text = $\"#{entry.Hex}\"", colorBlock);
        Assert.Contains("AppDiagnostics.LogWarning(", colorBlock);
        Assert.Contains("\"history.color.invalid\"", colorBlock);
        Assert.Contains("System.Windows.Media.Color.FromArgb(0, 0, 0, 0)", colorBlock);
        Assert.Contains("Invalid color", colorBlock);
        Assert.DoesNotContain("Convert.ToByte(entry.Hex[..2], 16)", colorBlock);
        Assert.Contains("copyBtn.Click += (_, _) =>", colorBlock);
        Assert.Contains("card.MouseLeftButtonDown += (_, e) =>", colorBlock);
        Assert.Contains("ClipboardService.CopyTextToClipboard(capturedHex);", colorBlock);
        Assert.Contains("ToastWindow.Show(\"Copied\", capturedHex);", colorBlock);
        Assert.Contains("catch (Exception ex)", colorBlock);
        Assert.Contains("CyberSnap could not copy this color history item. Try again from Settings -> History, or copy the visible color value manually.", colorBlock);
        Assert.Contains("void CopyColorValue()", colorBlock);

        var copyButtonIndex = colorBlock.IndexOf("copyBtn.Click += (_, _) =>", StringComparison.Ordinal);
        var cardClickIndex = colorBlock.IndexOf("card.MouseLeftButtonDown += (_, e) =>", StringComparison.Ordinal);
        Assert.True(cardClickIndex > copyButtonIndex, "Color card should keep separate copy button and card click handlers.");

        Assert.Contains("copyBtn.Click += (_, _) => CopyColorValue();", colorBlock);
        Assert.Contains("CopyColorValue();", colorBlock[cardClickIndex..]);
    }

    [Fact]
    public void HistoryCardDefaultOpenReportsFailures()
    {
        var mediaCardCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaCard.cs"));

        var openBlock = GetMethodBlock(mediaCardCode, "private static bool OpenFileWithDefaultApp(string filePath)");
        Assert.Contains("if (!File.Exists(filePath))", openBlock);
        Assert.Contains("ShowHistoryFileMissingError(filePath);", openBlock);
        Assert.Contains("return false;", openBlock);
        Assert.Contains("using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo", openBlock);
        Assert.Contains("if (process is null)", openBlock);
        Assert.Contains("ToastWindow.ShowError(\"Open failed\", \"Windows did not open the saved file. Try again from Settings -> History, or open it from disk manually.\", filePath);", openBlock);
        Assert.Contains("return true;", openBlock);
        Assert.Contains("catch (Exception ex)", openBlock);
        Assert.Contains("CyberSnap could not open the saved file. Try again from Settings -> History, or open it from disk manually.", openBlock);
        Assert.Contains("filePath);", openBlock);
        Assert.DoesNotContain("catch\r\n            {\r\n            }", openBlock);
        Assert.DoesNotContain("Task.Run", openBlock);
    }

    [Fact]
    public void HistoryCardActionMenuButtonIsKeyboardAccessible()
    {
        var mediaCardCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaCard.cs"));
        var mediaBlock = GetMethodBlock(mediaCardCode, "private MediaCardShell BuildMediaCardShell(HistoryItemVM vm, Action copyAction)");

        Assert.Contains("ToolTip = \"Open history item actions\"", mediaBlock);
        Assert.Contains("Focusable = true,", mediaBlock);
        Assert.Contains("BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(210, 255, 255, 255))", mediaBlock);
        Assert.Contains("BorderThickness = new Thickness(1)", mediaBlock);
        Assert.Contains("AutomationProperties.SetName(actionMenuBtn, $\"{kindLabel} actions\");", mediaBlock);
        Assert.Contains("AutomationProperties.SetHelpText(actionMenuBtn, \"Press Enter or Space to open this history item's actions.\");", mediaBlock);
        Assert.Contains("void OpenActionMenu()", mediaBlock);
        Assert.Contains("actionMenuBtn.PreviewMouseLeftButtonUp += (_, e) =>", mediaBlock);
        Assert.Contains("actionMenuBtn.KeyDown += (_, e) =>", mediaBlock);
        Assert.Contains("if (!IsHistoryCardActivationKey(e))", mediaBlock);
        Assert.Contains("actionMenuBtn.GotKeyboardFocus += (_, _) =>", mediaBlock);
        Assert.Contains("actionMenuBtn.LostKeyboardFocus += (_, _) =>", mediaBlock);
        Assert.Contains("actionMenu.Closed += (_, _) =>", mediaBlock);
    }

    [Fact]
    public void HistoryCardBadgesExposeAccessibleLabels()
    {
        var mediaHistoryCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaHistory.cs"));
        var mediaHelpersCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaHelpers.cs"));
        var historyCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.History.cs"));

        var gifBlock = GetMethodBlock(mediaHistoryCode, "private Border CreateGifCard(HistoryItemVM vm)");
        Assert.Contains("ToolTip = \"GIF media type\"", gifBlock);
        Assert.Contains("AutomationProperties.SetName(gifBadge, \"GIF media type badge\");", gifBlock);
        Assert.Contains("AutomationProperties.SetHelpText(gifBadge, \"This history item is an animated GIF.\");", gifBlock);

        var providerBadgeBlock = GetMethodBlock(mediaHelpersCode, "private static FrameworkElement? CreateProviderBadge(string? providerOrPath, bool isPath = false)");
        Assert.Contains("var helpText = $\"Uploaded with {providerName}.\";", providerBadgeBlock);
        Assert.Contains("ToolTip = helpText", providerBadgeBlock);
        Assert.Contains("AutomationProperties.SetName(textBadge, $\"{providerName} upload provider badge\");", providerBadgeBlock);
        Assert.Contains("AutomationProperties.SetHelpText(textBadge, helpText);", providerBadgeBlock);
        Assert.Contains("AutomationProperties.SetName(logoBadge, $\"{providerName} upload provider badge\");", providerBadgeBlock);
        Assert.Contains("AutomationProperties.SetHelpText(logoBadge, helpText);", providerBadgeBlock);

        var selectionUpdateBlock = GetMethodBlock(historyCode, "private void UpdateCardSelection(HistoryItemVM vm)");
        Assert.Contains("UpdateSelectionBadgeAccessibility(vm.SelectionBadge, vm.IsSelected);", selectionUpdateBlock);
    }

    [Fact]
    public void HistoryMediaDeleteReloadsOnlyAfterDeleteFlowCompletes()
    {
        var historyCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.History.cs"));
        var mediaHistoryCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.MediaHistory.cs"));

        var deleteMediaBlock = GetMethodBlock(mediaHistoryCode, "private void DeleteMediaItems(IEnumerable<HistoryItemVM> items)");
        Assert.Contains("var entries = items.Select(item => item.Entry).ToList();", deleteMediaBlock);
        Assert.Contains("_historyService.DeleteEntries(entries);", deleteMediaBlock);
        Assert.DoesNotContain("LoadCurrentHistoryTab();", deleteMediaBlock);

        var deleteAllBlock = GetMethodBlock(historyCode, "private void DeleteAllClick(object sender, RoutedEventArgs e)");
        Assert.Contains("DeleteMediaItems(_allGifItems);", deleteAllBlock);
        Assert.Contains("LoadCurrentHistoryTab();", deleteAllBlock);

        var deleteSelectedBlock = GetMethodBlock(historyCode, "private void DeleteSelectedClick(object sender, RoutedEventArgs e)");
        Assert.Contains("DeleteMediaItems(_filteredGifItems.Where(i => i.IsSelected).ToList());", deleteSelectedBlock);
        Assert.Contains("LoadCurrentHistoryTab();", deleteSelectedBlock);
    }

    [Fact]
    public void HistoryDeleteActionsLeaveDurableStatusOnSuccessAndFailure()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "HistoryWindow.xaml"));
        var historyCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.History.cs"));

        AssertSettingsActionButton(xaml, "SelectBtn", "Select history items", "Select history items", "ToggleSelectMode");
        AssertSettingsActionButton(xaml, "DeleteAllBtn", "Clear current history tab", "Delete all items in the current history tab", "DeleteAllClick");
        AssertSettingsActionButton(xaml, "DeleteSelectedBtn", "Delete selected history items", "Delete selected history items", "DeleteSelectedClick");

        var deleteAllBlock = GetMethodBlock(historyCode, "private void DeleteAllClick(object sender, RoutedEventArgs e)");
        Assert.Contains("var totalCount = GetCurrentTotalHistoryItemCount();", deleteAllBlock);
        Assert.Contains("var tab = GetCurrentHistoryCategoryLabel(totalCount);", deleteAllBlock);
        Assert.Contains("if (totalCount <= 0)", deleteAllBlock);
        Assert.Contains("SetHistoryDeleteStatus($\"No {tab} to delete.\");", deleteAllBlock);
        Assert.Contains("UpdateHistoryActionButtons();", deleteAllBlock);
        Assert.Contains("if (!ConfirmDeleteAllStep(1, totalCount, tab)) return;", deleteAllBlock);
        Assert.Contains("if (!ConfirmDeleteAllStep(2, totalCount, tab)) return;", deleteAllBlock);
        Assert.Contains("if (!ConfirmDeleteAllStep(3, totalCount, tab)) return;", deleteAllBlock);
        Assert.True(
            deleteAllBlock.IndexOf("CancelImageSearchWork();", StringComparison.Ordinal) >
            deleteAllBlock.IndexOf("if (!ConfirmDeleteAllStep(3, totalCount, tab)) return;", StringComparison.Ordinal),
            "Image search work should only be canceled after all delete confirmations pass.");
        Assert.Contains("SetHistoryDeleteStatus($\"Deleted all {tab}.\");", deleteAllBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.history-delete-all\", ex);", deleteAllBlock);
        Assert.Contains("SetHistoryDeleteStatus($\"Delete failed for {GetCurrentHistoryCategoryLabel(2)}. Refresh History and try again.\");", deleteAllBlock);
        Assert.Contains("CyberSnap could not finish deleting {GetCurrentHistoryCategoryLabel(2)}. Refresh History and try again.", deleteAllBlock);

        var deleteSelectedBlock = GetMethodBlock(historyCode, "private void DeleteSelectedClick(object sender, RoutedEventArgs e)");
        Assert.Contains("var selectedCount = GetCurrentSelectedHistoryItemCount();", deleteSelectedBlock);
        Assert.Contains("var selectedLabel = GetCurrentHistoryCategoryLabel(selectedCount);", deleteSelectedBlock);
        Assert.Contains("if (selectedCount <= 0)", deleteSelectedBlock);
        Assert.Contains("SetHistoryDeleteStatus($\"Select {GetCurrentHistoryCategoryLabel(2)} to delete.\");", deleteSelectedBlock);
        Assert.Contains("if (!ConfirmDeleteSelected(selectedCount, selectedLabel))", deleteSelectedBlock);
        Assert.True(
            deleteSelectedBlock.IndexOf("CancelImageSearchWork();", StringComparison.Ordinal) >
            deleteSelectedBlock.IndexOf("if (!ConfirmDeleteSelected(selectedCount, selectedLabel))", StringComparison.Ordinal),
            "Image search work should only be canceled after selected-delete confirmation passes.");
        Assert.True(
            deleteSelectedBlock.IndexOf("_selectMode = false;", StringComparison.Ordinal) >
            deleteSelectedBlock.IndexOf("if (!ConfirmDeleteSelected(selectedCount, selectedLabel))", StringComparison.Ordinal),
            "Selection mode should stay active when selected-delete confirmation is canceled.");
        Assert.Contains("SetHistoryDeleteStatus($\"Deleted {selectedCount} selected {selectedLabel}.\");", deleteSelectedBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.history-delete-selected\", ex);", deleteSelectedBlock);
        Assert.Contains("SetHistoryDeleteStatus($\"Delete failed for selected {GetCurrentHistoryCategoryLabel(2)}. Refresh History and try again.\");", deleteSelectedBlock);
        Assert.Contains("CyberSnap could not finish deleting the selected {GetCurrentHistoryCategoryLabel(2)}. Refresh History and try again.", deleteSelectedBlock);

        var statusBlock = GetMethodBlock(historyCode, "private void SetHistoryDeleteStatus(string message)");
        Assert.Contains("HistorySearchStatusText.Text = message;", statusBlock);

        var confirmStepBlock = GetMethodBlock(historyCode, "private bool ConfirmDeleteAllStep(int step, int totalCount, string categoryLabel)");
        Assert.Contains("ThemedConfirmDialog.Confirm(this, BuildDeleteAllConfirmationTitle(step, totalCount, categoryLabel), BuildDeleteAllConfirmationMessage(step, totalCount, categoryLabel), \"Delete\", \"Cancel\")", confirmStepBlock);
        Assert.Contains("SetHistoryDeleteStatus($\"Delete canceled. Kept {totalCount} {categoryLabel}.\");", confirmStepBlock);
        Assert.Contains("UpdateHistoryActionButtons();", confirmStepBlock);
        Assert.Contains("return false;", confirmStepBlock);

        var confirmSelectedBlock = GetMethodBlock(historyCode, "private bool ConfirmDeleteSelected(int selectedCount, string categoryLabel)");
        Assert.Contains("ThemedConfirmDialog.Confirm(", confirmSelectedBlock);
        Assert.Contains("$\"Delete {selectedCount} selected {categoryLabel}\"", confirmSelectedBlock);
        Assert.Contains("$\"Delete {selectedCount} selected {categoryLabel}? This cannot be undone.\"", confirmSelectedBlock);
        Assert.Contains("SetHistoryDeleteStatus($\"Delete canceled. Kept {selectedCount} selected {categoryLabel}.\");", confirmSelectedBlock);
        Assert.Contains("UpdateHistoryActionButtons();", confirmSelectedBlock);
        Assert.Contains("return false;", confirmSelectedBlock);

        Assert.Contains("private static string BuildDeleteAllConfirmationTitle(int step, int totalCount, string categoryLabel)", historyCode);
        Assert.Contains("return $\"Delete {totalCount} {categoryLabel} ({step}/3)\";", historyCode);

        var categoryLabelBlock = GetMethodBlock(historyCode, "private string GetCurrentHistoryCategoryLabel(int count)");
        Assert.Contains("0 => count == 1 ? \"screenshot\" : \"screenshots\"", categoryLabelBlock);
        Assert.Contains("1 => count == 1 ? \"text capture\" : \"text captures\"", categoryLabelBlock);
        Assert.Contains("4 => count == 1 ? \"QR/barcode scan\" : \"QR/barcode scans\"", categoryLabelBlock);

        var confirmMessageBlock = GetMethodBlock(historyCode, "private static string BuildDeleteAllConfirmationMessage(int step, int totalCount, string categoryLabel)");
        Assert.Contains("Delete all {totalCount} {categoryLabel} in this history tab?", confirmMessageBlock);
        Assert.Contains("Really delete all {totalCount} {categoryLabel}?", confirmMessageBlock);
        Assert.Contains("This cannot be undone. Delete all {totalCount} {categoryLabel}?", confirmMessageBlock);
    }

    [Fact]
    public void HistorySearchFilterPopoverFocusesFirstOption()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "HistoryWindow.xaml"));
        var actionsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.Actions.cs"));

        var filtersBtnTag = GetOpeningTag(xaml, xaml.IndexOf("x:Name=\"ImageSearchFiltersBtn\"", StringComparison.Ordinal), "<Button");
        Assert.Contains("AutomationProperties.HelpText=\"Open search source and exact-match options for History.\"", filtersBtnTag);

        var filtersMenuTag = GetOpeningTag(xaml, xaml.IndexOf("x:Name=\"ImageSearchFiltersMenu\"", StringComparison.Ordinal), "<ContextMenu");
        Assert.Contains("AutomationProperties.Name=\"Image search filter options\"", filtersMenuTag);

        var fileNameItemTag = GetOpeningTag(xaml, xaml.IndexOf("x:Name=\"ImageSearchFileNameCheck\"", StringComparison.Ordinal), "<MenuItem");
        Assert.Contains("AutomationProperties.Name=\"Search file names\"", fileNameItemTag);
        Assert.Contains("AutomationProperties.HelpText=\"Include screenshot file names in History search results.\"", fileNameItemTag);

        var ocrItemTag = GetOpeningTag(xaml, xaml.IndexOf("x:Name=\"ImageSearchOcrCheck\"", StringComparison.Ordinal), "<MenuItem");
        Assert.Contains("AutomationProperties.Name=\"Search OCR text\"", ocrItemTag);
        Assert.Contains("AutomationProperties.HelpText=\"Include recognized text from indexed screenshots in History search results.\"", ocrItemTag);

        var exactMatchItemTag = GetOpeningTag(xaml, xaml.IndexOf("x:Name=\"ImageSearchExactMatchCheck\"", StringComparison.Ordinal), "<MenuItem");
        Assert.Contains("AutomationProperties.Name=\"Exact match search\"", exactMatchItemTag);
        Assert.Contains("AutomationProperties.HelpText=\"Only show History search results that match the exact phrase or token.\"", exactMatchItemTag);

        var clickBlock = GetMethodBlock(actionsCode, "private void ImageSearchFiltersBtn_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("ImageSearchFiltersMenu.PlacementTarget = ImageSearchFiltersBtn;", clickBlock);
        Assert.Contains("ImageSearchFiltersMenu.IsOpen = true;", clickBlock);
        Assert.Contains("_ = Dispatcher.BeginInvoke(() =>", clickBlock);
        Assert.Contains("ImageSearchFileNameCheck.Focus();", clickBlock);
        Assert.Contains("Keyboard.Focus(ImageSearchFileNameCheck);", clickBlock);
    }

    [Fact]
    public void HistoryCountAndStatusTextAreLiveRegions()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "HistoryWindow.xaml"));

        var countTag = GetOpeningTag(xaml, xaml.IndexOf("x:Name=\"HistoryCountText\"", StringComparison.Ordinal), "<TextBlock");
        Assert.Contains("ToolTip=\"{Binding Text, RelativeSource={RelativeSource Self}}\"", countTag);
        Assert.Contains("AutomationProperties.Name=\"History item count\"", countTag);
        Assert.Contains("AutomationProperties.HelpText=\"{Binding Text, RelativeSource={RelativeSource Self}}\"", countTag);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", countTag);

        var statusTag = GetOpeningTag(xaml, xaml.IndexOf("x:Name=\"HistorySearchStatusText\"", StringComparison.Ordinal), "<TextBlock");
        Assert.Contains("ToolTip=\"{Binding Text, RelativeSource={RelativeSource Self}}\"", statusTag);
        Assert.Contains("AutomationProperties.Name=\"History status\"", statusTag);
        Assert.Contains("AutomationProperties.HelpText=\"{Binding Text, RelativeSource={RelativeSource Self}}\"", statusTag);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", statusTag);
    }

    [Fact]
    public void HistoryReindexRefreshFailuresLeaveDurableStatus()
    {
        var actionsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.Actions.cs"));

        var reindexBlock = GetMethodBlock(actionsCode, "private void ReindexAllBtn_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("try", reindexBlock);
        Assert.Contains("if (!ReindexAllBtn.IsEnabled)", reindexBlock);
        Assert.Contains("ReindexAllBtn.IsEnabled = false;", reindexBlock);
        Assert.Contains("ReindexAllBtn.Content = \"Starting index...\";", reindexBlock);
        Assert.Contains("ReindexAllProgressPanel.Visibility = Visibility.Visible;", reindexBlock);
        Assert.Contains("ReindexAllProgressBar.Visibility = Visibility.Visible;", reindexBlock);
        Assert.Contains("HistorySearchStatusText.Text = \"Starting image index refresh...\";", reindexBlock);
        Assert.Contains("_imageSearchIndexService.RequestSync(_historyService.ImageEntries, _settingsService.Settings.OcrLanguageTag);", reindexBlock);
        Assert.Contains("UpdateImageSearchStatus();", reindexBlock);
        Assert.Contains("QueueImageIndexRefresh();", reindexBlock);
        Assert.Contains("catch (Exception ex)", reindexBlock);
        Assert.Contains("AppDiagnostics.LogError(\"settings.history-reindex-refresh\", ex);", reindexBlock);
        Assert.Contains("SetImageSearchLoading(false, forceIndexed: true);", reindexBlock);
        Assert.Contains("HistorySearchStatusText.Text = \"Index refresh failed. Existing search data is still available.\";", reindexBlock);
        Assert.Contains("UpdateImageSearchActionButtons();", reindexBlock);
        Assert.Contains("CyberSnap could not refresh the image search index. Existing search data is still available; try again from History.", reindexBlock);
    }

    [Fact]
    public void HistoryToolbarActionsHaveAccessibleLabels()
    {
        var xaml = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "HistoryWindow.xaml"));
        var historyCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "History", "HistoryWindow.History.cs"));

        AssertSettingsActionButton(xaml, "SelectBtn", "Select history items", "Select history items", "ToggleSelectMode");
        AssertSettingsActionButton(xaml, "DeleteAllBtn", "Clear current history tab", "Delete all items in the current history tab", "DeleteAllClick");
        AssertSettingsActionButton(xaml, "DeleteSelectedBtn", "Delete selected history items", "Delete selected history items", "DeleteSelectedClick");
        AssertNamedControlHasLabel(xaml, "SelectBtn", "<Button", "Select history items", "Select history items", "Select history items");
        AssertNamedControlHasLabel(xaml, "DeleteAllBtn", "<Button", "Clear current history tab", "Delete all items in the current history tab", "Delete all items in the current history tab");
        AssertNamedControlHasLabel(xaml, "DeleteSelectedBtn", "<Button", "Delete selected history items", "Delete selected history items", "Delete selected history items");
        AssertSettingsActionButton(xaml, "ReindexAllBtn", "Refresh image search index", "Refresh the image search index", "ReindexAllBtn_Click");
        AssertSettingsActionButton(xaml, "ImageSearchFiltersBtn", "Image search filters", "Choose image search sources and exact matching", "ImageSearchFiltersBtn_Click");
        AssertSettingsActionButton(xaml, "HistoryEmptyRetryButton", "Retry loading history", "Retry loading history", "HistoryEmptyRetryButton_Click");

        var actionBlock = GetMethodBlock(historyCode, "private void UpdateHistoryActionButtons()");
        Assert.Contains("var categoryLabel = GetCurrentHistoryCategoryLabel(2);", actionBlock);
        Assert.Contains("var totalCategoryLabel = GetCurrentHistoryCategoryLabel(totalCount);", actionBlock);
        Assert.Contains("var selectedCategoryLabel = GetCurrentHistoryCategoryLabel(selectedCount);", actionBlock);
        Assert.Contains("var selectHelp = _selectMode ? $\"Finish selecting {categoryLabel}\" : $\"Select {categoryLabel}\";", actionBlock);
        Assert.Contains("var selectName = _selectMode ? $\"Finish selecting {categoryLabel}\" : $\"Select {categoryLabel}\";", actionBlock);
        Assert.Contains("var deleteAllName = totalCount > 0", actionBlock);
        Assert.Contains("var deleteSelectedName = selectedCount > 0", actionBlock);
        Assert.Contains("$\"Delete all {totalCount} {totalCategoryLabel} in the current history category\"", actionBlock);
        Assert.Contains("$\"Delete {selectedCount} selected {selectedCategoryLabel}\"", actionBlock);
        Assert.Contains("DeleteAllBtn.ToolTip = deleteAllHelp;", actionBlock);
        Assert.Contains("DeleteSelectedBtn.ToolTip = deleteSelectedHelp;", actionBlock);
        Assert.Contains("AutomationProperties.SetName(SelectBtn, selectName);", actionBlock);
        Assert.Contains("AutomationProperties.SetName(DeleteAllBtn, deleteAllName);", actionBlock);
        Assert.Contains("AutomationProperties.SetName(DeleteSelectedBtn, deleteSelectedName);", actionBlock);
        Assert.Contains("AutomationProperties.SetHelpText(SelectBtn, selectHelp);", actionBlock);
        Assert.Contains("AutomationProperties.SetHelpText(DeleteAllBtn, deleteAllHelp);", actionBlock);
        Assert.Contains("AutomationProperties.SetHelpText(DeleteSelectedBtn, deleteSelectedHelp);", actionBlock);
    }

    [Fact]
    public void SetupWizardHotkeysRollBackAndReportSaveFailures()
    {
        var wizardCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SetupWizard.xaml.cs"));

        var hotkeyBlock = GetMethodBlock(wizardCode, "private void WireHotkey(TextBox box, string toolId)");
        Assert.Contains("var previous = _settingsService.Settings.GetToolHotkey(toolId);", hotkeyBlock);
        Assert.Contains("_settingsService.Settings.SetToolHotkey(toolId, mod, vk);", hotkeyBlock);
        Assert.Contains("_settingsService.Save();", hotkeyBlock);
        Assert.Contains("AppDiagnostics.LogError(\"setup.tool-hotkey\", ex);", hotkeyBlock);
        Assert.Contains("_settingsService.Settings.SetToolHotkey(toolId, previous.mod, previous.key);", hotkeyBlock);
        Assert.Contains("AppDiagnostics.LogError(\"setup.tool-hotkey-rollback\", rollbackEx);", hotkeyBlock);
        Assert.Contains("box.Text = HotkeyFormatter.Format(previous.mod, previous.key);", hotkeyBlock);
        Assert.Contains("ShowSetupHotkeySaveFailed(ex);", hotkeyBlock);
        Assert.DoesNotContain("Check Settings -> Tools and try again.", hotkeyBlock);
        Assert.Contains("finally", hotkeyBlock);
        Assert.Contains("recording = false;", hotkeyBlock);
        Assert.Contains("Keyboard.ClearFocus();", hotkeyBlock);

        var failureBlock = GetMethodBlock(wizardCode, "private static void ShowSetupHotkeySaveFailed(Exception ex)");
        Assert.Contains("ToastWindow.ShowError(", failureBlock);
        Assert.Contains("\"Hotkey failed\"", failureBlock);
        Assert.Contains("The previous hotkey was restored.", failureBlock);
        Assert.Contains("Try this setup step again", failureBlock);
        Assert.Contains("change it later in Settings -> Tools", failureBlock);
    }

    [Fact]
    public void SetupWizardPageSavesBlockNavigationAndCloseOnFailure()
    {
        var wizardCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SetupWizard.xaml.cs"));

        var goToPageBlock = GetMethodBlock(wizardCode, "private void GoToPage(int page)");
        Assert.Contains("if (!SaveCurrentPage())", goToPageBlock);
        Assert.Contains("return;", goToPageBlock);
        Assert.Contains("_page = page;", goToPageBlock);

        var saveBlock = GetMethodBlock(wizardCode, "private bool SaveCurrentPage()");
        Assert.Contains("try", saveBlock);
        Assert.Contains("_settingsService.Save();", saveBlock);
        Assert.Contains("var previousCapture = (", saveBlock);
        Assert.Contains("s.ShowCrosshairGuides = previousCapture.ShowCrosshairGuides;", saveBlock);
        Assert.Contains("s.ShowCaptureMagnifier = previousCapture.ShowCaptureMagnifier;", saveBlock);
        Assert.Contains("s.MuteSounds = previousCapture.MuteSounds;", saveBlock);
        Assert.Contains("s.SaveToFile = previousCapture.SaveToFile;", saveBlock);
        Assert.Contains("s.CaptureImageFormat = previousCapture.CaptureImageFormat;", saveBlock);
        Assert.Contains("s.CaptureMaxLongEdge = previousCapture.CaptureMaxLongEdge;", saveBlock);
        Assert.Contains("LoadDefaults();", saveBlock);
        Assert.Contains("var previousCompleted = s.HasCompletedSetup;", saveBlock);
        Assert.Contains("s.HasCompletedSetup = previousCompleted;", saveBlock);
        Assert.Contains("return true;", saveBlock);
        Assert.Contains("catch (Exception ex)", saveBlock);
        Assert.Contains("ShowSetupSaveFailed(\"setup.save-page\", ex);", saveBlock);
        Assert.Contains("return false;", saveBlock);

        var nextBlock = GetMethodBlock(wizardCode, "private void Next_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!SaveCurrentPage())", nextBlock);
        Assert.Contains("return;", nextBlock);
        Assert.Contains("DialogResult = true;", nextBlock);

        var openSettingsBlock = GetMethodBlock(wizardCode, "private void OpenSettings_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!SaveCurrentPage() || !MarkSetupCompleted())", openSettingsBlock);
        Assert.Contains("return;", openSettingsBlock);
        Assert.Contains("Tag = \"OpenSettings\";", openSettingsBlock);

        var skipBlock = GetMethodBlock(wizardCode, "private void Skip_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("if (!SaveCurrentPage() || !MarkSetupCompleted())", skipBlock);
        Assert.Contains("return;", skipBlock);
        Assert.Contains("DialogResult = false;", skipBlock);

        var completeBlock = GetMethodBlock(wizardCode, "private bool MarkSetupCompleted()");
        Assert.Contains("var previous = _settingsService.Settings.HasCompletedSetup;", completeBlock);
        Assert.Contains("_settingsService.Settings.HasCompletedSetup = true;", completeBlock);
        Assert.Contains("ShowSetupSaveFailed(\"setup.complete\", ex);", completeBlock);
        Assert.Contains("_settingsService.Settings.HasCompletedSetup = previous;", completeBlock);

        var failureBlock = GetMethodBlock(wizardCode, "private static void ShowSetupSaveFailed(string diagnosticKey, Exception ex)");
        Assert.Contains("AppDiagnostics.LogError(diagnosticKey, ex);", failureBlock);
        Assert.Contains("diagnosticKey switch", failureBlock);
        Assert.Contains("ToastWindow.ShowError(", failureBlock);
        Assert.Contains("\"Setup save failed\"", failureBlock);
        Assert.Contains("\"Setup completion failed\"", failureBlock);
        Assert.Contains("Your setup choices were not saved.", failureBlock);
        Assert.Contains("Previous saved settings were restored.", failureBlock);
        Assert.Contains("Stay on this step and try again", failureBlock);
        Assert.Contains("finish setup later from Settings", failureBlock);
        Assert.Contains("Setup was not marked complete.", failureBlock);
        Assert.Contains("The previous setup status was restored.", failureBlock);
    }

    [Fact]
    public void SetupWizardSaveDirectoryBrowsePersistsOrRestoresSelection()
    {
        var wizardCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SetupWizard.xaml.cs"));

        var browseBlock = GetMethodBlock(wizardCode, "private void BrowseSaveDir_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("var previous = _settingsService.Settings.SaveDirectory;", browseBlock);
        Assert.Contains("_settingsService.Settings.SaveDirectory = dlg.SelectedPath;", browseBlock);
        Assert.Contains("_settingsService.Save();", browseBlock);
        Assert.Contains("WizSaveDirText.Text = dlg.SelectedPath;", browseBlock);
        Assert.Contains("AppDiagnostics.LogError(\"setup.save-directory\", ex);", browseBlock);
        Assert.Contains("_settingsService.Settings.SaveDirectory = previous;", browseBlock);
        Assert.Contains("AppDiagnostics.LogError(\"setup.save-directory-rollback\", rollbackEx);", browseBlock);
        Assert.Contains("WizSaveDirText.Text = previous;", browseBlock);
        Assert.Contains("ToastWindow.ShowError(", browseBlock);
        Assert.Contains("\"Save directory failed\"", browseBlock);
        Assert.Contains("The previous save directory was restored. Stay on this setup step and try again.", browseBlock);
    }

    private static void AssertUpdateActionButtonHasAccessibleLabel(string xaml, string buttonName, string automationName, string toolTip)
    {
        var buttonIndex = xaml.IndexOf($"x:Name=\"{buttonName}\"", StringComparison.Ordinal);
        Assert.True(buttonIndex >= 0, $"Could not find {buttonName}.");

        var tag = GetOpeningTag(xaml, buttonIndex, "<Button");
        Assert.Contains($"AutomationProperties.Name=\"{automationName}\"", tag);
        Assert.Contains($"ToolTip=\"{toolTip}\"", tag);
        Assert.Contains("Cursor=\"Hand\"", tag);
    }

    private static void AssertDynamicStatusTextBlock(string xaml, string textBlockName, string automationName, bool isLive)
    {
        var index = xaml.IndexOf($"x:Name=\"{textBlockName}\"", StringComparison.Ordinal);
        Assert.True(index >= 0, $"Could not find {textBlockName}.");

        var tag = GetOpeningTag(xaml, index, "<TextBlock");
        Assert.Contains("ToolTip=\"{Binding Text, RelativeSource={RelativeSource Self}}\"", tag);
        Assert.Contains($"AutomationProperties.Name=\"{automationName}\"", tag);
        if (isLive)
            Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", tag);
        else
            Assert.DoesNotContain("AutomationProperties.LiveSetting=", tag);
    }

    private static void AssertLocalModelActionRowWraps(string xaml, string firstButtonName, string lastButtonName)
    {
        var firstIndex = xaml.IndexOf($"x:Name=\"{firstButtonName}\"", StringComparison.Ordinal);
        var lastIndex = xaml.IndexOf($"x:Name=\"{lastButtonName}\"", StringComparison.Ordinal);
        Assert.True(firstIndex >= 0, $"Could not find {firstButtonName}.");
        Assert.True(lastIndex > firstIndex, $"Could not find {lastButtonName} after {firstButtonName}.");

        var wrapStart = xaml.LastIndexOf("<WrapPanel", firstIndex, StringComparison.Ordinal);
        var wrapEnd = xaml.IndexOf("</WrapPanel>", lastIndex, StringComparison.Ordinal);
        Assert.True(wrapStart >= 0, $"Could not find wrapping action row for {firstButtonName}.");
        Assert.True(wrapEnd > lastIndex, $"Could not find wrapping action row end for {lastButtonName}.");

        var row = xaml[wrapStart..wrapEnd];
        Assert.Contains($"x:Name=\"{firstButtonName}\"", row);
        Assert.Contains($"x:Name=\"{lastButtonName}\"", row);
        Assert.Contains("<WrapPanel Margin=\"0,10,0,-8\">", row);
        Assert.Contains("Margin=\"0,0,8,8\"", row);
        Assert.DoesNotContain("Margin=\"8,0,0,0\"", row);
        Assert.DoesNotContain("<StackPanel Orientation=\"Horizontal\"", row);
    }

    private static void AssertLocalModelSelectorUsesResponsiveWidth(string xaml, string comboName)
    {
        AssertSettingsSelectorUsesResponsiveWidth(xaml, comboName, "140", "240");
    }

    private static void AssertSettingsSelectorUsesResponsiveWidth(string xaml, string comboName, string minWidth, string maxWidth)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{comboName}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Could not find {comboName}.");

        var tag = GetOpeningTag(xaml, nameIndex, "<ComboBox");
        Assert.Contains($"MinWidth=\"{minWidth}\"", tag);
        Assert.Contains($"MaxWidth=\"{maxWidth}\"", tag);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", tag);
        Assert.DoesNotContain(" Width=\"", tag);
    }

    private static void AssertUploadProviderTextBoxUsesResponsiveWidth(string xaml, string textBoxName)
    {
        AssertUploadProviderTextBoxUsesResponsiveWidth(xaml, textBoxName, "145", "360");
    }

    private static void AssertUploadProviderTextBoxUsesResponsiveWidth(string xaml, string textBoxName, string minWidth, string maxWidth)
    {
        AssertTextBoxUsesResponsiveWidth(xaml, textBoxName, minWidth, maxWidth);

        var nameIndex = xaml.IndexOf($"x:Name=\"{textBoxName}\"", StringComparison.Ordinal);
        var tag = GetOpeningTag(xaml, nameIndex, "<TextBox");
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", tag);
    }

    private static void AssertUploadProviderPasswordBoxUsesResponsiveWidth(string xaml, string passwordBoxName)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{passwordBoxName}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Could not find {passwordBoxName}.");

        var tag = GetOpeningTag(xaml, nameIndex, "<PasswordBox");
        Assert.Contains("MinWidth=\"145\"", tag);
        Assert.Contains("MaxWidth=\"360\"", tag);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", tag);
        Assert.Contains("AutomationProperties.HelpText=\"Hidden for safety. Paste a new value to replace it.\"", tag);
        Assert.DoesNotContain(" Width=\"", tag);
    }

    private static void AssertTextBoxWrapsLongMultilineValues(string xaml, string textBoxName)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{textBoxName}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Could not find {textBoxName}.");

        var tag = GetOpeningTag(xaml, nameIndex, "<TextBox");
        Assert.Contains("AcceptsReturn=\"True\"", tag);
        Assert.Contains("TextWrapping=\"Wrap\"", tag);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", tag);
        Assert.DoesNotContain("HorizontalScrollBarVisibility=\"Auto\"", tag);
    }

    private static void AssertTextBoxUsesResponsiveWidth(string xaml, string textBoxName, string minWidth, string maxWidth)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{textBoxName}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Could not find {textBoxName}.");

        var tag = GetOpeningTag(xaml, nameIndex, "<TextBox");
        Assert.Contains($"MinWidth=\"{minWidth}\"", tag);
        Assert.Contains($"MaxWidth=\"{maxWidth}\"", tag);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", tag);
        Assert.DoesNotContain(" Width=\"", tag);
    }

    private static void AssertUploadProviderTextBoxUsesResponsiveWidth(
        string xaml,
        string textBoxName,
        string minWidth = "145",
        string maxWidth = "360",
        bool requireHorizontalScroll = true)
    {
        AssertTextBoxUsesResponsiveWidth(xaml, textBoxName, minWidth, maxWidth);

        var nameIndex = xaml.IndexOf($"x:Name=\"{textBoxName}\"", StringComparison.Ordinal);
        var tag = GetOpeningTag(xaml, nameIndex, "<TextBox");
        if (requireHorizontalScroll)
            Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", tag);
    }

    private static void AssertSupportActionRowKeyboardAccessible(string xaml, string name, string automationName, string keyDownHandler)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Could not find {name}.");

        var tag = GetOpeningTag(xaml, nameIndex, "<Border");
        Assert.Contains("Focusable=\"True\"", tag);
        Assert.Contains($"AutomationProperties.Name=\"{automationName}\"", tag);
        Assert.Contains($"KeyDown=\"{keyDownHandler}\"", tag);
    }

    private static void AssertSettingsActionButton(string xaml, string name, string automationName, string tooltip, string clickHandler)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Could not find {name}.");

        var tag = GetOpeningTag(xaml, nameIndex, "<Button");
        Assert.Contains($"AutomationProperties.Name=\"{automationName}\"", tag);
        Assert.Contains($"ToolTip=\"{tooltip}\"", tag);
        Assert.Contains("Cursor=\"Hand\"", tag);
        Assert.Contains($"Click=\"{clickHandler}\"", tag);
    }

    private static void AssertNamedControlHasLabel(string xaml, string name, string tagName, string automationName, string tooltip, string? helpText = null)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Could not find {name}.");

        var tag = GetOpeningTag(xaml, nameIndex, tagName);
        Assert.Contains($"AutomationProperties.Name=\"{automationName}\"", tag);
        Assert.Contains($"ToolTip=\"{tooltip}\"", tag);
        if (helpText is not null)
            Assert.Contains($"AutomationProperties.HelpText=\"{helpText}\"", tag);
    }

    private static void AssertComboBoxItemHasLabel(string xaml, string content, string tooltip, string automationName, string helpText)
    {
        var itemIndex = xaml.IndexOf($"Content=\"{content}\"", StringComparison.Ordinal);
        Assert.True(itemIndex >= 0, $"Could not find ComboBoxItem {content}.");

        var tag = GetOpeningTag(xaml, itemIndex, "<ComboBoxItem");
        Assert.Contains($"ToolTip=\"{tooltip}\"", tag);
        Assert.Contains($"AutomationProperties.Name=\"{automationName}\"", tag);
        Assert.Contains($"AutomationProperties.HelpText=\"{helpText}\"", tag);
    }

    private static void AssertComboBoxItemInNamedComboHasLabel(string xaml, string comboName, string content, string tooltip, string automationName, string helpText)
    {
        var comboIndex = xaml.IndexOf($"x:Name=\"{comboName}\"", StringComparison.Ordinal);
        Assert.True(comboIndex >= 0, $"Could not find {comboName}.");

        var comboEnd = xaml.IndexOf("</ComboBox>", comboIndex, StringComparison.Ordinal);
        Assert.True(comboEnd > comboIndex, $"Could not find {comboName} closing tag.");

        var itemIndex = xaml.IndexOf($"Content=\"{content}\"", comboIndex, comboEnd - comboIndex, StringComparison.Ordinal);
        Assert.True(itemIndex >= 0, $"Could not find ComboBoxItem {content} in {comboName}.");

        var tag = GetOpeningTag(xaml, itemIndex, "<ComboBoxItem");
        Assert.Contains($"ToolTip=\"{tooltip}\"", tag);
        Assert.Contains($"AutomationProperties.Name=\"{automationName}\"", tag);
        Assert.Contains($"AutomationProperties.HelpText=\"{helpText}\"", tag);
    }

    [Fact]
    public void TranslationRuntimeActionsLockImmediatelyUntilStatusRefresh()
    {
        var settingsCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "SettingsWindow.xaml.cs"));
        var ocrCode = File.ReadAllText(RepoPath("src", "CyberSnap", "UI", "Settings", "SettingsWindow.Ocr.cs"));
        var openSourceClickBlock = GetMethodBlock(ocrCode, "private void OpenSourceLocalInstallBtn_Click(object sender, RoutedEventArgs e)");
        var argosClickBlock = GetMethodBlock(ocrCode, "private void ArgosInstallBtn_Click(object sender, RoutedEventArgs e)");

        Assert.Contains("private bool _openSourceTranslationRuntimeActionInProgress;", settingsCode);
        Assert.Contains("if (_openSourceTranslationRuntimeActionInProgress)", ocrCode);
        Assert.Contains("_openSourceTranslationRuntimeActionInProgress = true;", ocrCode);
        Assert.Contains("SetOpenSourceTranslationRuntimeBusy(startingStatus, isUninstall);", ocrCode);
        Assert.Contains("OpenSourceLocalInstallBtn.IsEnabled = false;", ocrCode);
        Assert.Contains("_openSourceTranslationRuntimeActionInProgress = false;", ocrCode);
        Assert.Contains("await RefreshOpenSourceTranslationRuntimeStatusAsync();", ocrCode);
        Assert.Contains("if (isUninstall && !ThemedConfirmDialog.Confirm(", openSourceClickBlock);
        Assert.Contains("\"Uninstall open-source local translation\"", openSourceClickBlock);
        Assert.Contains("Open-source local uninstall canceled. Runtime was left installed.", openSourceClickBlock);
        Assert.Contains("OpenSourceLocalProgressBar.Visibility = Visibility.Collapsed;", openSourceClickBlock);
        Assert.Contains("OpenSourceLocalInstallBtn.Content = \"Uninstall\";", openSourceClickBlock);
        Assert.Contains("OpenSourceLocalInstallBtn.IsEnabled = true;", openSourceClickBlock);

        Assert.Contains("private bool _argosTranslationRuntimeActionInProgress;", settingsCode);
        Assert.Contains("if (_argosTranslationRuntimeActionInProgress)", ocrCode);
        Assert.Contains("_argosTranslationRuntimeActionInProgress = true;", ocrCode);
        Assert.Contains("SetArgosTranslationRuntimeBusy(startingStatus, isUninstall);", ocrCode);
        Assert.Contains("ArgosInstallBtn.IsEnabled = false;", ocrCode);
        Assert.Contains("_argosTranslationRuntimeActionInProgress = false;", ocrCode);
        Assert.Contains("await RefreshArgosTranslationRuntimeStatusAsync();", ocrCode);
        Assert.Contains("if (isUninstall && !ThemedConfirmDialog.Confirm(", argosClickBlock);
        Assert.Contains("\"Uninstall Argos Translate\"", argosClickBlock);
        Assert.Contains("Argos uninstall canceled. Runtime was left installed.", argosClickBlock);
        Assert.Contains("ArgosProgressBar.Visibility = Visibility.Collapsed;", argosClickBlock);
        Assert.Contains("ArgosInstallBtn.Content = \"Uninstall\";", argosClickBlock);
        Assert.Contains("ArgosInstallBtn.IsEnabled = true;", argosClickBlock);
        Assert.Contains("if (!started)", openSourceClickBlock);
        Assert.Contains("ToastWindow.Show(\"Open-source local\", \"That setup is already running in the background.\");", openSourceClickBlock);
        Assert.Contains("if (!started)", argosClickBlock);
        Assert.Contains("ToastWindow.Show(\"Argos Translate\", \"That setup is already running in the background.\");", argosClickBlock);

        var openSourceStartIndex = openSourceClickBlock.IndexOf("var started = BackgroundRuntimeJobService.Start(", StringComparison.Ordinal);
        var openSourceBusyIndex = openSourceClickBlock.IndexOf("SetOpenSourceTranslationRuntimeBusy(startingStatus, isUninstall);", StringComparison.Ordinal);
        Assert.True(openSourceBusyIndex > openSourceStartIndex, "Open-source translation should only show the optimistic busy state after Start accepts the job.");

        var argosStartIndex = argosClickBlock.IndexOf("var started = BackgroundRuntimeJobService.Start(", StringComparison.Ordinal);
        var argosBusyIndex = argosClickBlock.IndexOf("SetArgosTranslationRuntimeBusy(startingStatus, isUninstall);", StringComparison.Ordinal);
        Assert.True(argosBusyIndex > argosStartIndex, "Argos should only show the optimistic busy state after Start accepts the job.");

        var openSourceRefreshStart = ocrCode.IndexOf("private async Task RefreshOpenSourceTranslationRuntimeStatusAsync()", StringComparison.Ordinal);
        var argosRefreshStart = ocrCode.IndexOf("private async Task RefreshArgosTranslationRuntimeStatusAsync()", StringComparison.Ordinal);
        var refreshFailureHelperStart = ocrCode.IndexOf("private void SetOpenSourceTranslationRuntimeStatusRefreshFailed(string message)", StringComparison.Ordinal);
        Assert.True(openSourceRefreshStart >= 0, "Could not find open-source translation runtime refresh.");
        Assert.True(argosRefreshStart > openSourceRefreshStart, "Could not find Argos translation runtime refresh after open-source refresh.");
        Assert.True(refreshFailureHelperStart > argosRefreshStart, "Could not find translation runtime refresh-failure helper after refresh blocks.");

        var openSourceRefresh = ocrCode[openSourceRefreshStart..argosRefreshStart];
        Assert.Contains("AppDiagnostics.LogError(\"settings.ocr.check-open-source-status\", ex);", openSourceRefresh);
        Assert.Contains("SetOpenSourceTranslationRuntimeStatusRefreshFailed(ex.Message);", openSourceRefresh);
        Assert.Contains("FormatRuntimeReadinessStatus(_openSourceLocalInstalled, \"Installed\", openSourceJob, \"Open-source local\");", openSourceRefresh);
        Assert.DoesNotContain("OpenSourceLocalStatusText.Text = \"Python not found\";", openSourceRefresh);
        Assert.DoesNotContain("OpenSourceLocalStatusText.Text = $\"Failed: {FormatRuntimeStatus(openSourceJob.LastError)}\";", openSourceRefresh);
        Assert.DoesNotContain("_argosTranslationRuntimeActionInProgress = false;", openSourceRefresh);

        var argosRefresh = ocrCode[argosRefreshStart..refreshFailureHelperStart];
        Assert.Contains("AppDiagnostics.LogError(\"settings.ocr.check-argos-status\", ex);", argosRefresh);
        Assert.Contains("SetArgosTranslationRuntimeStatusRefreshFailed(ex.Message);", argosRefresh);
        Assert.Contains("FormatRuntimeReadinessStatus(_argosInstalled, \"Installed\", argosJob, \"Argos Translate\");", argosRefresh);
        Assert.DoesNotContain("ArgosStatusText.Text = \"Python not found\";", argosRefresh);
        Assert.DoesNotContain("ArgosStatusText.Text = $\"Failed: {FormatRuntimeStatus(argosJob.LastError)}\";", argosRefresh);
        Assert.DoesNotContain("_openSourceTranslationRuntimeActionInProgress = false;", argosRefresh);

        Assert.Contains("FormatRuntimeActionFailedStatus(openSourceJob.LastError, \"Open-source local\");", ocrCode);
        Assert.Contains("FormatRuntimeActionFailedStatus(argosJob.LastError, \"Argos Translate\");", ocrCode);

        var readinessFormatter = GetMethodBlock(ocrCode, "private static string FormatRuntimeReadinessStatus(bool isInstalled, string installedStatus, BackgroundRuntimeJobSnapshot? lastJob, string runtimeName)");
        Assert.Contains("lastJob is { LastSucceeded: false }", readinessFormatter);
        Assert.Contains("FormatRuntimeActionFailedStatus(lastJob.LastError, runtimeName)", readinessFormatter);
        Assert.Contains("if (isInstalled)", readinessFormatter);
        Assert.Contains("return installedStatus;", readinessFormatter);
        Assert.DoesNotContain("$\"{installedStatus}; {FormatRuntimeActionFailedStatus(lastJob.LastError)}\"", readinessFormatter);
        Assert.DoesNotContain("var failure = FormatRuntimeStatus(lastJob.LastError);", readinessFormatter);
        Assert.DoesNotContain("last action failed: {failure}", readinessFormatter);
        Assert.DoesNotContain("Failed: {failure}", readinessFormatter);
        Assert.Contains("return \"Not installed\";", readinessFormatter);
    }

    private static void AssertPasswordBox(string xaml, string name, string automationName)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Could not find {name}.");

        var tag = GetOpeningTag(xaml, nameIndex, "<PasswordBox");
        Assert.Contains($"AutomationProperties.Name=\"{automationName}\"", tag);
        Assert.Contains("PasswordChanged=", tag);
        Assert.DoesNotContain("TextChanged=", tag);
    }

    private static void AssertSettingsPageAllowsHorizontalOverflow(string xaml, string name)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Could not find {name}.");

        var tag = GetOpeningTag(xaml, nameIndex, "<ScrollViewer");
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", tag);
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", tag);
    }

    private static void AssertSettingsPageDisablesHorizontalOverflow(string xaml, string name)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Could not find {name}.");

        var tag = GetOpeningTag(xaml, nameIndex, "<ScrollViewer");
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", tag);
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", tag);
    }

    private static void AssertTextBlockUsesStyle(string xaml, string text, string styleKey)
    {
        var textIndex = xaml.IndexOf($"Text=\"{text}\"", StringComparison.Ordinal);
        Assert.True(textIndex >= 0, $"Could not find helper text: {text}");

        var tag = GetOpeningTag(xaml, textIndex, "<TextBlock");
        Assert.Contains($"Style=\"{{StaticResource {styleKey}}}\"", tag);
    }

    private static void AssertNamedTextBlockUsesStyle(string xaml, string name, string styleKey)
    {
        var nameIndex = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Could not find {name}.");

        var tag = GetOpeningTag(xaml, nameIndex, "<TextBlock");
        Assert.Contains($"Style=\"{{StaticResource {styleKey}}}\"", tag);
    }

    private static string GetOpeningTag(string xaml, int attributeIndex, string tagName)
    {
        var start = xaml.LastIndexOf(tagName, attributeIndex, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find opening tag before index {attributeIndex}.");

        var end = xaml.IndexOf('>', attributeIndex);
        Assert.True(end > start, $"Could not read opening tag at index {attributeIndex}.");

        return xaml[start..end];
    }

    private static string GetBlockStartingAt(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find block marker: {marker}");

        var end = source.IndexOf("return;", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find return after block marker: {marker}");

        return source[start..end];
    }

    private static string GetXamlElementBlock(string source, string openingMarker, string closingMarker)
    {
        var start = source.IndexOf(openingMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find XAML block marker: {openingMarker}");

        var end = source.IndexOf(closingMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find XAML block close: {closingMarker}");

        return source[start..(end + closingMarker.Length)];
    }

    private static void AssertSearchFailureStatusWrittenAfterLoadingStops(string methodBlock)
    {
        var catchIndex = methodBlock.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(catchIndex >= 0, "Could not find search failure catch block.");

        var loadingIndex = methodBlock.IndexOf("SetImageSearchLoading(false, forceIndexed: true);", catchIndex, StringComparison.Ordinal);
        var statusIndex = methodBlock.IndexOf("HistorySearchStatusText.Text = \"Search failed. Edit the query or retry from History.\";", catchIndex, StringComparison.Ordinal);
        var toastIndex = methodBlock.IndexOf("CyberSnap could not update history search. Edit the query or retry from History.", catchIndex, StringComparison.Ordinal);
        Assert.True(loadingIndex > catchIndex, "Search failure should stop loading in the catch block.");
        Assert.True(statusIndex > loadingIndex, "Search failure status should be written after loading stops so status refresh does not clear it.");
        Assert.True(toastIndex > statusIndex, "Search failure toast should be shown after the visible status is set.");
    }

    private static void AssertSearchCallbackFailureStopsLoadingThenSetsStatus(string methodBlock)
    {
        var catchIndex = methodBlock.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(catchIndex >= 0, "Could not find search callback failure catch block.");

        var loadingIndex = methodBlock.IndexOf("SetImageSearchLoading(false, forceIndexed: true);", catchIndex, StringComparison.Ordinal);
        var statusIndex = methodBlock.IndexOf("HistorySearchStatusText.Text = \"Search failed\";", catchIndex, StringComparison.Ordinal);
        Assert.True(loadingIndex > catchIndex, "Search callback failure should stop loading in the catch block.");
        Assert.True(statusIndex > loadingIndex, "Search callback failure status should be written after loading stops so status refresh does not clear it.");
    }

    private static string GetMethodBlock(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method: {signature}");

        var bodyStart = source.IndexOf('{', start);
        Assert.True(bodyStart > start, $"Could not find method body: {signature}");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[start..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Could not read method body: {signature}");
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string RepoPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find repo file: {Path.Combine(parts)}");
    }
}
