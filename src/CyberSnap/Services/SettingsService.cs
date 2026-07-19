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

    public static event Action<bool>? OcrAutoCopyToClipboardChanged;
    public static event Action<bool>? AutoCopyToClipboardChanged;

    /// <summary>
    /// Legacy OCR toggle. Maps onto global auto-copy + OCR exclusion.
    /// </summary>
    public static void SetOcrAutoCopyToClipboard(bool value)
    {
        MutateAutoCopy(settings =>
        {
            Helpers.AutoCopyPreferences.SetKindEnabled(settings, Helpers.AutoCopyKind.Ocr, value);
        }, "settings.ocr-auto-copy.static-save");

        OcrAutoCopyToClipboardChanged?.Invoke(value);
        RaiseAutoCopyEventsFromCache();
    }

    public static void SetAutoCopyToClipboard(bool value)
    {
        MutateAutoCopy(settings =>
        {
            Helpers.AutoCopyPreferences.SetMaster(settings, value);
            Helpers.AutoCopyPreferences.SyncAfterCaptureCopyBits(settings);
        }, "settings.auto-copy.static-save");

        RaiseAutoCopyEventsFromCache();
    }

    public static void SetAutoCopyExclude(Helpers.AutoCopyKind kind, bool excluded)
    {
        MutateAutoCopy(settings =>
        {
            Helpers.AutoCopyPreferences.SetExcluded(settings, kind, excluded);
            if (kind == Helpers.AutoCopyKind.Image)
                Helpers.AutoCopyPreferences.SyncAfterCaptureCopyBits(settings);
        }, "settings.auto-copy-exclude.static-save");

        RaiseAutoCopyEventsFromCache();
    }

    private static void MutateAutoCopy(Action<AppSettings> mutate, string errorCode)
    {
        lock (CacheGate)
        {
            if (s_cachedSettings != null)
                mutate(s_cachedSettings);
        }

        try
        {
            var svc = new SettingsService();
            svc.Load();
            mutate(svc.Settings);
            svc.Save();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError(errorCode, ex);
        }
    }

    private static void RaiseAutoCopyEventsFromCache()
    {
        AppSettings? snapshot;
        lock (CacheGate)
            snapshot = s_cachedSettings;

        if (snapshot is null)
            return;

        AutoCopyToClipboardChanged?.Invoke(snapshot.AutoCopyToClipboard);
        OcrAutoCopyToClipboardChanged?.Invoke(snapshot.OcrAutoCopyToClipboard);
    }

    /// <summary>
    /// After the Settings window mutates auto-copy fields and saves, publish the
    /// new state to the static cache and listeners (widget menus, OCR tools).
    /// </summary>
    public static void PublishAutoCopyState(AppSettings settings)
    {
        if (settings is null)
            return;

        Helpers.AutoCopyPreferences.SyncLegacyAliases(settings);

        lock (CacheGate)
        {
            if (s_cachedSettings != null)
            {
                s_cachedSettings.AutoCopyToClipboard = settings.AutoCopyToClipboard;
                s_cachedSettings.AutoCopyExcludeImages = settings.AutoCopyExcludeImages;
                s_cachedSettings.AutoCopyExcludeOcr = settings.AutoCopyExcludeOcr;
                s_cachedSettings.AutoCopyExcludeRecording = settings.AutoCopyExcludeRecording;
                s_cachedSettings.AutoCopyExcludeGif = settings.AutoCopyExcludeGif;
                s_cachedSettings.OcrAutoCopyToClipboard = settings.OcrAutoCopyToClipboard;
                s_cachedSettings.AfterCapture = settings.AfterCapture;
                s_cachedSettings.OpenEditorAfterCapture = settings.OpenEditorAfterCapture;
                s_cachedSettings.OpenInSystemViewerAfterCapture = settings.OpenInSystemViewerAfterCapture;
                s_cachedSettings.AutoCopySettingsSchemaVersion = settings.AutoCopySettingsSchemaVersion;
            }
        }

        AutoCopyToClipboardChanged?.Invoke(settings.AutoCopyToClipboard);
        OcrAutoCopyToClipboardChanged?.Invoke(settings.OcrAutoCopyToClipboard);
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

    private static AppSettings DeserializeSettings(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        settings.OpenWithApps = NormalizeOpenWithApps(settings.OpenWithApps);
        SensitiveSettingsProtection.Unprotect(settings);

        if (settings.CompressHistory && settings.CaptureImageFormat == CaptureImageFormat.Png)
            settings.CaptureImageFormat = CaptureImageFormat.Jpeg;

        if (string.Equals(settings.FileNameTemplate, Helpers.FileNameTemplate.LegacyDefaultTemplate, StringComparison.Ordinal))
            settings.FileNameTemplate = Helpers.FileNameTemplate.DefaultTemplate;

        settings.ImageSearchSources &= ImageSearchSourceOptions.All;
        settings.UiScale = CyberSnap.UI.UiScale.Normalize(settings.UiScale);
        settings.InterfaceLanguage = LocalizationService.NormalizeLanguageSetting(settings.InterfaceLanguage);
        NormalizeEnums(settings);
        settings.OcrDefaultTranslateFrom = TranslationService.ResolveSourceLanguage(settings.OcrDefaultTranslateFrom);
        settings.OcrDefaultTranslateTo = NormalizeTranslationTargetSetting(settings.OcrDefaultTranslateTo);

        // Preserved user configuration of enabled tools without forcing missing ones back on.

        NormalizeUnsafeModifierlessHotkeys(settings);
        NormalizeToastButtonLayout(settings.ToastButtons);
        Helpers.AutoCopyPreferences.MigrateIfNeeded(settings);
        Helpers.AfterCapturePreferences.MigrateSystemViewerFlagIfNeeded(settings);

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
        settings.AfterCapture = NormalizeEnum(settings.AfterCapture, AfterCaptureAction.PreviewAndCopy);
        settings.CaptureImageFormat = NormalizeEnum(settings.CaptureImageFormat, CaptureImageFormat.Png);
        settings.LastCaptureMode = NormalizeEnum(settings.LastCaptureMode, CaptureMode.Rectangle);
        settings.DefaultCaptureMode = NormalizeEnum(settings.DefaultCaptureMode, CaptureMode.Rectangle);
        settings.WindowDetection = NormalizeEnum(settings.WindowDetection, WindowDetectionMode.WindowOnly);
        settings.CaptureDockSide = NormalizeEnum(settings.CaptureDockSide, CaptureDockSide.Top);
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
        if (IsUnsafeModifierlessHotkey(settings.GifHotkeyModifiers, settings.GifHotkeyKey))
            settings.GifHotkeyKey = 0;
        if (IsUnsafeModifierlessHotkey(settings.StandaloneRulerHotkeyModifiers, settings.StandaloneRulerHotkeyKey))
            settings.StandaloneRulerHotkeyKey = 0;
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
        settings.CloseSlot = TakeSlot(settings.CloseSlot, ToastButtonSlot.TopRight, used);
        settings.PinSlot = TakeSlot(settings.PinSlot, ToastButtonSlot.TopLeft, used);
        settings.SaveSlot = TakeSlot(settings.SaveSlot, ToastButtonSlot.TopInnerRight, used);
        settings.CopySlot = TakeSlot(settings.CopySlot, ToastButtonSlot.BottomInnerRight, used);
        settings.ShareSlot = TakeSlot(settings.ShareSlot, ToastButtonSlot.BottomLeft, used);
        settings.HistorySlot = TakeSlot(settings.HistorySlot, ToastButtonSlot.TopInnerLeft, used);
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
