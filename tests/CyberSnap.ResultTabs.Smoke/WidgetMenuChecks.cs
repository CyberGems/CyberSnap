using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CyberSnap.Services;
using CyberSnap.UI;
using CyberSnap.Models;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void CheckWidgetMenu(CaptureWidgetWindow widget, SettingsService settings, string? renderDirectory)
    {
        string language = settings.Settings.InterfaceLanguage;
        string Tr(string text) => LocalizationService.Translate(language, text);
        using (var menu = (Forms.ContextMenuStrip)Call(widget, "BuildMoreMenu")!)
        {
            Assert(menu.Items.OfType<Forms.ToolStripMenuItem>().Select(item => item.Text).SequenceEqual(
                new[] { "Widget", "Image capture", "Scrolling capture", "Video (MP4)", "GIF", "OCR", "Color picker", "QR and Barcodes", "Annotations Editor...", "Configuration...", "Help" }.Select(Tr)),
                "widget More groups widget options first and image capture second in " + language, false);
            Assert(menu.Items[1] is Forms.ToolStripSeparator && menu.Items[9] is Forms.ToolStripSeparator,
                "Widget, capture modes and application commands are separated", false);
            var widgetOptions = (Forms.ToolStripMenuItem)menu.Items[0];
            Assert(menu.ShowImageMargin && menu.Items.OfType<Forms.ToolStripMenuItem>()
                .Concat(widgetOptions.DropDownItems.OfType<Forms.ToolStripMenuItem>().Where(item => item.HasDropDownItems))
                .All(item => item.Image is not null && item.ImageScaling == Forms.ToolStripItemImageScaling.None),
                "every widget More entry and Widget submenu has its own unscaled icon", false);
            var capture = (Forms.ToolStripMenuItem)menu.Items[2];
            Assert(capture.DropDownItems.OfType<Forms.ToolStripMenuItem>().Select(item => item.Text).SequenceEqual(
                new[] { "Auto-copy", "Show preview after capture", "Show magnifier", "Show selection size", "Show crosshair guides", "Detect windows" }.Select(Tr)),
                "image capture submenu contains delivery choices and all four localized capture options", false);
            var copyFlags = CopyFlags(settings.Settings);
            bool magnifier = settings.Settings.ShowCaptureMagnifier;
            capture.DropDownItems[3].PerformClick();
            Assert(settings.Settings.ShowCaptureMagnifier != magnifier && CopyFlags(settings.Settings).SequenceEqual(copyFlags),
                "nested capture toggle changes its own preference without changing clipboard delivery", false);
            capture.DropDownItems[3].PerformClick();
        }
        if (settings.Settings.WidgetDockEdge == CaptureDockSide.Top)
            CheckWidgetDelivery(widget, settings, renderDirectory);

        var button = Element<Button>(widget, "WidgetMoreButton");
        Call(widget, "More_Click", button, new RoutedEventArgs());
        Pump();
        var openMenu = GetField<Forms.ContextMenuStrip>(widget, "_contextMenu");
        var buttonOrigin = button.PointToScreen(new Point());
        var buttonEnd = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
        var buttonBounds = new System.Drawing.RectangleF((float)buttonOrigin.X, (float)buttonOrigin.Y,
            (float)(buttonEnd.X - buttonOrigin.X), (float)(buttonEnd.Y - buttonOrigin.Y));
        Assert(openMenu.Visible && GetField<bool>(widget, "_moreMenuOpen") && GetField<bool>(widget, "_contextMenuOpen"),
            "More opens exactly one tracked menu", false);
        Assert(button.Background == (Brush)widget.FindResource("WidgetButtonHoverBg"),
            "More keeps its highlighted background while the menu is open", false);
        Assert(!openMenu.Bounds.IntersectsWith(System.Drawing.Rectangle.Ceiling(buttonBounds))
            && Forms.Screen.FromRectangle(openMenu.Bounds).WorkingArea.Contains(openMenu.Bounds),
            "More menu stays within its monitor and leaves its button available for another click", false);
        Assert(!GetField<System.Windows.Threading.DispatcherTimer>(widget, "_collapseTimer").IsEnabled,
            "widget cannot auto-collapse while its menu is open", false);
        if (renderDirectory is not null)
        {
            Render(widget, renderDirectory, $"widget-more-open-{language}-{settings.Settings.WidgetDockEdge}.png");
            using var bitmap = new System.Drawing.Bitmap(openMenu.Width, openMenu.Height);
            openMenu.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(Path.Combine(renderDirectory, $"widget-more-menu-{language}-{settings.Settings.WidgetDockEdge}.png"));
            var widgetOptions = (Forms.ToolStripMenuItem)openMenu.Items[0];
            widgetOptions.ShowDropDown();
            Pump();
            using var submenuBitmap = new System.Drawing.Bitmap(widgetOptions.DropDown.Width, widgetOptions.DropDown.Height);
            widgetOptions.DropDown.DrawToBitmap(submenuBitmap, new System.Drawing.Rectangle(0, 0, submenuBitmap.Width, submenuBitmap.Height));
            submenuBitmap.Save(Path.Combine(renderDirectory, $"widget-options-menu-{language}-{settings.Settings.WidgetDockEdge}.png"));
            var screenOption = widgetOptions.DropDownItems.OfType<Forms.ToolStripMenuItem>()
                .SingleOrDefault(item => item.Text == Tr("Screen"));
            if (screenOption is not null)
            {
                screenOption.ShowDropDown();
                Pump();
                using var screenBitmap = new System.Drawing.Bitmap(screenOption.DropDown.Width, screenOption.DropDown.Height);
                screenOption.DropDown.DrawToBitmap(screenBitmap, new System.Drawing.Rectangle(0, 0, screenBitmap.Width, screenBitmap.Height));
                screenBitmap.Save(Path.Combine(renderDirectory, $"widget-screen-menu-{language}-{settings.Settings.WidgetDockEdge}.png"));
                screenOption.HideDropDown();
            }
            widgetOptions.HideDropDown();
            foreach (string key in new[] { "Position", "Activation delay" })
            {
                widgetOptions.ShowDropDown();
                var option = widgetOptions.DropDownItems.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == Tr(key));
                option.ShowDropDown();
                Pump();
                using var choiceBitmap = new System.Drawing.Bitmap(option.DropDown.Width, option.DropDown.Height);
                option.DropDown.DrawToBitmap(choiceBitmap, new System.Drawing.Rectangle(0, 0, choiceBitmap.Width, choiceBitmap.Height));
                choiceBitmap.Save(Path.Combine(renderDirectory, $"widget-{key.Replace(' ', '-')}-menu-{language}-{settings.Settings.WidgetDockEdge}.png"));
                option.HideDropDown();
                widgetOptions.HideDropDown();
            }
        }
        Call(widget, "More_Click", button, new RoutedEventArgs());
        Pump();
        Assert(!GetField<bool>(widget, "_moreMenuOpen") && !GetField<bool>(widget, "_contextMenuOpen") && openMenu.IsDisposed,
            "second More activation closes and releases the menu", false);
        Assert(button.ReadLocalValue(Control.BackgroundProperty) == DependencyProperty.UnsetValue,
            "closing More clears its persistent highlight", false);

        Call(widget, "More_Click", button, new RoutedEventArgs());
        var reopened = GetField<Forms.ContextMenuStrip>(widget, "_contextMenu");
        reopened.Close(Forms.ToolStripDropDownCloseReason.Keyboard);
        Pump();
        Assert(!GetField<bool>(widget, "_moreMenuOpen") && reopened.IsDisposed,
            "keyboard dismissal clears More and allows reopening", false);
        Call(widget, "More_Click", button, new RoutedEventArgs());
        var finalMenu = GetField<Forms.ContextMenuStrip>(widget, "_contextMenu");
        Call(widget, "ShowWidgetContextMenu");
        var replacement = GetField<Forms.ContextMenuStrip>(widget, "_contextMenu");
        string closeReason = "none";
        replacement.Closed += (_, e) => closeReason = e.CloseReason.ToString();
        Assert(finalMenu.IsDisposed && replacement.Visible,
            "a replacement opens only after the old native menu is released", false);
        Pump();
        Assert(finalMenu.IsDisposed && GetField<bool>(widget, "_contextMenuOpen") && !GetField<bool>(widget, "_moreMenuOpen"),
            $"the widget context menu replaces More without retaining its highlight ({language}, {settings.Settings.WidgetDockEdge}: "
                + $"oldDisposed={finalMenu.IsDisposed}, contextOpen={GetField<bool>(widget, "_contextMenuOpen")}, moreOpen={GetField<bool>(widget, "_moreMenuOpen")}, closed={closeReason})", false);
        GetField<Forms.ContextMenuStrip>(widget, "_contextMenu").Close(Forms.ToolStripDropDownCloseReason.AppClicked);
        Pump();
        Assert(!GetField<bool>(widget, "_contextMenuOpen"), "outside-click dismissal releases the context menu state", false);
    }
}
