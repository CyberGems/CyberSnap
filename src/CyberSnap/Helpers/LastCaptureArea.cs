using System.Drawing;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.Helpers;

/// <summary>Persists and resolves the last rectangle-mode capture region for instant repeat captures.</summary>
public static class LastCaptureArea
{
    public const int MinEdgePx = 8;

    /// <summary>Bitmap-relative selection from the overlay that should be remembered.</summary>
    public static bool CanPersist(Rectangle bitmapRelative, Rectangle captureBounds)
    {
        if (bitmapRelative.Width < MinEdgePx || bitmapRelative.Height < MinEdgePx)
            return false;

        // Skip full-screen selections (click without drag).
        if (bitmapRelative.X <= 1 && bitmapRelative.Y <= 1 &&
            bitmapRelative.Width >= captureBounds.Width - 2 &&
            bitmapRelative.Height >= captureBounds.Height - 2)
            return false;

        return true;
    }

    public static void PersistFromOverlaySelection(AppSettings settings, SettingsService settingsService,
        Rectangle bitmapRelative, Rectangle captureBounds)
    {
        if (!CanPersist(bitmapRelative, captureBounds))
            return;

        var screenRect = ToScreenRect(bitmapRelative, captureBounds);
        PersistScreenRect(settings, settingsService, screenRect);
    }

    public static void PersistScreenRect(AppSettings settings, SettingsService settingsService, Rectangle screenRect)
    {
        settings.HasLastCaptureRect = true;
        settings.LastCaptureRectX = screenRect.X;
        settings.LastCaptureRectY = screenRect.Y;
        settings.LastCaptureRectWidth = screenRect.Width;
        settings.LastCaptureRectHeight = screenRect.Height;
        try { settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("last-capture-area.persist", ex); }
    }

    public static bool TryGetScreenRect(AppSettings settings, out Rectangle screenRect)
    {
        screenRect = default;
        if (!settings.HasLastCaptureRect)
            return false;

        screenRect = new Rectangle(
            settings.LastCaptureRectX,
            settings.LastCaptureRectY,
            settings.LastCaptureRectWidth,
            settings.LastCaptureRectHeight);

        var virtualBounds = ScreenCapture.GetVirtualScreenBounds();
        screenRect = Rectangle.Intersect(screenRect, virtualBounds);
        return screenRect.Width >= MinEdgePx && screenRect.Height >= MinEdgePx;
    }

    private static Rectangle ToScreenRect(Rectangle bitmapRelative, Rectangle captureBounds) =>
        new(
            captureBounds.X + bitmapRelative.X,
            captureBounds.Y + bitmapRelative.Y,
            bitmapRelative.Width,
            bitmapRelative.Height);
}
