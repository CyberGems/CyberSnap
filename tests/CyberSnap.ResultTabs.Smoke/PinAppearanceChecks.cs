using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void CheckPinImageAppearance(string? renders)
    {
        if (renders is not null) Directory.CreateDirectory(renders);
        try
        {
            foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Light })
            foreach (var screen in Forms.Screen.AllScreens)
            {
                Theme.SetMode(theme);
                using var image = new Bitmap(320, 200);
                using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Magenta);
                using var pin = new PinImageForm((Bitmap)image.Clone(),
                    new Point(screen.WorkingArea.X + 400, screen.WorkingArea.Y + 350),
                    new AppSettings { PinIncludeInCaptures = true });
                ((Forms.Timer)Field(pin, "_hoverTimer")).Stop();
                pin.Show(); Forms.Application.DoEvents();
                foreach (double scale in new[] { .2, 1.0, 1.25 })
                foreach (bool border in new[] { true, false })
                foreach (bool hover in new[] { false, true })
                {
                    pin.SetImageScale(scale);
                    pin.ApplyAppearancePreferences(true, border);
                    SetField(pin, "_hover", hover);
                    using var frame = new Bitmap(pin.ClientSize.Width, pin.ClientSize.Height);
                    pin.DrawToBitmap(frame, new Rectangle(Point.Empty, frame.Size));
                    var edges = new[] { frame.GetPixel(frame.Width / 2, 0), frame.GetPixel(frame.Width / 2, frame.Height - 1),
                        frame.GetPixel(0, frame.Height / 2), frame.GetPixel(frame.Width - 1, frame.Height / 2) };
                    Assert(edges.All(color => color.ToArgb() == edges[0].ToArgb()),
                        $"Pin's four outer edges have the same color: {theme}, {screen.DeviceName}, zoom={scale}, hover={hover}, edges={string.Join(",", edges)}");
                    if (hover)
                        Assert(edges[0].R == Theme.Accent.R && edges[0].G == Theme.Accent.G && edges[0].B == Theme.Accent.B,
                            "hover highlights every edge in the current theme's accent");
                    else if (!border)
                        Assert(edges[0].ToArgb() == Color.Magenta.ToArgb(), "disabling the border reveals image pixels on all four edges without a gray gutter");
                    if (scale == 1.25)
                    {
                        uint nativeDpi = GetDpiForWindow(pin.Handle);
                        Assert(GetWindowDisplayAffinity(pin.Handle, out uint affinity) && affinity == 0,
                            "the synthetic Pin permits desktop inspection without changing user capture preferences");
                        var shadow = (Forms.Form)Field(pin, "_shadow");
                        Assert(GetWindowDisplayAffinity(shadow.Handle, out uint shadowAffinity) && shadowAffinity == 0
                            && IsWidgetToolTipAbove(pin.Handle, shadow.Handle),
                            "an included shadow is below its Pin and also permits capture");
                        pin.BringToFront(); pin.Activate();
                        Assert(SetShadowTestWindowPos(pin.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010),
                            "the native pixel fixture places its own Pin above other desktop windows without taking input focus");
                        Forms.Application.DoEvents();
                        pin.Refresh();
                        // A rapid series of zooms can leave the compositor on the
                        // previous window size after the synchronous GDI paint.
                        Thread.Sleep(100);
                        Marshal.ThrowExceptionForHR(FlushZoomTransitionComposition());
                        var origin = pin.PointToScreen(Point.Empty);
                        using var desktop = ScreenCapture.CaptureRegion(new Rectangle(origin, pin.ClientSize));
                        var presented = new[] { desktop.GetPixel(desktop.Width / 2, 0), desktop.GetPixel(desktop.Width / 2, desktop.Height - 1),
                            desktop.GetPixel(0, desktop.Height / 2), desktop.GetPixel(desktop.Width - 1, desktop.Height / 2) };
                        if (renders is not null)
                            desktop.Save(Path.Combine(renders, $"pin-frame-{theme}-{nativeDpi}-border{border}-hover{hover}.png"));
                        if (!presented.All(color => color.ToArgb() == edges[0].ToArgb()))
                        {
                            Console.WriteLine($"Pin inspection before compositor settlement: form={pin.Bounds}, client={pin.ClientRectangle}, origin={origin}, visible={pin.Visible}, foreground={GetForegroundWindow()}, pin={pin.Handle}, shadow={shadow.Handle}.");
                            Thread.Sleep(300);
                            Marshal.ThrowExceptionForHR(FlushZoomTransitionComposition());
                            using var settled = ScreenCapture.CaptureRegion(new Rectangle(origin, pin.ClientSize));
                            Console.WriteLine($"Pin inspection after compositor settlement: top={settled.GetPixel(settled.Width / 2, 0)}, center={settled.GetPixel(settled.Width / 2, settled.Height / 2)}.");
                        }
                        Assert(presented.All(color => color.ToArgb() == edges[0].ToArgb()),
                            $"the real desktop shows the same frame on all four sides at monitor DPI {nativeDpi}, hover={hover}: {string.Join(",", presented)}");
                    }
                }
            }
        }
        finally { Theme.SetMode(AppThemeMode.System); }
        CheckPinWindowShadow(renders);
        CheckPinImageMenus(renders);
        CheckPinMenuPlacement(renders);
    }

    private static void CheckPinImageMenus(string? renders)
    {
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        var labels = new[] { "Copy original image", "Save as...", "Send to Editor...", "Opacity", "Reset zoom and opacity", "Pin settings", "Close" };
        var icons = new[] { "copy", "save", "compose", "eye", "zoomReset", "gear", "close" };
        try
        {
            foreach (string language in new[] { "en", "es" })
            foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Light })
            {
                LocalizationService.ApplyCurrentCulture(language); Theme.SetMode(theme);
                using var image = new Bitmap(320, 200);
                using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Teal);
                using var pin = new PinImageForm((Bitmap)image.Clone(), new Point(500, 400), new AppSettings());
                pin.Show(); Forms.Application.DoEvents();
                using var menu = (Forms.ContextMenuStrip)Call(pin, "BuildContextMenu")!;
                var items = menu.Items.OfType<Forms.ToolStripMenuItem>().ToArray();
                Assert(menu.ShowImageMargin && items.Length == labels.Length
                    && items.Select(item => item.Text).SequenceEqual(labels.Select(LocalizationService.Translate)),
                    "all Pin context commands retain their localized labels and reserve an icon column: " + language);
                for (int index = 0; index < items.Length; index++)
                {
                    Assert(FluentIcons.HasIcon(icons[index]) && items[index].Image is Bitmap,
                        "each Pin context command has an icon from the existing family: " + labels[index]);
                    using var expectedItem = WindowsMenuRenderer.Item(LocalizationService.Translate(labels[index]), iconId: icons[index]);
                    using var expected = expectedItem.Image as Bitmap;
                    Assert(expected is not null && EqualZoomBitmaps((Bitmap)items[index].Image!, expected),
                        "each command uses its intended icon and theme color: " + labels[index]);
                    int text = Forms.TextRenderer.MeasureText(items[index].Text, items[index].Font).Width;
                    Assert(text <= items[index].Width - 55, "the icon column leaves room for the full localized command");
                }
                menu.Show(pin, new Point(10, 10)); Forms.Application.DoEvents();
                if (renders is not null)
                {
                    using var snapshot = new Bitmap(menu.Width, menu.Height);
                    menu.DrawToBitmap(snapshot, new Rectangle(Point.Empty, snapshot.Size));
                    snapshot.Save(Path.Combine(renders, $"pin-context-menu-{language}-{theme}.png"));
                }
                menu.Close();
                var opacityItems = items[3].DropDownItems.OfType<Forms.ToolStripMenuItem>().ToArray();
                Assert(opacityItems.Length == 10 && opacityItems.All(item => item.Image is Bitmap)
                    && items[3].ToolTipText == LocalizationService.Translate("Ctrl + wheel changes opacity."),
                    "opacity has direct percentage choices, icons and a localized wheel shortcut hint");
                Assert(Equals(opacityItems[9].Tag, true), "the current opacity is highlighted when opening its menu");
                double zoomBeforeOpacity = pin.ImageScale;
                for (int index = 0; index < opacityItems.Length; index++)
                {
                    opacityItems[index].PerformClick();
                    Assert(Math.Abs(pin.Opacity - (index + 1) / 10d) < .001 && pin.ImageScale == zoomBeforeOpacity,
                        "an opacity choice changes transparency without changing the image zoom");
                }
                int copies = 0, saves = 0, edits = 0, options = 0;
                void Receive(Bitmap output, Action count)
                {
                    using (output)
                        Assert(EqualZoomBitmaps(image, output), "menu commands deliver the unchanged original image");
                    count();
                }
                pin.CopyRequested += output => Receive(output, () => copies++);
                pin.SaveRequested += output => Receive(output, () => saves++);
                pin.EditRequested += output => Receive(output, () => edits++);
                pin.OptionsRequested += () => options++;
                for (int index = 0; index < 3; index++) items[index].PerformClick();
                pin.SetImageScale(1.25); pin.Opacity = .5;
                items[4].PerformClick(); items[5].PerformClick();
                Assert(copies == 1 && saves == 1 && edits == 1 && options == 1 && pin.ImageScale == 1 && pin.Opacity == 1,
                    "adding icons preserves the copy, save, editor, reset and settings actions");
                items[6].PerformClick();
                Assert(pin.IsDisposed, "the context menu's close command still closes only this Pin");
            }
        }
        finally { LocalizationService.ApplyCurrentCulture(originalLanguage); Theme.SetMode(AppThemeMode.System); }
    }
}
