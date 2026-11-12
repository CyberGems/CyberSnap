using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Bitmap = System.Drawing.Bitmap;

internal static partial class Program
{
    private static void CheckPreviewCloseSound(CyberSnap.App app, SettingsService settings, string temp)
    {
        var s = settings.Settings;
        s.CapturePreviewTimeoutSeconds = 0;
        s.ShowCapturePreview = true;
        s.PreviewSuppressCloseAllConfirm = true;
        s.AfterCapture = AfterCaptureAction.PreviewOnly;
        s.SaveHistory = false;
        s.SaveToFile = true;
        s.CelebrationsEnabled = false;
        AutoCopyPreferences.SetKindEnabled(s, AutoCopyKind.Image, false);
        ToastWindow.SetNotificationsEnabled(true);

        const BindingFlags staticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
        var queueField = typeof(SoundService).GetField("_playbackQueue", staticPrivate)!;
        var threadField = typeof(SoundService).GetField("_playbackThread", staticPrivate)!;
        var sounds = (Dictionary<SoundEvent, byte[]>)typeof(SoundService).GetField("_customWavs", staticPrivate)!.GetValue(null)!;
        var currentSpec = typeof(ToastWindow).GetField("_currentSpec", staticPrivate)!;
        var completion = typeof(CapturePreviewDialog).GetEvent("SessionCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var handler = typeof(CyberSnap.App).GetMethod("CompletePreviewSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var completionHandler = Delegate.CreateDelegate(completion.EventHandlerType!, app, handler);

        // Observe real capture sound requests at the playback queue without playing test audio.
        using var playback = new BlockingCollection<byte[]>();
        byte[] captureWav = [1];
        sounds.Add(SoundEvent.Capture, captureWav);
        queueField.SetValue(null, playback);
        threadField.SetValue(null, Thread.CurrentThread);
        try
        {
            foreach (int tabs in new[] { 1, 3, 3 })
            {
                var paths = new HashSet<string>();
                var presentedPaths = new HashSet<string>();
                DispatcherHookEventHandler observeToast = (_, _) =>
                {
                    var spec = currentSpec.GetValue(null);
                    if (spec?.GetType().GetProperty("FilePath")?.GetValue(spec) is string path && paths.Contains(path))
                        presentedPaths.Add(path);
                };

                var (bitmap, filePath) = MakeCapture();
                var preview = new CapturePreviewDialog(bitmap, settings, savedFilePath: filePath);
                app.Dispatcher.Hooks.OperationCompleted += observeToast;
                try
                {
                    preview.Show();
                    for (int tab = 1; tab < tabs; tab++)
                    {
                        var (otherBitmap, otherPath) = MakeCapture();
                        Call(preview, "AddSession", otherBitmap, otherPath, false, CaptureKind.Screenshot);
                    }
                    completion.GetAddMethod(true)!.Invoke(preview, [completionHandler]);
                    Call(preview, "RequestPrimaryClose");
                    Assert(!preview.IsVisible, $"{tabs} capture tab(s): preview window closed");
                    AwaitCondition(() => presentedPaths.Count == tabs, $"Only {presentedPaths.Count} of {tabs} capture notifications appeared.", 10000);
                    var queued = playback.ToArray();
                    Assert(queued.Count(wav => ReferenceEquals(wav, captureWav)) == 1,
                        $"{tabs} capture tab(s): one playback for {presentedPaths.Count} notifications (queued {queued.Length})");
                }
                finally
                {
                    app.Dispatcher.Hooks.OperationCompleted -= observeToast;
                    if (preview.IsVisible) preview.CloseFromReplace();
                    ToastWindow.ForceDismissCurrent();
                    while (playback.TryTake(out _)) { }
                }

                (Bitmap, string) MakeCapture()
                {
                    var image = OutlineFixture();
                    string path = Path.Combine(temp, Guid.NewGuid() + ".png");
                    CaptureOutputService.SavePng(image, path);
                    paths.Add(path);
                    return (image, path);
                }
            }
        }
        finally
        {
            queueField.SetValue(null, null);
            threadField.SetValue(null, null);
            sounds.Remove(SoundEvent.Capture);
        }
    }
}
