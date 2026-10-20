using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CyberSnap.Models;

namespace CyberSnap.Services;

public sealed record BackupFileInfo(string FilePath, DateTimeOffset CreatedUtc, long SizeBytes, bool IsRecovery, bool IsValid)
{
    public string FileName => Path.GetFileName(FilePath);
}

public sealed class AutomaticBackupService : IDisposable
{
    internal const string Format = "CyberSnap.SettingsBackup";
    private const int SchemaVersion = 1;
    private const int MaximumFileBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex OwnedName = new(
        @"^CyberSnap-(backup|recovery)-(?:[0-9]{4}(?:-[0-9]{2}){2}_[0-9]{2}(?:-[0-9]{2}){2}(?:_[0-9]+)?|[0-9]{8}T[0-9]{9}Z-[a-f0-9]{32})\.json$",
        RegexOptions.CultureInvariant);
    private readonly SettingsService _settings;
    private readonly TimeProvider _clock;
    private readonly SynchronizationContext? _settingsContext;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private System.Threading.Timer? _timer;
    private DateTimeOffset _retryAfter;
    private int _startupPending = 1;
    private int _timerRunning;
    private string? _operationWarning;
    private bool _disposed;

    public AutomaticBackupService(SettingsService settings, TimeProvider? clock = null)
    {
        _settings = settings;
        _clock = clock ?? TimeProvider.System;
        _settingsContext = SynchronizationContext.Current;
    }

    public event Action? Changed;
    public string? LastError { get; private set; }
    public string? LastWarning { get; private set; }
    public string DefaultDirectory => Path.Combine(_settings.StorageDirectory, "backups");
    public string BackupDirectory => ResolveDirectory(_settings.GetSnapshot().AutomaticBackup);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _timer ??= new System.Threading.Timer(async _ =>
        {
            if (Interlocked.Exchange(ref _timerRunning, 1) != 0) return;
            try
            {
                await CheckScheduleAsync(Volatile.Read(ref _startupPending) == 1, _shutdown.Token);
                if (_clock.GetUtcNow() >= _retryAfter) Interlocked.Exchange(ref _startupPending, 0);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (Exception ex) { AppDiagnostics.LogError("backup.scheduler", ex); }
            finally { Volatile.Write(ref _timerRunning, 0); }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
    }

    public static DateTimeOffset? NextBackupUtc(BackupFrequency frequency, DateTimeOffset? last)
        => last is null || frequency == BackupFrequency.OnStartup ? null : frequency switch
        {
            BackupFrequency.Weekly => last.Value.AddDays(7),
            BackupFrequency.Monthly => last.Value.AddMonths(1),
            _ => last.Value.AddDays(1)
        };

    public async Task<bool> CheckScheduleAsync(bool isStartup, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operations.WaitAsync(cancellation);
        try
        {
            _operationWarning = null;
            var snapshot = _settings.GetSnapshot();
            var options = snapshot.AutomaticBackup.Normalize();
            var now = _clock.GetUtcNow();
            if (!options.Enabled || (options.Frequency == BackupFrequency.OnStartup && !isStartup) || now < _retryAfter)
                return false;
            var directory = ResolveDirectory(options);
            var files = await Task.Run(() => ListCore(directory), cancellation);
            var last = files.Where(file => file.IsValid && !file.IsRecovery && file.CreatedUtc <= now.AddMinutes(5))
                .Select(file => (DateTimeOffset?)file.CreatedUtc).Max();
            bool due = last is null || (options.Frequency == BackupFrequency.OnStartup
                ? now - last.Value >= TimeSpan.FromMinutes(5)
                : now >= NextBackupUtc(options.Frequency, last)!.Value);
            if (!due) return false;
            var created = await Task.Run(() => CreateCore(snapshot, directory, false, cancellation), cancellation);
            await Task.Run(() => Prune(directory, options.MaxBackups, created.FilePath), cancellation);
            LastError = null;
            LastWarning = _operationWarning;
            NotifyChanged();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _retryAfter = _clock.GetUtcNow().AddMinutes(5);
            ReportFailure(ex);
            throw;
        }
        finally { _operations.Release(); }
    }

    public async Task<BackupFileInfo> CreateBackupAsync(CancellationToken cancellation = default)
    {
        return await ExclusiveAsync(async () =>
        {
            var snapshot = _settings.GetSnapshot();
            string directory = ResolveDirectory(snapshot.AutomaticBackup);
            var result = await Task.Run(() => CreateCore(snapshot, directory, false, cancellation), cancellation);
            await Task.Run(() => Prune(directory, snapshot.AutomaticBackup.Normalize().MaxBackups, result.FilePath), cancellation);
            _retryAfter = default;
            return result;
        }, cancellation);
    }

    public Task<IReadOnlyList<BackupFileInfo>> ListBackupsAsync(CancellationToken cancellation = default)
        => ExclusiveAsync<IReadOnlyList<BackupFileInfo>>(() => Task.Run<IReadOnlyList<BackupFileInfo>>(
            () => ListCore(BackupDirectory), cancellation), cancellation, clearError: false);

    public Task DeleteBackupAsync(string filePath, CancellationToken cancellation = default)
        => ExclusiveAsync(async () =>
        {
            string path = ValidateManagedPath(filePath);
            await Task.Run(() => File.Delete(path), cancellation);
            return true;
        }, cancellation);

    public Task RestoreBackupAsync(string filePath, CancellationToken cancellation = default)
        => ExclusiveAsync(async () =>
        {
            string path = ValidateManagedPath(filePath);
            var envelope = await Task.Run(() => ReadVerified(path), cancellation);
            if (!SettingsService.TryDeserialize(envelope.Settings.GetRawText(), out var restored))
                throw new InvalidDataException("The backup contains invalid settings.");
            cancellation.ThrowIfCancellationRequested();
            await OnSettingsContextAsync(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                var current = _settings.GetSnapshot();
                SensitiveSettingsProtection.KeepCurrentSecrets(restored, current);
                restored.AutomaticBackup = current.AutomaticBackup.Normalize();
                // A verified recovery copy must exist before replacing the live settings.
                // Keep the newest recovery copy outside the normal retention limit.
                var recovery = CreateCore(current, BackupDirectory, true, cancellation);
                _settings.ReplaceDurably(restored);
                Prune(BackupDirectory, restored.AutomaticBackup.MaxBackups, recovery.FilePath);
            });
            return true;
        }, cancellation);

    private async Task<T> ExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken cancellation, bool clearError = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operations.WaitAsync(cancellation);
        try
        {
            if (clearError) _operationWarning = null;
            var result = await operation();
            if (clearError) { LastError = null; LastWarning = _operationWarning; }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ReportFailure(ex, notify: clearError); throw; }
        finally { _operations.Release(); if (clearError) NotifyChanged(); }
    }

