using System.IO;
using System.Text.Json;
using CyberSnap.Models;

namespace CyberSnap.Services;

public sealed class SettingsService : IDisposable
{
    private static readonly string LegacySettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CyberSnap", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly object CacheGate = new();
    private static string? s_cachedPath;
    private static AppSettings? s_cachedSettings;

    private readonly string _settingsPath;
    private readonly string _settingsDir;
    private readonly TimeSpan _saveDelay;
    private readonly System.Threading.Timer _flushTimer;
    private readonly object _gate = new();
    private bool _settingsDirty;
    private bool _disposed;

    public AppSettings Settings { get; internal set; } = new();
    public event Action<string>? SaveFailed;

    public static event Action? AutoCopyPreferencesChanged;
    public static event Action<Helpers.WidgetPreferences>? WidgetPreferencesChanged;
    public static event Action? SettingsChanged;

    public static void PublishWidgetPreferences(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var preferences = Helpers.WidgetPreferences.FromSettings(settings);
        lock (CacheGate)
        {
            if (s_cachedSettings is not null)
                preferences.ApplyTo(s_cachedSettings);
        }
        WidgetPreferencesChanged?.Invoke(preferences);
    }

    public static void SetAutoCopy(Helpers.AutoCopyKind kind, bool enabled) =>
        MutateResultDelivery(settings => Helpers.ResultDelivery.SetCopyEnabled(settings, kind, enabled),
            "settings.auto-copy.save");

    public static void SetOcrShowResultWindow(bool show) =>
        MutateResultDelivery(settings => Helpers.ResultDelivery.SetShowWindow(settings, Helpers.AutoCopyKind.Ocr, show),
            "settings.ocr-show-window.save");

    public static void SetScanShowResultWindow(bool show) =>
        MutateResultDelivery(settings => Helpers.ResultDelivery.SetShowWindow(settings, Helpers.AutoCopyKind.Scan, show),
            "settings.scan-show-window.save");

    private static void MutateResultDelivery(Action<AppSettings> mutate, string errorCode)
    {
        try
        {
            using var service = new SettingsService();
            service.Settings = LoadStatic() ?? service.Settings;
            mutate(service.Settings);
            service.Save();
            PublishAutoCopyPreferences(service.Settings);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError(errorCode, ex);
        }
    }

    /// <summary>Publish independent clipboard/window preferences to open tools and Settings.</summary>
    public static void PublishAutoCopyPreferences(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (CacheGate)
        {
            if (s_cachedSettings is not null)
                CopyResultDeliveryPreferences(settings, s_cachedSettings);
        }
        AutoCopyPreferencesChanged?.Invoke();
        SettingsChanged?.Invoke();
    }

    private static void CopyResultDeliveryPreferences(AppSettings source, AppSettings target)
    {
        target.ImageAutoCopyToClipboard = source.ImageAutoCopyToClipboard;
        target.ScrollAutoCopyToClipboard = source.ScrollAutoCopyToClipboard;
        target.OcrAutoCopyToClipboard = source.OcrAutoCopyToClipboard;
        target.ScanAutoCopyToClipboard = source.ScanAutoCopyToClipboard;
        target.VideoAutoCopyToClipboard = source.VideoAutoCopyToClipboard;
        target.GifAutoCopyToClipboard = source.GifAutoCopyToClipboard;
        target.ColorDetailAutoCopy = source.ColorDetailAutoCopy;
        target.ColorDetailCopyFormat = source.ColorDetailCopyFormat;
        target.ColorDetailIncludeHash = source.ColorDetailIncludeHash;
        target.OcrShowResultWindow = source.OcrShowResultWindow;
        target.ScanShowResultWindow = source.ScanShowResultWindow;
        target.ShowColorDetailWindow = source.ShowColorDetailWindow;
        target.ShowCapturePreview = source.ShowCapturePreview;
        target.ShowScrollCapturePreview = source.ShowScrollCapturePreview;
        target.OpenVideoTrimmerAfterCapture = source.OpenVideoTrimmerAfterCapture;
        target.OpenGifTrimmerAfterCapture = source.OpenGifTrimmerAfterCapture;
        // The outcome editors normalize clipboard/window choices against these
        // destinations. A stale save/share flag could suppress the last-output
        // fallback or restore an older destination on the next edit.
        target.SaveToFile = source.SaveToFile;
        target.AutoShareAfterCapture = source.AutoShareAfterCapture;
        target.SaveVideoToFile = source.SaveVideoToFile;
        target.SaveGifToFile = source.SaveGifToFile;
        target.ShowVideoRecordingNotification = source.ShowVideoRecordingNotification;
        target.ShowGifRecordingNotification = source.ShowGifRecordingNotification;
        target.AfterCapture = source.AfterCapture;
        target.OpenEditorAfterCapture = source.OpenEditorAfterCapture;
        target.OpenInSystemViewerAfterCapture = source.OpenInSystemViewerAfterCapture;
    }

