using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CyberSnap.Helpers;

/// <summary>
/// Deterministic toggle support for ContextMenu triggers (⋯/⋮/burger buttons).
/// An open ContextMenu captures the mouse, so the second press on its trigger never
/// reaches Click cleanly: auto-dismiss races it and IsMouseOver reads stale, and the
/// menu just "refreshes". Instead, when the menu auto-dismisses we check the PHYSICAL
/// cursor position (immune to capture): if the dismissing press landed on the trigger,
/// the upcoming Click is a toggle-off and gets consumed. Call <see cref="Arm"/> from
/// the menu's Closed handler and <see cref="Consume"/> at the top of the trigger's
/// click path. All calls are UI-thread only, like the rest of the suite.
/// </summary>
public static class MenuReopenSuppress
{
    private static readonly Dictionary<ContextMenu, DateTime> s_armed = new();
    private static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Call from the menu's Closed handler: arms toggle-off when the dismissing press
    /// is still down over <paramref name="trigger"/>.
    /// </summary>
    public static void ArmIfCursorOverTrigger(ContextMenu? menu, FrameworkElement? trigger)
    {
        try
        {
            Prune();
            if (menu is null || trigger is null)
                return;
            // DIAG-TEMP: second-click toggle diagnosis (remove after root cause found).
            int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(menu);
            if (!trigger.IsVisible || trigger.ActualWidth <= 0 || trigger.ActualHeight <= 0)
            {
                Services.AppDiagnostics.LogInfo("menudiag-arm", $"menu={id:x} NOT-ARMED trigger not laid out");
                return;
            }
            var cursor = System.Windows.Forms.Cursor.Position;
            var topLeft = trigger.PointToScreen(new System.Windows.Point(0, 0));
            var source = PresentationSource.FromVisual(trigger);
            var toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            var size = toDevice.Transform(new Vector(trigger.ActualWidth, trigger.ActualHeight));
            bool over = cursor.X >= topLeft.X && cursor.X <= topLeft.X + size.X
                && cursor.Y >= topLeft.Y && cursor.Y <= topLeft.Y + size.Y;
            Services.AppDiagnostics.LogInfo("menudiag-arm",
                $"menu={id:x} cursor=({cursor.X},{cursor.Y}) rect=({topLeft.X:F0},{topLeft.Y:F0},{topLeft.X + size.X:F0},{topLeft.Y + size.Y:F0}) over={over} pressed={System.Windows.Input.Mouse.LeftButton}");
            if (over)
                s_armed[menu] = DateTime.UtcNow;
        }
        catch { }
    }

    /// <summary>
    /// Call at the top of the trigger's click path: returns true when this press
    /// already dismissed the menu and the menu must stay closed.
    /// </summary>
    public static bool Consume(ContextMenu? menu)
    {
        try
        {
            if (menu is null)
                return false;
            // DIAG-TEMP: second-click toggle diagnosis (remove after root cause found).
            int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(menu);
            if (s_armed.TryGetValue(menu, out var armedAt))
            {
                s_armed.Remove(menu);
                bool fresh = DateTime.UtcNow - armedAt < FreshWindow;
                Services.AppDiagnostics.LogInfo("menudiag-consume", $"menu={id:x} ARMED age={(DateTime.UtcNow - armedAt).TotalMilliseconds:F0}ms -> suppress={fresh}");
                return fresh;
            }
            Services.AppDiagnostics.LogInfo("menudiag-consume", $"menu={id:x} NOT-armed -> proceed");
            return false;
        }
        catch { }

        return false;
    }

    /// <summary>Clears a stale arm (e.g. the menu was reopened through another path).</summary>
    public static void Clear(ContextMenu? menu)
    {
        try
        {
            if (menu is not null)
                s_armed.Remove(menu);
        }
        catch { }
    }

    private static void Prune()
    {
        try
        {
            if (s_armed.Count == 0)
                return;
            var now = DateTime.UtcNow;
            ContextMenu? stale = null;
            foreach (var pair in s_armed)
            {
                if (now - pair.Value >= FreshWindow)
                {
                    stale = pair.Key;
                    break;
                }
            }

            if (stale is not null)
                s_armed.Remove(stale);
        }
        catch { }
    }

    /// <summary>Physical cursor hit-test: immune to WPF mouse capture while a menu is open.</summary>
    public static bool IsCursorOver(FrameworkElement trigger)
    {
        try
        {
            if (trigger is null || !trigger.IsVisible || trigger.ActualWidth <= 0 || trigger.ActualHeight <= 0)
                return false;
            var cursor = System.Windows.Forms.Cursor.Position;
            var topLeft = trigger.PointToScreen(new System.Windows.Point(0, 0));
            var source = PresentationSource.FromVisual(trigger);
            var toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            var size = toDevice.Transform(new Vector(trigger.ActualWidth, trigger.ActualHeight));
            return cursor.X >= topLeft.X && cursor.X <= topLeft.X + size.X
                && cursor.Y >= topLeft.Y && cursor.Y <= topLeft.Y + size.Y;
        }
        catch
        {
            return false;
        }
    }
}
