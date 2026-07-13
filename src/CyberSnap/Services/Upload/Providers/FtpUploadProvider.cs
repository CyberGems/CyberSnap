using System.IO;
using System.Security.Authentication;
using CyberSnap.Models;
using FluentFTP;

namespace CyberSnap.Services.Upload.Providers;

internal sealed class FtpUploadProvider : IImageUploadProvider
{
    public UploadProviderKind Kind => UploadProviderKind.Custom;
    public UploadCustomProtocol? CustomProtocol => UploadCustomProtocol.Ftp;

    public bool IsConfigured(UploadRuntimeConfig config)
        => !string.IsNullOrWhiteSpace(config.CustomHost);

    public async Task<UploadResult> UploadAsync(
        Stream imageStream,
        string contentType,
        string fileName,
        long contentLength,
        UploadRuntimeConfig config,
        IProgress<UploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured(config))
        {
            return UploadResult.Fail(
                Kind,
                UploadErrorKind.NotConfigured,
                LocalizationService.Translate("Upload not configured"));
        }

        progress?.Report(new UploadProgress(0.05, LocalizationService.Translate("Uploading…")));

        var port = config.CustomPort > 0 ? config.CustomPort : (config.FtpUseTls ? 21 : 21);
        try
        {
            using var client = new AsyncFtpClient(
                config.CustomHost.Trim(),
                config.CustomUsername ?? "",
                config.CustomPassword ?? "",
                port);

            client.Config.EncryptionMode = config.FtpUseTls
                ? FtpEncryptionMode.Explicit
                : FtpEncryptionMode.None;
            client.Config.ValidateAnyCertificate = config.FtpAllowInsecureCertificate;
            client.Config.DataConnectionType = config.FtpPassive
                ? FtpDataConnectionType.AutoPassive
                : FtpDataConnectionType.AutoActive;

            await client.Connect(cancellationToken).ConfigureAwait(false);

            var remoteDir = string.IsNullOrWhiteSpace(config.CustomRemoteDirectory)
                ? "/"
                : config.CustomRemoteDirectory.Replace('\\', '/');

            if (config.AutoCreateRemoteDirectory && remoteDir is not ("/" or ""))
            {
                await client.CreateDirectory(remoteDir, force: true, token: cancellationToken)
                    .ConfigureAwait(false);
            }

            var attempt = 0;
            string remotePath;
            while (true)
            {
                var name = config.UniqueSuffixOnCollision
                    ? CustomUploadHelpers.WithUniqueSuffix(fileName, attempt)
                    : fileName;
                remotePath = CustomUploadHelpers.CombineRemotePath(remoteDir, name);

                if (config.UniqueSuffixOnCollision && !config.OverwriteOnCollision)
                {
                    if (await client.FileExists(remotePath, cancellationToken).ConfigureAwait(false))
                    {
                        attempt++;
                        if (attempt > 50)
                        {
                            return UploadResult.Fail(
                                Kind,
                                UploadErrorKind.HostRejected,
                                LocalizationService.Translate("Upload failed"));
                        }
                        continue;
                    }
                }

                progress?.Report(new UploadProgress(0.4, LocalizationService.Translate("Uploading…")));
                imageStream.Position = 0;
                var status = await client.UploadStream(
                    imageStream,
                    remotePath,
                    config.OverwriteOnCollision || config.UniqueSuffixOnCollision
                        ? FtpRemoteExists.Overwrite
                        : FtpRemoteExists.Skip,
                    createRemoteDir: config.AutoCreateRemoteDirectory,
                    token: cancellationToken).ConfigureAwait(false);

                if (status is FtpStatus.Failed)
                {
                    return UploadResult.Fail(
                        Kind,
                        UploadErrorKind.HostRejected,
                        LocalizationService.Translate("Upload failed"));
                }

                break;
            }

            progress?.Report(new UploadProgress(1.0, LocalizationService.Translate("Uploaded")));
            var publicUrl = CustomUploadHelpers.CombinePublicUrl(config.CustomPublicUrlBase, Path.GetFileName(remotePath));
            AppDiagnostics.LogInfo("upload.ftp", "Upload succeeded");
            return CustomUploadHelpers.SuccessPathOrUrl(Kind, remotePath, publicUrl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UploadResult.Fail(Kind, UploadErrorKind.Cancelled, LocalizationService.Translate("Upload cancelled"));
        }
        catch (TimeoutException)
        {
            return UploadResult.Fail(Kind, UploadErrorKind.Timeout, LocalizationService.Translate("Upload timed out"));
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("upload.ftp", ex);
            var kind = ex is AuthenticationException
                ? UploadErrorKind.AuthFailed
                : UploadErrorKind.Network;
            return UploadResult.Fail(
                Kind,
                kind,
                LocalizationService.Translate("Upload failed") + ": " + ex.Message);
        }
    }
}
