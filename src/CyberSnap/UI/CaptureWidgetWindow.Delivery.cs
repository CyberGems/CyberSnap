using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using Forms = System.Windows.Forms;

namespace CyberSnap.UI;

public partial class CaptureWidgetWindow
{
    private Forms.ToolStripMenuItem BuildDeliverySubmenu(string label, AutoCopyKind kind)
    {
        var menu = WindowsMenuRenderer.Submenu(LocalizationService.Translate(label), showImages: true);
        AddDeliveryOptions(menu, kind);
        WindowsMenuRenderer.NormalizeDropDownWidths(menu, minWidth: 240);
        return menu;
    }

    private void AddDeliveryOptions(Forms.ToolStripMenuItem menu, AutoCopyKind kind)
    {
        menu.DropDownItems.Add(BuildDeliveryToggle("Auto-copy",
            settings => AutoCopyPreferences.ShouldCopy(settings, kind),
            (settings, enabled) =>
            {
                if (kind is AutoCopyKind.Image or AutoCopyKind.Scroll)
                {
                    var captureKind = kind == AutoCopyKind.Scroll ? CaptureKind.ScrollCapture : CaptureKind.Screenshot;
                    var outcome = AfterCaptureOutcomeModel.FromSettings(settings, captureKind) with { Clipboard = enabled };
                    AfterCaptureOutcomeModel.ApplyToSettings(outcome, settings, captureKind);
                }
                else ResultDelivery.SetCopyEnabled(settings, kind, enabled);
            }));

        string windowLabel = kind switch
        {
            AutoCopyKind.Image or AutoCopyKind.Scroll => "Show preview after capture",
            AutoCopyKind.Video or AutoCopyKind.Gif => "Outcome step: open trimmer",
            _ => "Show result window"
        };
        menu.DropDownItems.Add(BuildDeliveryToggle(windowLabel,
            settings => kind switch
            {
                AutoCopyKind.Image => AfterCaptureOutcomeModel.FromSettings(settings).Preview,
                AutoCopyKind.Scroll => AfterCaptureOutcomeModel.FromSettings(settings, CaptureKind.ScrollCapture).Preview,
                AutoCopyKind.Video => settings.OpenVideoTrimmerAfterCapture,
                AutoCopyKind.Gif => settings.OpenGifTrimmerAfterCapture,
                AutoCopyKind.Ocr => ResultDelivery.ForOcr(settings).ShowWindow,
                AutoCopyKind.Scan => ResultDelivery.ForScan(settings).ShowWindow,
                AutoCopyKind.Color => ResultDelivery.ForColor(settings).ShowWindow,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            },
            (settings, show) =>
            {
                switch (kind)
                {
                    case AutoCopyKind.Image:
                    case AutoCopyKind.Scroll:
                        var captureKind = kind == AutoCopyKind.Scroll ? CaptureKind.ScrollCapture : CaptureKind.Screenshot;
                        var outcome = AfterCaptureOutcomeModel.FromSettings(settings, captureKind) with { Preview = show };
                        AfterCaptureOutcomeModel.ApplyToSettings(outcome, settings, captureKind);
                        break;
                    case AutoCopyKind.Video: settings.OpenVideoTrimmerAfterCapture = show; break;
                    case AutoCopyKind.Gif: settings.OpenGifTrimmerAfterCapture = show; break;
                    default: ResultDelivery.SetShowWindow(settings, kind, show); break;
                }
            }));
    }

    private Forms.ToolStripMenuItem BuildDeliveryToggle(string label, Func<AppSettings, bool> get, Action<AppSettings, bool> set)
    {
        var item = BuildCaptureToggle(label, get(_settings), () =>
        {
            // Other result windows may own a separate SettingsService. Read their
            // latest delivery choices before changing just this mode.
            _settingsService.RefreshResultDeliveryPreferences();
            var settings = _settings;
            set(settings, !get(settings));
        }, publishDelivery: true);
        item.Tag = get;
        return item;
    }

    private void OnDeliveryPreferencesChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnDeliveryPreferencesChanged);
            return;
        }
        _settingsService.RefreshResultDeliveryPreferences();
        if (_contextMenu is not { IsDisposed: false } menu) return;
        RefreshItems(menu.Items);

        void RefreshItems(Forms.ToolStripItemCollection items)
        {
            foreach (var item in items.OfType<Forms.ToolStripMenuItem>())
            {
                if (item.Tag is Func<AppSettings, bool> get && get(_settings) != (item.Image is not null))
                {
                    var oldImage = item.Image;
                    item.Image = get(_settings) ? FluentIcons.RenderBitmap("check",
                        System.Drawing.Color.FromArgb(255, UiChrome.SurfaceTextPrimary.R,
                            UiChrome.SurfaceTextPrimary.G, UiChrome.SurfaceTextPrimary.B), 20, true) : null;
                    item.ImageScaling = Forms.ToolStripItemImageScaling.None;
                    oldImage?.Dispose();
                }
                RefreshItems(item.DropDownItems);
            }
        }
    }
}
