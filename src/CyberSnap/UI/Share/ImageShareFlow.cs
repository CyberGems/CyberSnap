using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.Services.Upload;
using App = CyberSnap.App;

namespace CyberSnap.UI.Share;

/// <summary>
/// Shared share/upload flow used by the Editor and Gallery.
/// Encodes on the calling UI thread when given a Bitmap; file path loads off UI then encodes.
/// </summary>
public static class ImageShareFlow
{
    public const string ImgBbTermsUrl = "https://imgbb.com/tos";
    public const string ImgurTermsUrl = "https://imgur.com/tos";

    public static string? GetTermsOfServiceUrl(UploadProviderKind provider) => provider switch
    {
        UploadProviderKind.ImgBB => ImgBbTermsUrl,
        UploadProviderKind.Imgur => ImgurTermsUrl,
        _ => null,
    };

    /// <summary>
    /// Confirms third-party public upload when required. Returns false if the user cancels.
    /// </summary>
    public static bool ConfirmThirdPartyUploadIfNeeded(
        Window? ownerWindow,
        IntPtr ownerHandle,
        UploadProviderKind provider,
        AppSettings settings)
    {
        if (provider is not (UploadProviderKind.Imgur or UploadProviderKind.ImgBB))
            return true;
        if (settings.UploadSuppressThirdPartyConfirm)
            return true;

        var hostName = provider == UploadProviderKind.ImgBB ? "ImgBB" : "Imgur";
        var title = LocalizationService.Translate("Share image?");
        var message = string.Format(
            LocalizationService.Translate(
                "This image will be uploaded to {0}. Anyone with the link may view it. You are responsible for complying with the host's terms of service. Continue?"),
            hostName);

        var tosUrl = GetTermsOfServiceUrl(provider);
        bool ok = ownerWindow is not null
            ? ThemedConfirmDialog.Confirm(
                ownerWindow,
                title,
                message,
                out bool dontShowAgain,
                primaryText: LocalizationService.Translate("Share"),
                secondaryText: LocalizationService.Translate("Cancel"),
                danger: false,
                iconId: "share",
                messageLinkUrl: tosUrl,
                messageLinkLabel: LocalizationService.Translate("View terms of service"))
            : ThemedConfirmDialog.Confirm(
                ownerHandle,
                title,
                message,
                out dontShowAgain,
                primaryText: LocalizationService.Translate("Share"),
                secondaryText: LocalizationService.Translate("Cancel"),
                danger: false,
                iconId: "share",
                messageLinkUrl: tosUrl,
                messageLinkLabel: LocalizationService.Translate("View terms of service"));

        if (dontShowAgain && Application.Current is App app)
            app.PersistUploadSuppressThirdPartyConfirm(true);

        return ok;
    }

    public static async Task<UploadResult> ShareBitmapAsync(
        Bitmap bitmap,
        UploadProviderKind? providerOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var settings = SettingsService.LoadStatic() ?? new AppSettings();
        var provider = providerOverride ?? ImageUploadService.GetDefaultProvider(settings);

        if (provider == UploadProviderKind.Custom)
        {
            var probe = ImageUploadService.GetMenuProviders(settings)
                .FirstOrDefault(p => p.Kind == UploadProviderKind.Custom);
            if (!probe.Available)
            {
                return UploadResult.Fail(
                    provider,
                    UploadErrorKind.NotConfigured,
                    LocalizationService.Translate("Configure a custom destination in Settings → Uploads."));
            }
        }

        UploadImageEncoder.EncodedImage encoded;
        try
        {
            encoded = UploadImageEncoder.Encode(bitmap, settings.UploadImageFormat, settings.UploadJpegQuality);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("share.encode", ex);
            return UploadResult.Fail(
                provider,
                UploadErrorKind.EncodingFailed,
                LocalizationService.Translate("Upload failed"));
        }

        var fileName = ImageUploadService.BuildRemoteFileName(null, encoded.Extension);

        ToastWindow.Show(
            LocalizationService.Translate("Uploading…"),
            LocalizationService.Translate("Sharing your image…"));

        return await ImageUploadService.UploadEncodedAsync(
            encoded.Bytes,
            encoded.ContentType,
            fileName,
            new UploadRequest(provider),
            cancellationToken).ConfigureAwait(true);
    }

    public static async Task<UploadResult> ShareFileAsync(
        string filePath,
        UploadProviderKind? providerOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return UploadResult.Fail(
                providerOverride ?? UploadProviderKind.ImgBB,
                UploadErrorKind.NotConfigured,
                LocalizationService.Translate("Upload failed"));
        }

        using var bmp = new Bitmap(filePath);
        var result = await ShareBitmapAsync(bmp, providerOverride, cancellationToken).ConfigureAwait(true);
        return result;
    }

    public static void PresentResult(UploadResult result, AppSettings? settings = null)
    {
        settings ??= SettingsService.LoadStatic() ?? new AppSettings();

        if (!result.Success)
        {
            ToastWindow.ShowError(
                LocalizationService.Translate("Upload failed"),
                result.ErrorMessage ?? LocalizationService.Translate("Upload failed"));
            if (result.DefaultCredentialRejected && Application.Current is App app)
                app.ShowSettings("uploads");
            if (result.ErrorKind == UploadErrorKind.NotConfigured
                && result.Provider == UploadProviderKind.Custom
                && Application.Current is App app2)
            {
                app2.ShowSettings("uploads");
            }
            return;
        }

        var clipboard = result.ClipboardText ?? result.PublicUrl ?? result.RemotePath;
        if (!string.IsNullOrWhiteSpace(clipboard))
            ClipboardService.CopyTextToClipboard(clipboard);

        // Dedicated share sound (not the generic capture toast sound).
        SoundService.PlayUploadSound();

        if (result.HasOpenableHttpUrl && !string.IsNullOrWhiteSpace(result.PublicUrl))
        {
            ToastWindow.Show(new ToastSpec
            {
                Title = LocalizationService.Translate("Link copied"),
                Body = Truncate(result.PublicUrl!, 96),
                ClickActionUrl = result.PublicUrl,
                ClickActionLabel = LocalizationService.Translate("Open link"),
                IsSystemMessage = true,
                SuppressSound = true,
            });

            if (settings.UploadOpenUrlAfterSuccess)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(result.PublicUrl!) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    AppDiagnostics.LogWarning("upload.open-url", "Failed to open uploaded URL.", ex);
                }
            }
        }
        else
        {
            ToastWindow.Show(new ToastSpec
            {
                Title = LocalizationService.Translate("Uploaded"),
                Body = Truncate(clipboard ?? "", 96),
                IsSystemMessage = true,
                SuppressSound = true,
            });
        }
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..(max - 1)] + "…";
}
