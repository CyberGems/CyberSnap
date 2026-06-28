using System.Drawing;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.Helpers;

/// <summary>Persists and resolves the last scroll capture region for instant repeat captures.</summary>
public static class LastScrollArea
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
        settings.HasLastScrollRect = true;
        settings.LastScrollRectX = screenRect.X;
        settings.LastScrollRectY = screenRect.Y;
        settings.LastScrollRectWidth = screenRect.Width;
        settings.LastScrollRectHeight = screenRect.Height;
        try { settingsService.Save(); }
        catch (Exception ex) { AppDiagnostics.LogError("last-scroll-area.persist", ex); }
    }

    public static bool TryGetScreenRect(AppSettings settings, out Rectangle screenRect)
    {
        screenRect = default;
        if (!settings.HasLastScrollRect)
            return false;

        screenRect = new Rectangle(
            settings.LastScrollRectX,
            settings.LastScrollRectY,
            settings.LastScrollRectWidth,
            settings.LastScrollRectHeight);

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