using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Services;

namespace CyberSnap.UI;

public partial class CapturePreviewDialog
{
    private void CopyImagePath(PreviewSession session)
    {
        if (_isClosing || SavedPathIfExists(session) is not { } path)
            return;

        CancelAutoCloseOnInteraction();
        try
        {
            CopyPathText(path);
        }
        catch (Exception ex)
        {
            ToastWindow.ShowError(LocalizationService.Translate("Copy failed"), ex.Message);
        }
    }

    private static ProcessStartInfo PreparePaintImage(PreviewSession session)
    {
        string path = CaptureSavePath.BuildTempCapturePath(".png");
        try
        {
            CaptureOutputService.SavePng(session.EffectiveBitmap, path);
            // Let Windows resolve the classic Paint executable or its Store app
            // registration. Both Windows 10 and 11 accept the image as one argument.
            var startInfo = new ProcessStartInfo("mspaint.exe")
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.SystemDirectory
            };
            startInfo.ArgumentList.Add(path);
            return startInfo;
        }
        catch
        {
            CaptureSavePath.TryDeleteTempRecording(path);
            throw;
        }
    }

    private void SendToPaint(PreviewSession session)
    {
        if (_isClosing)
            return;

        CancelAutoCloseOnInteraction();
        ProcessStartInfo? startInfo = null;
        try
        {
            startInfo = PreparePaintImage(session);
            Process.Start(startInfo)?.Dispose();
            // Paint may reuse another process, so its launcher exiting is not a
            // safe cleanup signal. Keep the snapshot available in the OS temp folder.
        }
        catch (Exception ex)
        {
            if (startInfo is not null)
                CaptureSavePath.TryDeleteTempRecording(startInfo.ArgumentList[0]);
            ToastWindow.ShowError(LocalizationService.Translate("Open failed"),
                LocalizationService.Translate("Could not open the image in Paint.") + "\n" + ex.Message);
        }
    }

    private void RecenterPreviewOnStateChange()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosing || PreviewImage.Source is null || WindowState == WindowState.Minimized)
                return;

            ApplyZoom();
            ZoomViewport.UpdateLayout();
            // A larger viewport can coerce a centered absolute-size image's
            // offsets to zero. Restore its center after the new extents settle.
            ZoomViewport.ScrollToHorizontalOffset(ZoomViewport.ScrollableWidth / 2);
            ZoomViewport.ScrollToVerticalOffset(ZoomViewport.ScrollableHeight / 2);
        }), DispatcherPriority.Loaded);
    }
}