    public static void SetEditorExportFormat(int format)
    {
        lock (CacheGate)
        {
            if (s_cachedSettings != null)
            {
                s_cachedSettings.EditorExportFormat = format;
            }
        }

        try
        {
            var svc = new SettingsService();
            svc.Load();
            svc.Settings.EditorExportFormat = format;
            svc.Save();
            svc.FlushPendingWrites();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.editor-export-format.static-save", ex);
        }
    }

    public static void SetUploadDefaultProvider(UploadProviderKind kind)
    {
        lock (CacheGate)
        {
            if (s_cachedSettings != null)
            {
                s_cachedSettings.UploadDefaultProvider = kind;
            }
        }

        try
        {
            var svc = new SettingsService();
            svc.Load();
            svc.Settings.UploadDefaultProvider = kind;
            svc.Save();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.upload-default-provider.static-save", ex);
        }
    }

    public static void SaveLastEmoji(string emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return;

        lock (CacheGate)
        {
            if (s_cachedSettings != null)
                s_cachedSettings.LastEmoji = emoji;
        }

        try
        {
            var svc = new SettingsService();
            svc.Load();
            if (string.Equals(svc.Settings.LastEmoji, emoji, StringComparison.Ordinal))
                return;
            svc.Settings.LastEmoji = emoji;
            svc.Save();
            svc.FlushPendingWrites();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.last-emoji.static-save", ex);
        }
    }

    public static void SaveShowCapturePreview(bool value, CaptureKind captureKind = CaptureKind.Screenshot) =>
        MutateResultDelivery(settings =>
        {
            var outcome = Helpers.AfterCaptureOutcomeModel.FromSettings(settings, captureKind) with { Preview = value };
            Helpers.AfterCaptureOutcomeModel.ApplyToSettings(outcome, settings, captureKind);
        }, "settings.show-capture-preview.static-save");

    public static void SaveQuickPanelCompact(bool value)
    {
        lock (CacheGate)
        {
            if (s_cachedSettings != null)
                s_cachedSettings.QuickPanelCompact = value;
        }

        try
        {
            var svc = new SettingsService();
            svc.Load();
            svc.Settings.QuickPanelCompact = value;
            svc.Save();
            svc.FlushPendingWrites();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.quick-panel-compact.static-save", ex);
        }
    }

    public static void SaveQuickPanelShowRecording(bool value)
    {
        lock (CacheGate)
        {
            if (s_cachedSettings != null)
                s_cachedSettings.QuickPanelShowRecording = value;
        }

        try
        {
            var svc = new SettingsService();
            svc.Load();
            svc.Settings.QuickPanelShowRecording = value;
            svc.Save();
            svc.FlushPendingWrites();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.quick-panel-recording.static-save", ex);
        }
    }

    public static void SaveQuickPanelShowTools(bool value)
    {
        lock (CacheGate)
        {
            if (s_cachedSettings != null)
                s_cachedSettings.QuickPanelShowTools = value;
        }

        try
        {
            var svc = new SettingsService();
            svc.Load();
            svc.Settings.QuickPanelShowTools = value;
            svc.Save();
            svc.FlushPendingWrites();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.quick-panel-tools.static-save", ex);
        }
    }

