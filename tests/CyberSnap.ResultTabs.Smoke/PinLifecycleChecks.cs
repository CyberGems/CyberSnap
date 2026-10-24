using System.Drawing;
using System.Reflection;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Services;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static Forms.ContextMenuStrip OpenPinContextMenu(PinImageForm pin)
    {
        Call(pin, "OpenMenu", new Point(12, 12));
        var filter = typeof(Forms.ToolStripManager).GetNestedType("ModalMenuFilter", BindingFlags.NonPublic)!;
        return (Forms.ContextMenuStrip)filter.GetMethod("GetActiveToolStrip", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null)!;
    }

    private static void CheckPinMenuLifecycle()
    {
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        var failures = new List<Exception>();
        System.Threading.ThreadExceptionEventHandler onFailure = (_, args) => failures.Add(args.Exception);
        Forms.Application.ThreadException += onFailure;
        void Drain()
        {
            Forms.Application.DoEvents(); Pump();
            Assert(failures.Count == 0, "Pin menu message processing has no unhandled WinForms exception: "
                + string.Join("; ", failures.Select(error => error.ToString())), false);
        }
        try
        {
            foreach (string language in new[] { "en", "es" })
            foreach (var screen in Forms.Screen.AllScreens)
            {
                LocalizationService.ApplyCurrentCulture(language);
                using var image = new Bitmap(320, 200);
                using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Teal);
                using var pin = new PinImageForm((Bitmap)image.Clone(),
                    new Point(screen.WorkingArea.X + 500, screen.WorkingArea.Y + 350), new AppSettings());
                pin.Show(); Drain();
                var reasons = new[] { Forms.ToolStripDropDownCloseReason.Keyboard, Forms.ToolStripDropDownCloseReason.AppClicked,
                    Forms.ToolStripDropDownCloseReason.AppFocusChange, Forms.ToolStripDropDownCloseReason.CloseCalled };
                for (int iteration = 0; iteration < 3; iteration++)
                foreach (var reason in reasons)
                {
                    var menu = OpenPinContextMenu(pin);
                    Assert(menu.Visible && !menu.IsDisposed,
                        "the production Pin menu opens normally during repeated use: " + reason);
                    bool disposedInsideClosed = false;
                    int disposals = 0;
                    menu.Closed += (_, _) => disposedInsideClosed = menu.IsDisposed;
                    menu.Disposed += (_, _) => disposals++;
                    menu.Close(reason);
                    Assert(!disposedInsideClosed, "Pin does not destroy its menu inside the WinForms close callback");
                    Drain();
                    Assert(menu.IsDisposed && disposals == 1,
                        "the closed menu and icons are released once, after the native close sequence finishes");
                }
                int copies = 0, saves = 0, edits = 0, options = 0;
                pin.CopyRequested += output => { using (output) copies++; };
                pin.SaveRequested += output =>
                {
                    using (output)
                    using (var modal = new Forms.Form { Size = new Size(180, 100), ShowInTaskbar = false,
                        StartPosition = Forms.FormStartPosition.CenterParent, Text = LocalizationService.Translate("Pin to screen") })
                    {
                        modal.Shown += (_, _) => modal.BeginInvoke(new Action(modal.Close));
                        modal.ShowDialog(pin); // Re-enter the message loop like the real Save dialog.
                        saves++;
                    }
                };
                pin.EditRequested += output => { using (output) edits++; };
                pin.OptionsRequested += () => options++;
                pin.SetImageScale(1.2); pin.Opacity = .7;
                for (int index = 0; index < 5; index++)
                {
                    var menu = OpenPinContextMenu(pin);
                    ((Forms.ToolStripMenuItem)menu.Items[index]).PerformClick();
                    Drain();
                    Assert(menu.IsDisposed && !pin.IsDisposed,
                        "an action from the still-open production menu releases it safely: " + index);
                }
                Assert(copies == 1 && saves == 1 && edits == 1 && options == 1 && pin.ImageScale == 1 && pin.Opacity == 1,
                    "copy, modal save, editor, reset and settings keep their behavior after menu cleanup");
                var closeMenu = OpenPinContextMenu(pin);
                ((Forms.ToolStripMenuItem)closeMenu.Items[5]).PerformClick();
                Drain();
                Assert(pin.IsDisposed && closeMenu.IsDisposed,
                    "Close from an open context menu safely releases both the Pin and its popup");
            }

            foreach (bool toolbar in new[] { false, true })
            {
                using var pin = new PinImageForm(new Bitmap(400, 260), new Point(600, 400), new AppSettings());
                ((Forms.Timer)Field(pin, "_hoverTimer")).Stop();
                pin.Show(); Drain();
                SetField(pin, "_hover", true);
                using (var frame = new Bitmap(pin.Width, pin.Height)) pin.DrawToBitmap(frame, new Rectangle(Point.Empty, frame.Size));
                var menu = OpenPinContextMenu(pin);
                Call(pin, "DisplayChanged", null!, EventArgs.Empty); // Queue work that outlives the owner.
                if (toolbar)
                {
                    var button = ((Rectangle[])Field(pin, "_buttons"))[2];
                    var point = new Point(button.X + button.Width / 2, button.Y + button.Height / 2);
                    var mouse = Forms.Message.Create(pin.Handle, 0x0201, new IntPtr(1), new IntPtr(point.X | (point.Y << 16)));
                    Forms.Application.FilterMessage(ref mouse); // Real outside-click modal-menu filtering.
                    Call(pin, "OnMouseDown", new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, point.X, point.Y, 0));
                }
                else pin.Close();
                Drain();
                Assert(pin.IsDisposed && menu.IsDisposed,
                    "closing Pin with its menu open also drains pending display callbacks: toolbar=" + toolbar);
                Call(pin, "OpenMenu", new Point(12, 12));
                Assert(!pin.IsHandleCreated, "a late menu request cannot recreate the disposed Pin HWND");
            }
        }
        finally
        {
            Forms.Application.ThreadException -= onFailure;
            LocalizationService.ApplyCurrentCulture(originalLanguage);
        }
    }
}
