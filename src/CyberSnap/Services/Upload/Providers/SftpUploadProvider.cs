using System.IO;
using System.Security.Cryptography;
using CyberSnap.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace CyberSnap.Services.Upload.Providers;

internal sealed class SftpUploadProvider : IImageUploadProvider
{
    public UploadProviderKind Kind => UploadProviderKind.Custom;
    public UploadCustomProtocol? CustomProtocol => UploadCustomProtocol.Sftp;

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

        return await Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var port = config.CustomPort > 0 ? config.CustomPort : 22;
                using var client = CreateClient(config, port);

                // TOFU host-key verification
                client.HostKeyReceived += (_, e) =>
                {
                    var fingerprint = Convert.ToHexString(SHA256.HashData(e.HostKey)).ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(config.SftpTrustedHostKeySha256))
                    {
                        // First connect: accept and persist later via settings (caller may update).
                        e.CanTrust = true;
                        TryPersistHostKey(fingerprint);
                    }
                    else if (string.Equals(
                                 fingerprint,
                                 config.SftpTrustedHostKeySha256.Replace(":", "").Trim(),
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        e.CanTrust = true;
                    }
                    else
                    {
                        e.CanTrust = false;
                    }
                };

                client.Connect();
                if (!client.IsConnected)
                {
                    return UploadResult.Fail(
                        Kind,
                        UploadErrorKind.Network,
                        LocalizationService.Translate("Upload failed"));
                }

                var remoteDir = string.IsNullOrWhiteSpace(config.CustomRemoteDirectory)
                    ? "."
                    : config.CustomRemoteDirectory.Replace('\\', '/');

                if (config.AutoCreateRemoteDirectory && remoteDir is not ("." or "/" or ""))
                    EnsureDirectory(client, remoteDir);

                var attempt = 0;
                string remotePath;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = config.UniqueSuffixOnCollision
                        ? CustomUploadHelpers.WithUniqueSuffix(fileName, attempt)
                        : fileName;
                    remotePath = CombineSftpPath(remoteDir, name);

                    if (config.UniqueSuffixOnCollision && client.Exists(remotePath))
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

                    progress?.Report(new UploadProgress(0.4, LocalizationService.Translate("Uploading…")));
                    imageStream.Position = 0;
                    client.UploadFile(imageStream, remotePath, canOverride: config.OverwriteOnCollision || config.UniqueSuffixOnCollision);
                    break;
                }

                progress?.Report(new UploadProgress(1.0, LocalizationService.Translate("Uploaded")));
                var publicUrl = CustomUploadHelpers.CombinePublicUrl(config.CustomPublicUrlBase, Path.GetFileName(remotePath));
                AppDiagnostics.LogInfo("upload.sftp", "Upload succeeded");
                return CustomUploadHelpers.SuccessPathOrUrl(Kind, remotePath, publicUrl);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return UploadResult.Fail(Kind, UploadErrorKind.Cancelled, LocalizationService.Translate("Upload cancelled"));
            }
            catch (SshConnectionException ex) when (ex.Message.Contains("Host key", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("trust", StringComparison.OrdinalIgnoreCase))
            {
                AppDiagnostics.LogError("upload.sftp", ex);
                return UploadResult.Fail(
                    Kind,
                    UploadErrorKind.HostKeyMismatch,
                    LocalizationService.Translate(
                        "SFTP host key mismatch. Reset the trusted host key in Settings → Uploads."));
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("upload.sftp", ex);
                var kind = ex is SshAuthenticationException
                    ? UploadErrorKind.AuthFailed
                    : UploadErrorKind.Network;
                return UploadResult.Fail(
                    Kind,
                    kind,
                    LocalizationService.Translate("Upload failed") + ": " + ex.Message);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static SftpClient CreateClient(UploadRuntimeConfig config, int port)
    {
        ConnectionInfo connectionInfo;
        if (!string.IsNullOrWhiteSpace(config.SftpPrivateKeyPath) && File.Exists(config.SftpPrivateKeyPath))
        {
            PrivateKeyFile keyFile = string.IsNullOrEmpty(config.SftpPrivateKeyPassphrase)
                ? new PrivateKeyFile(config.SftpPrivateKeyPath)
                : new PrivateKeyFile(config.SftpPrivateKeyPath, config.SftpPrivateKeyPassphrase);
            connectionInfo = new ConnectionInfo(
                config.CustomHost.Trim(),
                port,
                config.CustomUsername ?? "",
                new PrivateKeyAuthenticationMethod(config.CustomUsername ?? "", keyFile));
        }
        else
        {
            connectionInfo = new ConnectionInfo(
                config.CustomHost.Trim(),
                port,
                config.CustomUsername ?? "",
                new PasswordAuthenticationMethod(config.CustomUsername ?? "", config.CustomPassword ?? ""));
        }

        return new SftpClient(connectionInfo);
    }

    private static void EnsureDirectory(SftpClient client, string remoteDir)
    {
        var parts = remoteDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = remoteDir.StartsWith('/') ? "/" : "";
        foreach (var part in parts)
        {
            path = path is "/" or "" ? path + part : path + "/" + part;
            if (!client.Exists(path))
                client.CreateDirectory(path);
        }
    }

    private static string CombineSftpPath(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory == ".")
            return fileName;
        return directory.TrimEnd('/') + "/" + fileName.TrimStart('/');
    }

    private static void TryPersistHostKey(string fingerprintSha256)
    {
        try
        {
            var existing = SettingsService.LoadStatic()?.UploadSftpTrustedHostKeySha256;
            if (!string.IsNullOrWhiteSpace(existing)) return;

            var svc = new SettingsService();
            svc.Load();
            if (string.IsNullOrWhiteSpace(svc.Settings.UploadSftpTrustedHostKeySha256))
            {
                svc.Settings.UploadSftpTrustedHostKeySha256 = fingerprintSha256;
                svc.Save();
            }
            svc.Dispose();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("upload.sftp.tofu", ex.Message, ex);
        }
    }
}