    public static void SaveQuickPanelShowGallery(bool value)
    {
        lock (CacheGate)
        {
            if (s_cachedSettings != null)
                s_cachedSettings.QuickPanelShowGallery = value;
        }

        try
        {
            var svc = new SettingsService();
            svc.Load();
            svc.Settings.QuickPanelShowGallery = value;
            svc.Save();
            svc.FlushPendingWrites();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.quick-panel-gallery.static-save", ex);
        }
    }

    public static void SaveEditorWindowPlacement(double left, double top, double width, double height, bool maximized)
    {
        lock (CacheGate)
        {
            if (s_cachedSettings != null)
            {
                s_cachedSettings.EditorWindowLeft = left;
                s_cachedSettings.EditorWindowTop = top;
                s_cachedSettings.EditorWindowWidth = width;
                s_cachedSettings.EditorWindowHeight = height;
                s_cachedSettings.EditorWindowMaximized = maximized;
            }
        }

        try
        {
            var svc = new SettingsService();
            svc.Load();
            svc.Settings.EditorWindowLeft = left;
            svc.Settings.EditorWindowTop = top;
            svc.Settings.EditorWindowWidth = width;
            svc.Settings.EditorWindowHeight = height;
            svc.Settings.EditorWindowMaximized = maximized;
            svc.Save();
            svc.FlushPendingWrites();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.editor-placement.static-save", ex);
        }
    }

