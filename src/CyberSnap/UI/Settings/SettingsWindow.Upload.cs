using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Controls;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.Services.Upload;

namespace CyberSnap.UI;

public partial class SettingsWindow
{
    private bool _suppressUploadPreferenceChange;
    private bool _uploadTestInProgress;

    private void UploadSubTab_Click(object sender, RoutedEventArgs e)
    {
        ApplyUploadSubTabSelection();
        PersistUploadSubTabSelection();
    }

    private void ApplyUploadSubTabSelection()
    {
        if (UploadSectionGeneral is null) return;

        var tag = "general";
        if (UploadSubTabCyberGems?.IsChecked == true) tag = "cybergems";
        else if (UploadSubTabImgBB?.IsChecked == true) tag = "imgbb";
        else if (UploadSubTabImgur?.IsChecked == true) tag = "imgur";
        else if (UploadSubTabCustom?.IsChecked == true) tag = "custom";

        UploadSectionGeneral.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        UploadSectionCyberGems.Visibility = tag == "cybergems" ? Visibility.Visible : Visibility.Collapsed;
        UploadSectionImgBB.Visibility = tag == "imgbb" ? Visibility.Visible : Visibility.Collapsed;
        UploadSectionImgur.Visibility = tag == "imgur" ? Visibility.Visible : Visibility.Collapsed;
        UploadSectionCustom.Visibility = tag == "custom" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PersistUploadSubTabSelection()
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var tag = "general";
        if (UploadSubTabCyberGems?.IsChecked == true) tag = "cybergems";
        else if (UploadSubTabImgBB?.IsChecked == true) tag = "imgbb";
        else if (UploadSubTabImgur?.IsChecked == true) tag = "imgur";
        else if (UploadSubTabCustom?.IsChecked == true) tag = "custom";

        if (string.Equals(_settingsService.Settings.UploadSettingsSubTab, tag, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _settingsService.Settings.UploadSettingsSubTab = tag;
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("settings.upload-subtab", ex.Message, ex);
        }
    }

    private void RestoreUploadSubTabSelection(string? tag)
    {
        tag = (tag ?? "general").Trim().ToLowerInvariant();
        UploadSubTabGeneral.IsChecked = tag is "general" or "";
        UploadSubTabCyberGems.IsChecked = tag is "cybergems" or "cybersnap";
        UploadSubTabImgBB.IsChecked = tag == "imgbb";
        UploadSubTabImgur.IsChecked = tag == "imgur";
        UploadSubTabCustom.IsChecked = tag is "custom" or "ftp" or "sftp" or "s3" or "webhook";
        if (UploadSubTabGeneral.IsChecked != true
            && UploadSubTabCyberGems.IsChecked != true
            && UploadSubTabImgBB.IsChecked != true
            && UploadSubTabImgur.IsChecked != true
            && UploadSubTabCustom.IsChecked != true)
        {
            UploadSubTabGeneral.IsChecked = true;
        }
        ApplyUploadSubTabSelection();
    }

    private void LoadUploadsTab()
    {
        _suppressUploadPreferenceChange = true;
        try
        {
            var s = _settingsService.Settings;
            RestoreUploadSubTabSelection(s.UploadSettingsSubTab);
            RebuildUploadDefaultProviderCombo(s);
            SelectComboByTag(UploadImageFormatCombo, s.UploadImageFormat == UploadImageFormatPreference.Jpeg ? "Jpeg" : "Png");
            SelectComboByTag(UploadJpegQualityCombo, s.UploadJpegQuality.ToString());
            UploadOpenUrlAfterSuccessCheck.IsChecked = s.UploadOpenUrlAfterSuccess;

            UploadCyberGemsBaseUrlBox.Text = s.UploadCyberGemsBaseUrl ?? "";
            UploadUseCustomCyberGemsApiKeyCheck.IsChecked = s.UploadUseCustomCyberGemsApiKey;
            UploadCyberGemsApiKeyBox.Password = s.UploadCyberGemsApiKey ?? "";

            UploadUseCustomImgBBApiKeyCheck.IsChecked = s.UploadUseCustomImgBBApiKey;
            UploadImgBBApiKeyBox.Password = s.UploadImgBBApiKey ?? "";

            UploadUseCustomImgurClientIdCheck.IsChecked = s.UploadUseCustomImgurClientId;
            UploadImgurClientIdBox.Password = s.UploadImgurClientId ?? "";
            UploadTestImgurBtn.IsEnabled = UploadCredentialResolver.HasUserImgurClientId(s);

            SelectComboByTag(UploadCustomProtocolCombo, s.UploadCustomProtocol.ToString());
            UploadCustomHostBox.Text = s.UploadCustomHost ?? "";
            UploadCustomPortBox.Text = s.UploadCustomPort.ToString();
            UploadCustomUsernameBox.Text = s.UploadCustomUsername ?? "";
            UploadCustomPasswordBox.Password = s.UploadCustomPassword ?? "";
            UploadCustomRemoteDirectoryBox.Text = s.UploadCustomRemoteDirectory ?? "";
            UploadCustomPublicUrlBaseBox.Text = s.UploadCustomPublicUrlBase ?? "";
            UploadWebhookUrlBox.Text = s.UploadWebhookUrl ?? "";
            UploadWebhookBearerTokenBox.Password = s.UploadWebhookBearerToken ?? "";
            SelectComboByTag(UploadWebhookBodyModeCombo,
                s.UploadWebhookBodyMode == UploadWebhookBodyMode.JsonBase64 ? "JsonBase64" : "Multipart");
            UploadWebhookFormFieldNameBox.Text = string.IsNullOrWhiteSpace(s.UploadWebhookFormFieldName)
                ? "image"
                : s.UploadWebhookFormFieldName;
            UploadUniqueSuffixOnCollisionCheck.IsChecked = s.UploadUniqueSuffixOnCollision;

            UploadFtpUseTlsCheck.IsChecked = s.UploadFtpUseTls;
            UploadFtpAllowInsecureCertificateCheck.IsChecked = s.UploadFtpAllowInsecureCertificate;
            UploadFtpPassiveCheck.IsChecked = s.UploadFtpPassive;

            UploadSftpPrivateKeyPathBox.Text = s.UploadSftpPrivateKeyPath ?? "";
            UploadSftpPrivateKeyPassphraseBox.Password = s.UploadSftpPrivateKeyPassphrase ?? "";
            UpdateSftpTrustedHostKeyLabel(s.UploadSftpTrustedHostKeySha256);

            UploadS3EndpointBox.Text = s.UploadS3Endpoint ?? "";
            UploadS3RegionBox.Text = s.UploadS3Region ?? "";
            UploadS3BucketBox.Text = s.UploadS3Bucket ?? "";
            UploadS3AccessKeyBox.Password = s.UploadS3AccessKey ?? "";
            UploadS3SecretKeyBox.Password = s.UploadS3SecretKey ?? "";
            UploadS3KeyPrefixBox.Text = s.UploadS3KeyPrefix ?? "";
            UploadS3ForcePathStyleCheck.IsChecked = s.UploadS3ForcePathStyle;
            UploadS3MakePublicCheck.IsChecked = s.UploadS3MakePublic;

            UpdateCustomProtocolFieldsVisibility(s.UploadCustomProtocol);
            UploadTestCustomBtn.IsEnabled = true;
            UploadTestCustomBtn.ToolTip = LocalizationService.Translate(
                "Uploads a tiny test image using the current custom settings.");
            SetUploadPreferenceStatus(string.Empty);
        }
        finally
        {
            _suppressUploadPreferenceChange = false;
        }
    }

    private void RebuildUploadDefaultProviderCombo(AppSettings s)
    {
        var previous = s.UploadDefaultProvider;
        UploadDefaultProviderCombo.Items.Clear();
        UploadDefaultProviderCombo.Items.Add(new ComboBoxItem
        {
            Content = LocalizationService.Translate("CyberSnap Share"),
            Tag = nameof(UploadProviderKind.CyberGems),
        });
        UploadDefaultProviderCombo.Items.Add(new ComboBoxItem
        {
            Content = LocalizationService.Translate("ImgBB"),
            Tag = nameof(UploadProviderKind.ImgBB),
        });
        if (UploadCredentialResolver.HasUserImgurClientId(s))
        {
            UploadDefaultProviderCombo.Items.Add(new ComboBoxItem
            {
                Content = LocalizationService.Translate("Imgur"),
                Tag = nameof(UploadProviderKind.Imgur),
            });
        }
        UploadDefaultProviderCombo.Items.Add(new ComboBoxItem
        {
            Content = LocalizationService.Translate("Custom destination"),
            Tag = nameof(UploadProviderKind.Custom),
        });

        // Show the user's preference; runtime GetDefaultProvider may fall back (e.g. CyberGems → ImgBB).
        var display = Enum.IsDefined(typeof(UploadProviderKind), s.UploadDefaultProvider)
            ? s.UploadDefaultProvider
            : UploadProviderKind.CyberGems;
        if (display == UploadProviderKind.Imgur && !UploadCredentialResolver.HasUserImgurClientId(s))
        {
            display = UploadProviderKind.CyberGems;
            if (previous != display)
            {
                s.UploadDefaultProvider = display;
                // Persist the fallback when changed at runtime (not during initial load).
                if (!_suppressUploadPreferenceChange)
                {
                    try { _settingsService.Save(); }
                    catch (Exception ex) { AppDiagnostics.LogWarning("settings.upload-default-provider", ex.Message, ex); }
                }
            }
        }
        SelectComboByTag(UploadDefaultProviderCombo, display.ToString());
    }

    private void UpdateCustomProtocolFieldsVisibility(UploadCustomProtocol protocol)
    {
        bool isFtp = protocol == UploadCustomProtocol.Ftp;
        bool isSftp = protocol == UploadCustomProtocol.Sftp;
        bool isS3 = protocol == UploadCustomProtocol.S3;
        bool isWebhook = protocol == UploadCustomProtocol.Webhook;
        UploadFtpFields.Visibility = isFtp ? Visibility.Visible : Visibility.Collapsed;
        UploadSftpFields.Visibility = isSftp ? Visibility.Visible : Visibility.Collapsed;
        UploadS3Fields.Visibility = isS3 ? Visibility.Visible : Visibility.Collapsed;
        if (UploadWebhookFields is not null)
            UploadWebhookFields.Visibility = isWebhook ? Visibility.Visible : Visibility.Collapsed;

        // Host/user/password/remote dir are FTP/SFTP only; S3 and Webhook use their own fields.
        if (UploadCustomServerFields is not null)
            UploadCustomServerFields.Visibility = (isS3 || isWebhook) ? Visibility.Collapsed : Visibility.Visible;
        // Public URL base still useful for webhook fallback; unique-suffix is FTP/SFTP/S3-ish.
        if (UploadCustomSharedFields is not null)
            UploadCustomSharedFields.Visibility = Visibility.Visible;
        if (UploadUniqueSuffixOnCollisionCheck is not null)
        {
            var uniqueRow = UploadUniqueSuffixOnCollisionCheck.Parent as FrameworkElement;
            if (uniqueRow is not null)
                uniqueRow.Visibility = isWebhook ? Visibility.Collapsed : Visibility.Visible;
        }

        // Differentiate the shared FTP/SFTP server block so both protocols don't look identical.
        if (UploadCustomServerTitle is not null)
        {
            var source = isSftp ? "SFTP server" : "FTP server";
            LocalizationService.SetSourceText(UploadCustomServerTitle, source);
            UploadCustomServerTitle.Text = LocalizationService.Translate(source);
        }
        if (UploadCustomPortHint is not null)
        {
            const string source = "0 = protocol default (FTP: 21, SFTP: 22)";
            LocalizationService.SetSourceText(UploadCustomPortHint, source);
            UploadCustomPortHint.Text = LocalizationService.Translate(source);
        }

        UpdateFtpTlsDependentVisibility(_settingsService.Settings.UploadFtpUseTls);
        UpdateSftpPassphraseVisibility(_settingsService.Settings.UploadSftpPrivateKeyPath);
    }

    private void UpdateFtpTlsDependentVisibility(bool useTls)
    {
        // Regular FTP needs no certificate option; only show it for FTPS.
        var visibility = useTls ? Visibility.Visible : Visibility.Collapsed;
        if (UploadFtpAllowInsecureRow is not null)
            UploadFtpAllowInsecureRow.Visibility = visibility;
        if (UploadFtpAllowInsecureSeparator is not null)
            UploadFtpAllowInsecureSeparator.Visibility = visibility;
    }

    private void UpdateSftpPassphraseVisibility(string? keyPath)
    {
        // Password-auth SFTP needs no passphrase row; only show it when a key is configured.
        var hasKey = !string.IsNullOrWhiteSpace(keyPath);
        var visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;
        if (UploadSftpPassphraseRow is not null)
            UploadSftpPassphraseRow.Visibility = visibility;
        if (UploadSftpPassphraseSeparator is not null)
            UploadSftpPassphraseSeparator.Visibility = visibility;
    }

    private void UpdateSftpTrustedHostKeyLabel(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            const string source = "Not stored yet — set on first successful connect.";
            LocalizationService.SetSourceText(UploadSftpTrustedHostKeyText, source);
            UploadSftpTrustedHostKeyText.Text = LocalizationService.Translate(source);
            UploadSftpTrustedHostKeyText.ToolTip = null;
        }
        else
        {
            var trimmed = fingerprint.Trim();
            var shortFp = trimmed.Length > 24 ? trimmed[..24] + "…" : trimmed;
            // Pin the source text so a later language pass doesn't overwrite the fingerprint.
            LocalizationService.SetSourceText(UploadSftpTrustedHostKeyText, shortFp);
            UploadSftpTrustedHostKeyText.Text = shortFp;
            // Full fingerprint on hover for verification.
            UploadSftpTrustedHostKeyText.ToolTip = trimmed;
        }
    }