    private string ResolveDirectory(AutomaticBackupSettings options)
    {
        string configured = options.CustomDirectory?.Trim() ?? "";
        if (configured.Length == 0) return Path.GetFullPath(DefaultDirectory);
        if (!Path.IsPathFullyQualified(configured))
            throw new IOException("Choose an absolute backup folder path.");
        // Never silently fall back to another folder if a chosen drive is unavailable.
        return Path.GetFullPath(configured);
    }

    private BackupFileInfo CreateCore(AppSettings snapshot, string directory, bool recovery, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var now = _clock.GetUtcNow();
        var payload = JsonSerializer.Deserialize<JsonElement>(SettingsService.ExportRedactedJson(snapshot));
        var envelope = new BackupEnvelope
        {
            Format = Format, Version = SchemaVersion, CreatedUtc = now,
            AppVersion = UpdateService.GetCurrentVersionLabel(), IsRecovery = recovery, Settings = payload
        };
        envelope.Sha256 = Checksum(envelope);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("The backup exceeds the supported size limit.");
        string timestamp = TimeZoneInfo.ConvertTime(now, _clock.LocalTimeZone).ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        string name = $"CyberSnap-{(recovery ? "recovery" : "backup")}-{timestamp}";
        for (int sequence = 1; ; sequence = checked(sequence + 1))
        {
            cancellation.ThrowIfCancellationRequested();
            string suffix = sequence == 1 ? "" : "_" + sequence.ToString(CultureInfo.InvariantCulture);
            string path = Path.Combine(directory, name + suffix + ".json");
            if (File.Exists(path)) continue;
            try { AtomicBackupFile.Write(path, bytes); }
            // Another writer can claim the name after File.Exists. Retry only
            // file-already-exists errors; other I/O failures must still surface.
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 80 or 183) { continue; }
            ReadVerified(path);
            return new BackupFileInfo(path, now, bytes.Length, recovery, true);
        }
    }

    private static List<BackupFileInfo> ListCore(string directory)
    {
        string[] paths;
        try { paths = Directory.GetFiles(directory, "CyberSnap-*.json", SearchOption.TopDirectoryOnly); }
        catch (DirectoryNotFoundException) when (Directory.Exists(Path.GetPathRoot(directory))) { return []; }
        var result = new List<BackupFileInfo>();
        foreach (string path in paths)
        {
            if (!OwnedName.IsMatch(Path.GetFileName(path))) continue;
            var file = new FileInfo(path);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                var envelope = ReadVerified(path);
                result.Add(new(path, envelope.CreatedUtc, file.Length, envelope.IsRecovery, true));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                result.Add(new(path, file.LastWriteTimeUtc, file.Length, Path.GetFileName(path).StartsWith("CyberSnap-recovery-", StringComparison.Ordinal), false));
            }
        }
        return result.OrderByDescending(file => file.CreatedUtc).ThenBy(file => file.FileName, StringComparer.Ordinal).ToList();
    }

    private static BackupEnvelope ReadVerified(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("The backup exceeds the supported size limit.");
        BackupEnvelope envelope;
        try { envelope = JsonSerializer.Deserialize<BackupEnvelope>(stream) ?? throw new InvalidDataException("Invalid CyberSnap backup file."); }
        catch (JsonException ex) { throw new InvalidDataException("The backup is damaged or uses an unsupported format.", ex); }
        if (envelope.Format != Format || envelope.Version != SchemaVersion || envelope.CreatedUtc == default
            || envelope.Settings.ValueKind != JsonValueKind.Object
            || !envelope.Settings.TryGetProperty(nameof(AppSettings.HasCompletedSetup), out _)
            || !envelope.Settings.TryGetProperty(nameof(AppSettings.CelebrationCaptureCount), out _)
            || !string.Equals(envelope.Sha256, Checksum(envelope), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The backup is damaged or uses an unsupported format.");
        return envelope;
    }

    private static string Checksum(BackupEnvelope envelope)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            envelope.Format, envelope.Version, envelope.CreatedUtc, envelope.AppVersion, envelope.IsRecovery, envelope.Settings
        })));

    private string ValidateManagedPath(string filePath)
    {
        string path = Path.GetFullPath(filePath);
        if (!OwnedName.IsMatch(Path.GetFileName(path))
            || !string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(BackupDirectory), StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Select a backup from the current backup folder.");
        return path;
    }

    private void Prune(string directory, int maximum, string keepPath)
    {
        try
        {
            var files = ListCore(directory).Where(file => file.IsValid)
                .OrderByDescending(file => string.Equals(file.FilePath, keepPath, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (bool recovery in new[] { false, true })
                foreach (var old in files.Where(file => file.IsRecovery == recovery).Skip(recovery ? 1 : Math.Clamp(maximum, 1, 50)))
                    File.Delete(old.FilePath);
        }
        catch (Exception ex)
        {
            // The new copy is already valid. Surface retention failures without
            // pretending the copy failed or deleting anything else to compensate.
            _operationWarning = "The backup was created, but older backups could not be removed.";
            AppDiagnostics.LogError("backup.retention", ex);
        }
    }

    private Task OnSettingsContextAsync(Action action)
    {
        if (_settingsContext is null || ReferenceEquals(SynchronizationContext.Current, _settingsContext))
        {
            action();
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _settingsContext.Post(_ =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }, null);
        return completion.Task;
    }

    private void ReportFailure(Exception exception, bool notify = true)
    {
        LastError = exception.Message;
        AppDiagnostics.LogError("backup.operation", exception);
        if (notify) NotifyChanged();
    }

    private void NotifyChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { AppDiagnostics.LogWarning("backup.observer", ex.Message, ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _shutdown.Cancel();
    }

    private sealed class BackupEnvelope
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
        public string AppVersion { get; set; } = "";
        public bool IsRecovery { get; set; }
        public string Sha256 { get; set; } = "";
        public JsonElement Settings { get; set; }
    }
}