    public SettingsService(string? settingsPath = null, TimeSpan? saveDelay = null)
    {
        _settingsPath = ResolveSettingsPath(settingsPath);
        _settingsDir = Path.GetDirectoryName(_settingsPath) ?? AppContext.BaseDirectory;
        _saveDelay = saveDelay ?? TimeSpan.FromMilliseconds(350);
        _flushTimer = new System.Threading.Timer(_ =>
        {
            try { FlushPendingWrites(); }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("settings.save", ex, $"Failed to persist settings to {_settingsPath}.");
                NotifySaveFailed(ex.Message);
            }
        }, null, System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>Synchronize delivery preferences across result windows backed by separate services.</summary>
    internal void RefreshResultDeliveryPreferences()
    {
        var current = LoadStatic(_settingsPath);
        if (current is null)
            return;
        CopyResultDeliveryPreferences(current, Settings);
    }

    internal void RefreshWidgetPreferences()
    {
        var current = LoadStatic(_settingsPath);
        if (current is not null)
            Helpers.WidgetPreferences.FromSettings(current).ApplyTo(Settings);
    }

    internal void RefreshPreviewPreferences()
    {
        var current = LoadStatic(_settingsPath);
        if (current is not null)
        {
            Settings.PreviewShowCheckerboard = current.PreviewShowCheckerboard;
            Settings.PreviewBackgroundDarkColorArgb = current.PreviewBackgroundDarkColorArgb;
            Settings.PreviewBackgroundLightColorArgb = current.PreviewBackgroundLightColorArgb;
        }
    }

    internal void RefreshCursorCapturePreferences()
    {
        var current = LoadStatic(_settingsPath);
        if (current is null) return;
        Settings.ShowCursor = current.ShowCursor;
        Settings.VideoShowCursor = current.VideoShowCursor;
        Settings.GifShowCursor = current.GifShowCursor;
    }

    /// <summary>Quick static load for read-only access (e.g. tooltips). Returns null on error.</summary>
    public static AppSettings? LoadStatic(string? settingsPath = null)
    {
        var resolvedPath = ResolveSettingsPath(settingsPath);
        if (!File.Exists(resolvedPath))
            TryMigrateLegacyPortableSettings(resolvedPath);

        if (TryGetCachedSettings(resolvedPath, out var cached))
            return cached;

        try
        {
            if (!File.Exists(resolvedPath))
            {
                var defaults = new AppSettings();
                CacheSettings(resolvedPath, defaults);
                return CloneSettings(defaults);
            }

            var json = File.ReadAllText(resolvedPath);
            var loaded = DeserializeSettings(json);
            CacheSettings(resolvedPath, loaded);
            return CloneSettings(loaded);
        }
        catch { return null; }
    }

    public static bool TryDeserialize(string json, out AppSettings settings)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || (document.RootElement.TryGetProperty("Format", out var format)
                    && format.ValueKind == JsonValueKind.String && format.GetString() == AutomaticBackupService.Format))
            {
                // An automatic backup is an envelope, not raw settings. Treating it
                // as AppSettings would silently import defaults and discard the payload.
                settings = new AppSettings();
                return false;
            }
            settings = DeserializeSettings(json);
            return true;
        }
        catch
        {
            settings = new AppSettings();
            return false;
        }
    }

    public void Load()
    {
        // Flush any debounced write first so a reload cannot overwrite an
        // in-memory change that has not hit disk yet (e.g. widget toggles).
        FlushPendingWrites();

        if (!File.Exists(_settingsPath))
            TryMigrateLegacyPortableSettings();

        if (!File.Exists(_settingsPath))
        {
            CacheSettings(_settingsPath, Settings);
            return;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            Settings = DeserializeSettings(json);
            CacheSettings(_settingsPath, Settings);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.load", ex, $"Failed to load settings from {_settingsPath}. Using defaults.");
        }
    }

    public void Save()
    {
        CacheSettings(_settingsPath, Settings);

        lock (_gate)
        {
            if (_disposed)
            {
                FlushPendingWrites_NoLock();
                return;
            }

            _settingsDirty = true;
            _flushTimer.Change(_saveDelay, System.Threading.Timeout.InfiniteTimeSpan);
        }
        SettingsChanged?.Invoke();
    }

    public void FlushPendingWrites()
    {
        lock (_gate)
            FlushPendingWrites_NoLock();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            try { _flushTimer.Change(System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan); } catch { }
            FlushPendingWrites_NoLock();
        }

        _flushTimer.Dispose();
        GC.SuppressFinalize(this);
    }

    private void FlushPendingWrites_NoLock()
    {
        if (!_settingsDirty)
            return;

        Directory.CreateDirectory(_settingsDir);
        var storedSettings = SensitiveSettingsProtection.ProtectForStorage(Settings, JsonOptions);
        var json = JsonSerializer.Serialize(storedSettings, JsonOptions);
        var tmpPath = _settingsPath + ".tmp";
        bool wrote = false;
        try
        {
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _settingsPath, overwrite: true);
            wrote = true;
        }
        catch (IOException ex)
        {
            wrote = TryWriteSettingsFallback_NoLock(tmpPath, json, ex.Message, "IO");
        }
        catch (UnauthorizedAccessException ex)
        {
            wrote = TryWriteSettingsFallback_NoLock(tmpPath, json, ex.Message, "access");
        }

        if (wrote)
            _settingsDirty = false;
    }

    private bool TryWriteSettingsFallback_NoLock(string tmpPath, string json, string initialError, string errorKind)
    {
        TryDeleteSettingsTempFile_NoLock(tmpPath, "fallback");
        try
        {
            File.WriteAllText(_settingsPath, json);
            return true;
        }
        catch (Exception fallbackEx)
        {
            var message = $"Failed to persist settings after {errorKind} error writing {_settingsPath}. Initial error: {initialError}";
            AppDiagnostics.LogError("settings.save", fallbackEx, message);
            NotifySaveFailed(fallbackEx.Message);
            return false;
        }
    }

    private static void TryDeleteSettingsTempFile_NoLock(string tmpPath, string context)
    {
        try
        {
            if (File.Exists(tmpPath))
                File.Delete(tmpPath);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning(
                "settings.temp-cleanup",
                $"Failed to delete {context} temporary settings file {Path.GetFileName(tmpPath)}: {ex.Message}",
                ex);
        }
    }

    private void NotifySaveFailed(string message)
    {
        try { SaveFailed?.Invoke(message); } catch { }
    }

    private void TryMigrateLegacyPortableSettings()
        => TryMigrateLegacyPortableSettings(_settingsPath);

    private static void TryMigrateLegacyPortableSettings(string settingsPath)
    {
        if (string.Equals(settingsPath, LegacySettingsPath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            if (!File.Exists(LegacySettingsPath))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath) ?? AppContext.BaseDirectory);
            File.Copy(LegacySettingsPath, settingsPath, overwrite: false);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("settings.migrate-portable", ex.Message, ex);
        }
    }

    private static string ResolveSettingsPath(string? settingsPath) =>
        AppStoragePaths.ResolveSettingsPath(settingsPath);

    private static bool TryGetCachedSettings(string settingsPath, out AppSettings? settings)
    {
        lock (CacheGate)
        {
            if (string.Equals(s_cachedPath, settingsPath, StringComparison.OrdinalIgnoreCase))
            {
                settings = s_cachedSettings is null ? null : CloneSettings(s_cachedSettings);
                return settings is not null;
            }
        }

        settings = null;
        return false;
    }

    private static void CacheSettings(string settingsPath, AppSettings settings)
    {
        lock (CacheGate)
        {
            s_cachedPath = settingsPath;
            s_cachedSettings = CloneSettings(settings);
        }
    }

    private static AppSettings CloneSettings(AppSettings settings)
    {
        return JsonSerializer.Deserialize<AppSettings>(
                   JsonSerializer.Serialize(settings, JsonOptions),
                   JsonOptions)
               ?? new AppSettings();
    }

    public static string ExportRedactedJson(AppSettings settings)
    {
        var redacted = SensitiveSettingsProtection.RedactForExport(settings, JsonOptions);
        return JsonSerializer.Serialize(redacted, JsonOptions);
    }

    internal string StorageDirectory => _settingsDir;

    // Save() publishes an immutable clone immediately, before its debounced disk write.
    // Backups therefore include the latest saved changes without reading mutable UI collections.
    internal AppSettings GetSnapshot() => TryGetCachedSettings(_settingsPath, out var snapshot)
        ? snapshot! : CloneSettings(Settings);

    internal void ReplaceDurably(AppSettings replacement)
        => PersistReplacementDurably(replacement, backupOptionsOnly: false);

    internal void SaveAutomaticBackupOptionsDurably(AutomaticBackupSettings options)
    {
        var replacement = GetSnapshot();
        replacement.AutomaticBackup = options.Normalize();
        PersistReplacementDurably(replacement, backupOptionsOnly: true);
    }

    private void PersistReplacementDurably(AppSettings replacement, bool backupOptionsOnly)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var copy = CloneSettings(replacement);
            var json = JsonSerializer.Serialize(SensitiveSettingsProtection.ProtectForStorage(copy, JsonOptions), JsonOptions);
            AtomicBackupFile.Write(_settingsPath, System.Text.Encoding.UTF8.GetBytes(json), overwrite: true);
            if (backupOptionsOnly) Settings.AutomaticBackup = copy.AutomaticBackup;
            else Settings = copy;
            _settingsDirty = false;
            _flushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            CacheSettings(_settingsPath, copy);
        }
        SettingsChanged?.Invoke();
    }

    private static AppSettings DeserializeSettings(string json)
    {
        using var document = JsonDocument.Parse(json);
        var settings = document.RootElement.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings();
        if (!document.RootElement.TryGetProperty(nameof(AppSettings.CaptureToolbarSchemaVersion), out _))
            settings.CaptureToolbarSchemaVersion = 0;
        if (settings.CaptureToolbarSchemaVersion < 1)
        {
            // Introduce only the new tool once; keep every other custom visibility choice intact.
            if (settings.EnabledTools is not null && !settings.EnabledTools.Contains("zoom"))
                settings.EnabledTools.Add("zoom");
            settings.CaptureToolbarSchemaVersion = 1;
        }
        if (settings.CaptureToolbarSchemaVersion < 2)
        {
            if (settings.EnabledTools is not null && !settings.EnabledTools.Contains("pin"))
                settings.EnabledTools.Add("pin");
            settings.CaptureToolbarSchemaVersion = 2;
        }
        settings.OpenWithApps = NormalizeOpenWithApps(settings.OpenWithApps);
        SensitiveSettingsProtection.Unprotect(settings);

        if (settings.CompressHistory && settings.CaptureImageFormat == CaptureImageFormat.Png)
            settings.CaptureImageFormat = CaptureImageFormat.Jpeg;

        if (string.Equals(settings.FileNameTemplate, Helpers.FileNameTemplate.LegacyDefaultTemplate, StringComparison.Ordinal))
            settings.FileNameTemplate = Helpers.FileNameTemplate.DefaultTemplate;

        settings.ImageSearchSources &= ImageSearchSourceOptions.All;
        settings.UiScale = CyberSnap.UI.UiScale.Normalize(settings.UiScale);
        settings.InterfaceLanguage = LocalizationService.NormalizeLanguageSetting(settings.InterfaceLanguage);
        settings.AutomaticBackup = (settings.AutomaticBackup ?? new()).Normalize();
        NormalizeEnums(settings);
        settings.OcrDefaultTranslateFrom = TranslationService.ResolveSourceLanguage(settings.OcrDefaultTranslateFrom);
        settings.OcrDefaultTranslateTo = NormalizeTranslationTargetSetting(settings.OcrDefaultTranslateTo);

        // Preserved user configuration of enabled tools without forcing missing ones back on.

        NormalizeUnsafeModifierlessHotkeys(settings);
        NormalizeToastButtonLayout(settings.ToastButtons);
        Helpers.AfterCapturePreferences.MigrateSystemViewerFlagIfNeeded(settings);
        Helpers.RecordingOutcomeModel.MigrateSaveMediaIfNeeded(settings);

        // If the primary capture hotkey was never set (e.g. old settings file predating the
        // property default), restore the factory default: Alt+Shift+A.
        if (settings.HotkeyKey == 0 && settings.HotkeyModifiers == 0)
        {
            settings.HotkeyModifiers = Native.User32.MOD_ALT | Native.User32.MOD_SHIFT;
            settings.HotkeyKey = 0x41; // A
        }

        return settings;
    }

    private static void NormalizeEnums(AppSettings settings)
    {
        settings.AfterCapture = NormalizeEnum(settings.AfterCapture, AfterCaptureAction.CopyToClipboard);
        settings.CaptureImageFormat = NormalizeEnum(settings.CaptureImageFormat, CaptureImageFormat.Png);
        settings.LastCaptureMode = NormalizeEnum(settings.LastCaptureMode, CaptureMode.Rectangle);
        settings.DefaultCaptureMode = NormalizeEnum(settings.DefaultCaptureMode, CaptureMode.Rectangle);
        settings.WindowDetection = NormalizeEnum(settings.WindowDetection, WindowDetectionMode.WindowOnly);
        settings.CaptureDockSide = NormalizeEnum(settings.CaptureDockSide, CaptureDockSide.Top);
        settings.ZoomInitialLevel = Capture.ZoomViewport.NormalizeZoom(settings.ZoomInitialLevel);
        settings.PinInitialScale = Capture.PinImageForm.NormalizeScale(settings.PinInitialScale);
        settings.ScrollingCaptureMode = NormalizeEnum(settings.ScrollingCaptureMode, ScrollingCaptureMode.AssistAutoscroll);
        if (settings.ScrollingCaptureMode == ScrollingCaptureMode.Manual)
        {
            settings.ScrollingCaptureMode = ScrollingCaptureMode.AssistAutoscroll;
        }
        settings.HistoryRetention = NormalizeEnum(settings.HistoryRetention, HistoryRetentionPeriod.Never);
        settings.HistoryClickAction = NormalizeEnum(settings.HistoryClickAction, HistoryClickAction.OpenInEditor);
        settings.ToastPreviewClickAction = NormalizeEnum(settings.ToastPreviewClickAction, ToastPreviewClickAction.OpenInEditor);
        settings.ToastPosition = NormalizeEnum(settings.ToastPosition, ToastPosition.TopCenter);

        settings.RecordingFormat = NormalizeEnum(settings.RecordingFormat, RecordingFormat.MP4);
        settings.RecordingQuality = NormalizeEnum(settings.RecordingQuality, RecordingQuality.Original);
        settings.RecordingFps = settings.RecordingFps switch
        {
            15 or 24 or 30 or 60 => settings.RecordingFps,
            _ => 30
        };
        // GIF encoder supports up to 30 FPS; keep options intentionally small for file size.
        settings.GifFps = settings.GifFps == 30 ? 30 : 15;
        settings.CenterSelectionAspectRatio = NormalizeEnum(settings.CenterSelectionAspectRatio, CenterSelectionAspectRatio.Free);
        settings.TranslationModel = Enum.IsDefined(typeof(TranslationModel), settings.TranslationModel)
            ? settings.TranslationModel
            : (int)TranslationModel.MyMemory;
        settings.UploadDefaultProvider = NormalizeEnum(settings.UploadDefaultProvider, UploadProviderKind.CyberGems);
        // One-time: installs from before CyberGems Share still have ImgBB as default.
        // Promote to CyberGems once; later explicit ImgBB choices are preserved (schema >= 1).
        if (settings.UploadSettingsSchemaVersion < 1)
        {
            if (settings.UploadDefaultProvider == UploadProviderKind.ImgBB)
                settings.UploadDefaultProvider = UploadProviderKind.CyberGems;
            settings.UploadSettingsSchemaVersion = 1;
        }
        // Imgur is user-key-only (no OOTB); do not keep it as default without a Client-ID.
        if (settings.UploadDefaultProvider == UploadProviderKind.Imgur &&
            !(settings.UploadUseCustomImgurClientId && !string.IsNullOrWhiteSpace(settings.UploadImgurClientId)))
        {
            settings.UploadDefaultProvider = UploadProviderKind.CyberGems;
        }
        settings.UploadCustomProtocol = NormalizeEnum(settings.UploadCustomProtocol, UploadCustomProtocol.Sftp);
        settings.UploadWebhookBodyMode = NormalizeEnum(settings.UploadWebhookBodyMode, UploadWebhookBodyMode.Multipart);
        if (string.IsNullOrWhiteSpace(settings.UploadWebhookFormFieldName))
            settings.UploadWebhookFormFieldName = "image";
        settings.UploadImageFormat = NormalizeEnum(settings.UploadImageFormat, UploadImageFormatPreference.Png);
        if (settings.UploadJpegQuality is < 1 or > 100)
            settings.UploadJpegQuality = 90;
        if (settings.UploadMaxBytes <= 0)
            settings.UploadMaxBytes = 10 * 1024 * 1024;
        if (settings.UploadMinIntervalMs < 0)
            settings.UploadMinIntervalMs = 3000;
        if (settings.UploadDailyCapOotb < 0)
            settings.UploadDailyCapOotb = 50;
        if (settings.UploadHttpTimeoutSeconds is < 15 or > 600)
            settings.UploadHttpTimeoutSeconds = 120;
    }

    private static TEnum NormalizeEnum<TEnum>(TEnum value, TEnum fallback)
        where TEnum : struct, Enum =>
        Enum.IsDefined(typeof(TEnum), value) ? value : fallback;

    private static void NormalizeUnsafeModifierlessHotkeys(AppSettings settings)
    {
        if (IsUnsafeModifierlessHotkey(settings.HotkeyModifiers, settings.HotkeyKey))
            settings.HotkeyKey = 0;
        if (IsUnsafeModifierlessHotkey(settings.OcrHotkeyModifiers, settings.OcrHotkeyKey))
            settings.OcrHotkeyKey = 0;
        if (IsUnsafeModifierlessHotkey(settings.PickerHotkeyModifiers, settings.PickerHotkeyKey))
            settings.PickerHotkeyKey = 0;
        if (IsUnsafeModifierlessHotkey(settings.ScanHotkeyModifiers, settings.ScanHotkeyKey))
            settings.ScanHotkeyKey = 0;
        if (IsUnsafeModifierlessHotkey(settings.CenterHotkeyModifiers, settings.CenterHotkeyKey))
            settings.CenterHotkeyKey = 0;
        if (IsUnsafeModifierlessHotkey(settings.FullscreenHotkeyModifiers, settings.FullscreenHotkeyKey))
            settings.FullscreenHotkeyKey = 0;
        if (IsUnsafeModifierlessHotkey(settings.ActiveWindowHotkeyModifiers, settings.ActiveWindowHotkeyKey))
            settings.ActiveWindowHotkeyKey = 0;
        if (IsUnsafeModifierlessHotkey(settings.RulerHotkeyModifiers, settings.RulerHotkeyKey))
            settings.RulerHotkeyKey = 0;
        if (IsUnsafeModifierlessHotkey(settings.ScrollCaptureHotkeyModifiers, settings.ScrollCaptureHotkeyKey))
            settings.ScrollCaptureHotkeyKey = 0;
        if (IsUnsafeModifierlessHotkey(settings.StandaloneRulerHotkeyModifiers, settings.StandaloneRulerHotkeyKey))
            settings.StandaloneRulerHotkeyKey = 0;
        if (IsUnsafeModifierlessHotkey(settings.StandaloneZoomHotkeyModifiers, settings.StandaloneZoomHotkeyKey))
            settings.StandaloneZoomHotkeyKey = 0;
    }

    private static bool IsUnsafeModifierlessHotkey(uint modifiers, uint key) =>
        modifiers == 0 && key != 0 && key != Native.User32.VK_SNAPSHOT;

    private static string NormalizeTranslationTargetSetting(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode) ||
            string.Equals(languageCode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        return TranslationService.ResolveTargetLanguage(languageCode, "en");
    }

    private static void NormalizeToastButtonLayout(AppSettings.ToastButtonLayoutSettings settings)
    {
        var used = new HashSet<ToastButtonSlot>();

        // Only *visible* buttons reserve slots. Hidden toast-only controls (Close/Pin/…)
        // keep stale slot values in JSON but must not shove confirm destinations onto
        // the bottom row when Settings load after an app restart.
        if (settings.ShowSave)
            settings.SaveSlot = TakeSlot(settings.SaveSlot, ToastButtonSlot.TopLeft, used);
        if (settings.ShowCopy)
            settings.CopySlot = TakeSlot(settings.CopySlot, ToastButtonSlot.TopInnerLeft, used);
        if (settings.ShowEdit)
            settings.EditSlot = TakeSlot(settings.EditSlot, ToastButtonSlot.TopInnerRight, used);
        if (settings.ShowShare)
            settings.ShareSlot = TakeSlot(settings.ShareSlot, ToastButtonSlot.TopRight, used);

        if (settings.ShowClose)
            settings.CloseSlot = TakeSlot(settings.CloseSlot, ToastButtonSlot.TopRight, used);
        if (settings.ShowPin)
            settings.PinSlot = TakeSlot(settings.PinSlot, ToastButtonSlot.TopLeft, used);
        if (settings.ShowHistory)
            settings.HistorySlot = TakeSlot(settings.HistorySlot, ToastButtonSlot.TopInnerLeft, used);
        if (settings.ShowDelete)
            settings.DeleteSlot = TakeSlot(settings.DeleteSlot, ToastButtonSlot.BottomLeft, used);
    }

    private static Dictionary<string, string> NormalizeOpenWithApps(Dictionary<string, string>? apps)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (apps is null)
            return normalized;

        foreach (var pair in apps)
        {
            var ext = OfficeExportService.NormalizeExtension(pair.Key);
            if (ext is null || string.IsNullOrWhiteSpace(pair.Value))
                continue;

            normalized[ext] = pair.Value;
        }

        return normalized;
    }

    private static ToastButtonSlot TakeSlot(ToastButtonSlot requested, ToastButtonSlot fallback, HashSet<ToastButtonSlot> used)
    {
        if (Enum.IsDefined(requested) && used.Add(requested))
            return requested;

        if (used.Add(fallback))
            return fallback;

        foreach (ToastButtonSlot slot in Enum.GetValues<ToastButtonSlot>())
            if (used.Add(slot))
                return slot;

        return fallback;
    }
}