    private void SetUploadPreferenceStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            UploadPreferenceStatusText.Visibility = Visibility.Collapsed;
            UploadPreferenceStatusText.Text = "";
            return;
        }

        UploadPreferenceStatusText.Text = message;
        UploadPreferenceStatusText.Visibility = Visibility.Visible;
    }

    private void UpdateUploadPreference<T>(
        string diagnosticKey,
        string label,
        T previous,
        T current,
        Action<T> setValue,
        Action<T> restoreUi,
        Action<T>? applyRuntime = null)
    {
        try
        {
            setValue(current);
            _settingsService.Save();
            SetUploadPreferenceStatus(string.Empty);
            applyRuntime?.Invoke(current);
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError(diagnosticKey, ex);
            setValue(previous);
            try { _settingsService.Save(); }
            catch (Exception rollbackEx) { AppDiagnostics.LogError($"{diagnosticKey}-rollback", rollbackEx); }

            _suppressUploadPreferenceChange = true;
            try { restoreUi(previous); }
            finally { _suppressUploadPreferenceChange = false; }

            applyRuntime?.Invoke(previous);
            SetUploadPreferenceStatus($"{label} change was not saved. Previous setting restored.");
        }
    }

    private static string? GetUploadComboTag(System.Windows.Controls.ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    // ── General ──────────────────────────────────────────────────────────

    private void UploadDefaultProviderCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var tag = GetUploadComboTag(UploadDefaultProviderCombo);
        if (!Enum.TryParse<UploadProviderKind>(tag, ignoreCase: true, out var selected))
            return;
        var previous = _settingsService.Settings.UploadDefaultProvider;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-default-provider",
            "Default share destination",
            previous,
            selected,
            value => _settingsService.Settings.UploadDefaultProvider = value,
            value => SelectComboByTag(UploadDefaultProviderCombo, value.ToString()));
    }

    private void UploadImageFormatCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var tag = GetUploadComboTag(UploadImageFormatCombo);
        var selected = string.Equals(tag, "Jpeg", StringComparison.OrdinalIgnoreCase)
            ? UploadImageFormatPreference.Jpeg
            : UploadImageFormatPreference.Png;
        var previous = _settingsService.Settings.UploadImageFormat;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-image-format",
            "Upload image format",
            previous,
            selected,
            value => _settingsService.Settings.UploadImageFormat = value,
            value => SelectComboByTag(UploadImageFormatCombo, value == UploadImageFormatPreference.Jpeg ? "Jpeg" : "Png"));
    }

    private void UploadJpegQualityCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var tag = GetUploadComboTag(UploadJpegQualityCombo);
        if (!int.TryParse(tag, out var selected)) return;
        var previous = _settingsService.Settings.UploadJpegQuality;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-jpeg-quality",
            "JPEG quality",
            previous,
            selected,
            value => _settingsService.Settings.UploadJpegQuality = value,
            value => SelectComboByTag(UploadJpegQualityCombo, value.ToString()));
    }

    private void UploadOpenUrlAfterSuccessCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadOpenUrlAfterSuccess;
        var selected = UploadOpenUrlAfterSuccessCheck.IsChecked == true;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-open-url",
            "Open link after upload",
            previous,
            selected,
            value => _settingsService.Settings.UploadOpenUrlAfterSuccess = value,
            value => UploadOpenUrlAfterSuccessCheck.IsChecked = value);
    }

    // ── CyberGems Share ──────────────────────────────────────────────────

    private void UploadCyberGemsBaseUrlBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadCyberGemsBaseUrl ?? "";
        var selected = UploadCyberGemsBaseUrlBox.Text?.Trim() ?? "";
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-cybergems-base-url",
            "Share server URL",
            previous,
            selected,
            value => _settingsService.Settings.UploadCyberGemsBaseUrl = value,
            value => UploadCyberGemsBaseUrlBox.Text = value);
    }

    private void UploadUseCustomCyberGemsApiKeyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadUseCustomCyberGemsApiKey;
        var selected = UploadUseCustomCyberGemsApiKeyCheck.IsChecked == true;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-use-custom-cybergems",
            "Use my own Share API key",
            previous,
            selected,
            value => _settingsService.Settings.UploadUseCustomCyberGemsApiKey = value,
            value => UploadUseCustomCyberGemsApiKeyCheck.IsChecked = value);
    }

    private void UploadCyberGemsApiKeyBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadCyberGemsApiKey;
        var key = UploadCyberGemsApiKeyBox.Password?.Trim();
        var selected = string.IsNullOrWhiteSpace(key) ? null : key;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-cybergems-key",
            "CyberSnap Share API key",
            previous,
            selected,
            value =>
            {
                _settingsService.Settings.UploadCyberGemsApiKey = value;
                if (!string.IsNullOrWhiteSpace(value))
                    _settingsService.Settings.UploadUseCustomCyberGemsApiKey = true;
            },
            value => UploadCyberGemsApiKeyBox.Password = value ?? "",
            applyRuntime: value =>
            {
                if (!string.IsNullOrWhiteSpace(value) && UploadUseCustomCyberGemsApiKeyCheck.IsChecked != true)
                {
                    _suppressUploadPreferenceChange = true;
                    try { UploadUseCustomCyberGemsApiKeyCheck.IsChecked = true; }
                    finally { _suppressUploadPreferenceChange = false; }
                }
            });
    }

    // ── ImgBB ────────────────────────────────────────────────────────────

    private void UploadUseCustomImgBBApiKeyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadUseCustomImgBBApiKey;
        var selected = UploadUseCustomImgBBApiKeyCheck.IsChecked == true;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-use-custom-imgbb",
            "Use my own ImgBB API key",
            previous,
            selected,
            value => _settingsService.Settings.UploadUseCustomImgBBApiKey = value,
            value => UploadUseCustomImgBBApiKeyCheck.IsChecked = value);
    }

    private void UploadImgBBApiKeyBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadImgBBApiKey;
        var key = UploadImgBBApiKeyBox.Password?.Trim();
        var selected = string.IsNullOrWhiteSpace(key) ? null : key;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-imgbb-key",
            "ImgBB API key",
            previous,
            selected,
            value =>
            {
                _settingsService.Settings.UploadImgBBApiKey = value;
                // Pasting a key implies "use my own key" — keep the toggle in sync with intent.
                if (!string.IsNullOrWhiteSpace(value))
                    _settingsService.Settings.UploadUseCustomImgBBApiKey = true;
            },
            value => UploadImgBBApiKeyBox.Password = value ?? "",
            applyRuntime: value =>
            {
                if (!string.IsNullOrWhiteSpace(value) && UploadUseCustomImgBBApiKeyCheck.IsChecked != true)
                {
                    _suppressUploadPreferenceChange = true;
                    try { UploadUseCustomImgBBApiKeyCheck.IsChecked = true; }
                    finally { _suppressUploadPreferenceChange = false; }
                }
            });
    }

    // ── Imgur (user key only) ────────────────────────────────────────────

    private void UploadUseCustomImgurClientIdCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadUseCustomImgurClientId;
        var selected = UploadUseCustomImgurClientIdCheck.IsChecked == true;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-use-custom-imgur",
            "Use my own Imgur Client-ID",
            previous,
            selected,
            value => _settingsService.Settings.UploadUseCustomImgurClientId = value,
            value => UploadUseCustomImgurClientIdCheck.IsChecked = value,
            applyRuntime: _ =>
            {
                RebuildUploadDefaultProviderCombo(_settingsService.Settings);
                UploadTestImgurBtn.IsEnabled = UploadCredentialResolver.HasUserImgurClientId(_settingsService.Settings);
            });
    }

    private void UploadImgurClientIdBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadImgurClientId;
        var key = UploadImgurClientIdBox.Password?.Trim();
        var selected = string.IsNullOrWhiteSpace(key) ? null : key;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-imgur-client-id",
            "Imgur Client-ID",
            previous,
            selected,
            value => _settingsService.Settings.UploadImgurClientId = value,
            value => UploadImgurClientIdBox.Password = value ?? "",
            applyRuntime: _ =>
            {
                RebuildUploadDefaultProviderCombo(_settingsService.Settings);
                UploadTestImgurBtn.IsEnabled = UploadCredentialResolver.HasUserImgurClientId(_settingsService.Settings);
            });
    }

    // ── Custom destination ───────────────────────────────────────────────

    private void UploadCustomProtocolCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var tag = GetUploadComboTag(UploadCustomProtocolCombo);
        if (!Enum.TryParse<UploadCustomProtocol>(tag, ignoreCase: true, out var selected))
            return;
        var previous = _settingsService.Settings.UploadCustomProtocol;
        if (previous == selected)
        {
            UpdateCustomProtocolFieldsVisibility(selected);
            return;
        }
        UpdateUploadPreference(
            "settings.upload-custom-protocol",
            "Protocol",
            previous,
            selected,
            value => _settingsService.Settings.UploadCustomProtocol = value,
            value => SelectComboByTag(UploadCustomProtocolCombo, value.ToString()),
            applyRuntime: UpdateCustomProtocolFieldsVisibility);
    }

    private void UploadCustomHostBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadCustomHost ?? "";
        var selected = UploadCustomHostBox.Text ?? "";
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-custom-host",
            "Host",
            previous,
            selected,
            value => _settingsService.Settings.UploadCustomHost = value,
            value => UploadCustomHostBox.Text = value);
    }

    private void UploadCustomPortBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        if (!int.TryParse(UploadCustomPortBox.Text?.Trim(), out var selected) || selected < 0 || selected > 65535)
        {
            SetUploadPreferenceStatus(LocalizationService.Translate("Port must be a number from 0 to 65535 (0 = default)."));
            return;
        }
        var previous = _settingsService.Settings.UploadCustomPort;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-custom-port",
            "Port",
            previous,
            selected,
            value => _settingsService.Settings.UploadCustomPort = value,
            value => UploadCustomPortBox.Text = value.ToString());
    }

    private void UploadCustomUsernameBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadCustomUsername ?? "";
        var selected = UploadCustomUsernameBox.Text ?? "";
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-custom-username",
            "Username",
            previous,
            selected,
            value => _settingsService.Settings.UploadCustomUsername = value,
            value => UploadCustomUsernameBox.Text = value);
    }

    private void UploadCustomPasswordBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadCustomPassword;
        var pwd = UploadCustomPasswordBox.Password;
        var selected = string.IsNullOrEmpty(pwd) ? null : pwd;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-custom-password",
            "Password",
            previous,
            selected,
            value => _settingsService.Settings.UploadCustomPassword = value,
            value => UploadCustomPasswordBox.Password = value ?? "");
    }

    private void UploadCustomRemoteDirectoryBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadCustomRemoteDirectory ?? "";
        var selected = UploadCustomRemoteDirectoryBox.Text ?? "";
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-custom-remote-dir",
            "Remote directory",
            previous,
            selected,
            value => _settingsService.Settings.UploadCustomRemoteDirectory = value,
            value => UploadCustomRemoteDirectoryBox.Text = value);
    }

    private void UploadCustomPublicUrlBaseBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadCustomPublicUrlBase ?? "";
        var selected = UploadCustomPublicUrlBaseBox.Text ?? "";
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-custom-public-url",
            "Public URL base",
            previous,
            selected,
            value => _settingsService.Settings.UploadCustomPublicUrlBase = value,
            value => UploadCustomPublicUrlBaseBox.Text = value);
    }

    // ── Webhook ──────────────────────────────────────────────────────────

    private void UploadWebhookUrlBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadWebhookUrl ?? "";
        var selected = UploadWebhookUrlBox.Text?.Trim() ?? "";
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-webhook-url",
            "Webhook URL",
            previous,
            selected,
            value => _settingsService.Settings.UploadWebhookUrl = value,
            value => UploadWebhookUrlBox.Text = value);
    }

    private void UploadWebhookBearerTokenBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadWebhookBearerToken;
        var token = UploadWebhookBearerTokenBox.Password?.Trim();
        var selected = string.IsNullOrWhiteSpace(token) ? null : token;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-webhook-bearer",
            "Bearer token",
            previous,
            selected,
            value => _settingsService.Settings.UploadWebhookBearerToken = value,
            value => UploadWebhookBearerTokenBox.Password = value ?? "");
    }

    private void UploadWebhookBodyModeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var tag = GetUploadComboTag(UploadWebhookBodyModeCombo);
        var selected = string.Equals(tag, "JsonBase64", StringComparison.OrdinalIgnoreCase)
            ? UploadWebhookBodyMode.JsonBase64
            : UploadWebhookBodyMode.Multipart;
        var previous = _settingsService.Settings.UploadWebhookBodyMode;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-webhook-body-mode",
            "Request body",
            previous,
            selected,
            value => _settingsService.Settings.UploadWebhookBodyMode = value,
            value => SelectComboByTag(
                UploadWebhookBodyModeCombo,
                value == UploadWebhookBodyMode.JsonBase64 ? "JsonBase64" : "Multipart"));
    }

    private void UploadWebhookFormFieldNameBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadWebhookFormFieldName ?? "image";
        var selected = string.IsNullOrWhiteSpace(UploadWebhookFormFieldNameBox.Text)
            ? "image"
            : UploadWebhookFormFieldNameBox.Text.Trim();
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-webhook-field",
            "Form field name",
            previous,
            selected,
            value => _settingsService.Settings.UploadWebhookFormFieldName = value,
            value => UploadWebhookFormFieldNameBox.Text = value);
    }

    private void UploadUniqueSuffixOnCollisionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadUniqueSuffixOnCollision;
        var selected = UploadUniqueSuffixOnCollisionCheck.IsChecked == true;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-unique-suffix",
            "Unique name if file exists",
            previous,
            selected,
            value => _settingsService.Settings.UploadUniqueSuffixOnCollision = value,
            value => UploadUniqueSuffixOnCollisionCheck.IsChecked = value);
    }

    private void UploadFtpUseTlsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadFtpUseTls;
        var selected = UploadFtpUseTlsCheck.IsChecked == true;
        if (previous == selected)
        {
            UpdateFtpTlsDependentVisibility(selected);
            return;
        }
        UpdateUploadPreference(
            "settings.upload-ftp-tls",
            "Use FTPS (TLS)",
            previous,
            selected,
            value => _settingsService.Settings.UploadFtpUseTls = value,
            value => UploadFtpUseTlsCheck.IsChecked = value,
            applyRuntime: UpdateFtpTlsDependentVisibility);
    }

    private void UploadFtpAllowInsecureCertificateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadFtpAllowInsecureCertificate;
        var selected = UploadFtpAllowInsecureCertificateCheck.IsChecked == true;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-ftp-insecure-cert",
            "Allow insecure certificate",
            previous,
            selected,
            value => _settingsService.Settings.UploadFtpAllowInsecureCertificate = value,
            value => UploadFtpAllowInsecureCertificateCheck.IsChecked = value);
    }

    private void UploadFtpPassiveCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadFtpPassive;
        var selected = UploadFtpPassiveCheck.IsChecked == true;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-ftp-passive",
            "Passive mode",
            previous,
            selected,
            value => _settingsService.Settings.UploadFtpPassive = value,
            value => UploadFtpPassiveCheck.IsChecked = value);
    }

    private void UploadSftpPrivateKeyPathBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadSftpPrivateKeyPath;
        var text = UploadSftpPrivateKeyPathBox.Text?.Trim();
        var selected = string.IsNullOrWhiteSpace(text) ? null : text;
        if (previous == selected)
        {
            UpdateSftpPassphraseVisibility(selected);
            return;
        }
        UpdateUploadPreference(
            "settings.upload-sftp-key-path",
            "Private key path",
            previous,
            selected,
            value => _settingsService.Settings.UploadSftpPrivateKeyPath = value,
            value => UploadSftpPrivateKeyPathBox.Text = value ?? "",
            applyRuntime: UpdateSftpPassphraseVisibility);
        if (!string.IsNullOrWhiteSpace(selected) && !System.IO.File.Exists(selected))
        {
            SetUploadPreferenceStatus(LocalizationService.Translate(
                "Private key file not found. Password authentication will be used."));
        }
    }

    private void UploadSftpBrowseKeyBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = LocalizationService.Translate("Select SFTP private key"),
                CheckFileExists = true,
                Filter = LocalizationService.Translate("Key files") + " (*.pem;*.ppk;*.key;id_rsa;id_ed25519)|*.pem;*.ppk;*.key;id_rsa;id_ed25519|"
                    + LocalizationService.Translate("All files") + " (*.*)|*.*",
            };
            if (dialog.ShowDialog(this) == true)
                UploadSftpPrivateKeyPathBox.Text = dialog.FileName;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("settings.upload-sftp-browse", ex.Message, ex);
        }
    }

    private void UploadSftpPrivateKeyPassphraseBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadSftpPrivateKeyPassphrase;
        var pwd = UploadSftpPrivateKeyPassphraseBox.Password;
        var selected = string.IsNullOrEmpty(pwd) ? null : pwd;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-sftp-key-passphrase",
            "Private key passphrase",
            previous,
            selected,
            value => _settingsService.Settings.UploadSftpPrivateKeyPassphrase = value,
            value => UploadSftpPrivateKeyPassphraseBox.Password = value ?? "");
    }

    private void UploadResetSftpHostKeyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadSftpTrustedHostKeySha256;
        UpdateUploadPreference(
            "settings.upload-sftp-reset-host-key",
            "Trusted host key",
            previous,
            (string?)null,
            value => _settingsService.Settings.UploadSftpTrustedHostKeySha256 = value,
            value => UpdateSftpTrustedHostKeyLabel(value),
            applyRuntime: UpdateSftpTrustedHostKeyLabel);
    }

    private void UploadS3EndpointBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadS3Endpoint ?? "";
        var selected = UploadS3EndpointBox.Text ?? "";
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-s3-endpoint",
            "Endpoint",
            previous,
            selected,
            value => _settingsService.Settings.UploadS3Endpoint = value,
            value => UploadS3EndpointBox.Text = value);
    }

    private void UploadS3RegionBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadS3Region ?? "";
        var selected = UploadS3RegionBox.Text ?? "";
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-s3-region",
            "Region",
            previous,
            selected,
            value => _settingsService.Settings.UploadS3Region = value,
            value => UploadS3RegionBox.Text = value);
    }

    private void UploadS3BucketBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadS3Bucket ?? "";
        var selected = UploadS3BucketBox.Text ?? "";
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-s3-bucket",
            "Bucket",
            previous,
            selected,
            value => _settingsService.Settings.UploadS3Bucket = value,
            value => UploadS3BucketBox.Text = value);
    }

    private void UploadS3AccessKeyBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadS3AccessKey;
        var key = UploadS3AccessKeyBox.Password?.Trim();
        var selected = string.IsNullOrWhiteSpace(key) ? null : key;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-s3-access-key",
            "Access key",
            previous,
            selected,
            value => _settingsService.Settings.UploadS3AccessKey = value,
            value => UploadS3AccessKeyBox.Password = value ?? "");
    }

    private void UploadS3SecretKeyBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadS3SecretKey;
        var key = UploadS3SecretKeyBox.Password?.Trim();
        var selected = string.IsNullOrWhiteSpace(key) ? null : key;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-s3-secret-key",
            "Secret key",
            previous,
            selected,
            value => _settingsService.Settings.UploadS3SecretKey = value,
            value => UploadS3SecretKeyBox.Password = value ?? "");
    }

    private void UploadS3KeyPrefixBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadS3KeyPrefix ?? "";
        var selected = UploadS3KeyPrefixBox.Text ?? "";
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-s3-key-prefix",
            "Key prefix",
            previous,
            selected,
            value => _settingsService.Settings.UploadS3KeyPrefix = value,
            value => UploadS3KeyPrefixBox.Text = value);
    }

    private void UploadS3ForcePathStyleCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadS3ForcePathStyle;
        var selected = UploadS3ForcePathStyleCheck.IsChecked == true;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-s3-path-style",
            "Force path-style URLs",
            previous,
            selected,
            value => _settingsService.Settings.UploadS3ForcePathStyle = value,
            value => UploadS3ForcePathStyleCheck.IsChecked = value);
    }

    private void UploadS3MakePublicCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressUploadPreferenceChange) return;
        var previous = _settingsService.Settings.UploadS3MakePublic;
        var selected = UploadS3MakePublicCheck.IsChecked == true;
        if (previous == selected) return;
        UpdateUploadPreference(
            "settings.upload-s3-make-public",
            "Try ACL public-read",
            previous,
            selected,
            value => _settingsService.Settings.UploadS3MakePublic = value,
            value => UploadS3MakePublicCheck.IsChecked = value);
    }

    // ── Test uploads ─────────────────────────────────────────────────────

    private async void UploadTestCyberGemsBtn_Click(object sender, RoutedEventArgs e)
        => await RunProviderTestAsync(UploadProviderKind.CyberGems).ConfigureAwait(true);

    private async void UploadTestImgBBBtn_Click(object sender, RoutedEventArgs e)
        => await RunProviderTestAsync(UploadProviderKind.ImgBB).ConfigureAwait(true);

    private async void UploadTestImgurBtn_Click(object sender, RoutedEventArgs e)
        => await RunProviderTestAsync(UploadProviderKind.Imgur).ConfigureAwait(true);

    private async void UploadTestCustomBtn_Click(object sender, RoutedEventArgs e)
        => await RunProviderTestAsync(UploadProviderKind.Custom).ConfigureAwait(true);

    private async Task RunProviderTestAsync(UploadProviderKind provider)
    {
        if (_uploadTestInProgress)
        {
            ToastWindow.Show(
                LocalizationService.Translate("Please wait"),
                LocalizationService.Translate("Share upload is already running."));
            return;
        }
        var precheck = GetCustomTestPrecheckError(provider);
        if (precheck is not null)
        {
            SetUploadPreferenceStatus(precheck);
            ToastWindow.ShowError(
                LocalizationService.Translate("Upload not configured"),
                precheck);
            return;
        }
        _uploadTestInProgress = true;
        UploadTestCyberGemsBtn.IsEnabled = false;
        UploadTestImgBBBtn.IsEnabled = false;
        UploadTestImgurBtn.IsEnabled = false;
        UploadTestCustomBtn.IsEnabled = false;
        SetUploadPreferenceStatus(LocalizationService.Translate("Testing upload…"));
        try
        {
            using var bmp = CreateTestBitmap();
            var result = await ImageUploadService.UploadBitmapAsync(
                bmp,
                new UploadRequest(provider, SuggestedFileName: "cybersnap-test.png")).ConfigureAwait(true);

            if (result.Success)
            {
                SetUploadPreferenceStatus(string.Empty);
                ToastWindow.Show(
                    LocalizationService.Translate("Upload works"),
                    result.PublicUrl ?? result.ClipboardText ?? LocalizationService.Translate("Uploaded"));
            }
            else
            {
                SetUploadPreferenceStatus(result.ErrorMessage ?? LocalizationService.Translate("Upload failed"));
                ToastWindow.ShowError(
                    LocalizationService.Translate("Upload failed"),
                    result.ErrorMessage ?? LocalizationService.Translate("Upload failed"));
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.upload-test", ex);
            SetUploadPreferenceStatus(ex.Message);
            ToastWindow.ShowError(
                LocalizationService.Translate("Upload failed"),
                ex.Message);
        }
        finally
        {
            _uploadTestInProgress = false;
            UploadTestCyberGemsBtn.IsEnabled = true;
            UploadTestImgBBBtn.IsEnabled = true;
            UploadTestImgurBtn.IsEnabled = UploadCredentialResolver.HasUserImgurClientId(_settingsService.Settings);
            UploadTestCustomBtn.IsEnabled = true;
        }
    }

    private string? GetCustomTestPrecheckError(UploadProviderKind provider)
    {
        if (provider != UploadProviderKind.Custom)
            return null;
        var s = _settingsService.Settings;
        switch (s.UploadCustomProtocol)
        {
            case UploadCustomProtocol.Ftp:
            case UploadCustomProtocol.Sftp:
                if (string.IsNullOrWhiteSpace(s.UploadCustomHost))
                    return LocalizationService.Translate("Enter a server host before testing.");
                break;
            case UploadCustomProtocol.S3:
                if (string.IsNullOrWhiteSpace(s.UploadS3Bucket)
                    || string.IsNullOrWhiteSpace(s.UploadS3AccessKey)
                    || string.IsNullOrWhiteSpace(s.UploadS3SecretKey))
                    return LocalizationService.Translate("Enter the S3 bucket, access key, and secret key before testing.");
                break;
            case UploadCustomProtocol.Webhook:
                var url = s.UploadWebhookUrl?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(url)
                    || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                    return LocalizationService.Translate("Enter a valid webhook URL before testing.");
                break;
        }
        return null;
    }

    private static Bitmap CreateTestBitmap()
    {
        var bmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        bmp.SetPixel(0, 0, Color.FromArgb(255, 0, 200, 255));
        return bmp;
    }
}
