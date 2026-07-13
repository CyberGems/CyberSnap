using System.IO;
using System.Text.Json;
using CyberSnap.Models;

namespace CyberSnap.Services.Upload;

/// <summary>
/// Client-side abuse controls: minimum interval between uploads, and a soft daily cap
/// that applies only when using OOTB (shared) credentials (K21).
/// </summary>
internal static class ClientRateLimiter
{
    private static readonly object Gate = new();
    private static RateState? _state;

    private sealed class RateState
    {
        public DateTime LastUploadUtc { get; set; }
        public string DayKey { get; set; } = "";
        public int OotbCountToday { get; set; }
    }

    public readonly record struct RateLimitDecision(bool Allowed, UploadErrorKind ErrorKind, string? Message);

    public static RateLimitDecision TryAcquire(UploadRuntimeConfig config, bool usingOotbCredential)
    {
        lock (Gate)
        {
            var state = LoadState_NoLock();
            var now = DateTime.UtcNow;

            if (config.MinIntervalMs > 0 && state.LastUploadUtc != default)
            {
                var elapsed = (now - state.LastUploadUtc).TotalMilliseconds;
                if (elapsed < config.MinIntervalMs)
                {
                    return new RateLimitDecision(
                        false,
                        UploadErrorKind.RateLimited,
                        LocalizationService.Translate("Please wait a moment before uploading again."));
                }
            }

            // Soft daily cap: OOTB credentials only (K21).
            if (usingOotbCredential && config.DailyCapOotb > 0)
            {
                var dayKey = now.ToString("yyyy-MM-dd");
                if (!string.Equals(state.DayKey, dayKey, StringComparison.Ordinal))
                {
                    state.DayKey = dayKey;
                    state.OotbCountToday = 0;
                }

                if (state.OotbCountToday >= config.DailyCapOotb)
                {
                    return new RateLimitDecision(
                        false,
                        UploadErrorKind.RateLimited,
                        LocalizationService.Translate(
                            "Daily upload limit reached for CyberSnap's shared key. Add your own API key in Settings → Upload."));
                }
            }

            return new RateLimitDecision(true, UploadErrorKind.None, null);
        }
    }

    public static void RecordSuccess(bool usingOotbCredential)
    {
        lock (Gate)
        {
            var state = LoadState_NoLock();
            var now = DateTime.UtcNow;
            state.LastUploadUtc = now;

            if (usingOotbCredential)
            {
                var dayKey = now.ToString("yyyy-MM-dd");
                if (!string.Equals(state.DayKey, dayKey, StringComparison.Ordinal))
                {
                    state.DayKey = dayKey;
                    state.OotbCountToday = 0;
                }
                state.OotbCountToday++;
            }

            SaveState_NoLock(state);
        }
    }

    private static string StatePath =>
        Path.Combine(
            Path.GetDirectoryName(AppStoragePaths.SettingsPath) ?? AppContext.BaseDirectory,
            "upload-rate.json");

    private static RateState LoadState_NoLock()
    {
        if (_state is not null)
            return _state;

        try
        {
            if (File.Exists(StatePath))
            {
                var json = File.ReadAllText(StatePath);
                var dto = JsonSerializer.Deserialize<RateStateDto>(json);
                if (dto is not null)
                {
                    _state = new RateState
                    {
                        LastUploadUtc = dto.LastUploadUtc,
                        DayKey = dto.DayKey ?? "",
                        OotbCountToday = dto.OotbCountToday,
                    };
                    return _state;
                }
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("upload.rate-load", ex.Message, ex);
        }

        _state = new RateState();
        return _state;
    }

    private static void SaveState_NoLock(RateState state)
    {
        _state = state;
        try
        {
            var dir = Path.GetDirectoryName(StatePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var dto = new RateStateDto
            {
                LastUploadUtc = state.LastUploadUtc,
                DayKey = state.DayKey,
                OotbCountToday = state.OotbCountToday,
            };
            File.WriteAllText(StatePath, JsonSerializer.Serialize(dto));
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("upload.rate-save", ex.Message, ex);
        }
    }

    private sealed class RateStateDto
    {
        public DateTime LastUploadUtc { get; set; }
        public string? DayKey { get; set; }
        public int OotbCountToday { get; set; }
    }
}
