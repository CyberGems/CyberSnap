using CyberSnap.Services;

namespace CyberSnap.Helpers;

/// <summary>
/// Remembers the last emoji chosen in the editor or capture overlay so activating
/// the Emoji tool always arms a glyph — never a blank placement cursor.
/// </summary>
public static class LastUsedEmoji
{
    private static string? _session;

    public static string Default =>
        EmojiCatalog.Items.Length > 0 ? EmojiCatalog.Items[0].emoji : "\U0001F600";

    public static string Get()
    {
        if (!string.IsNullOrEmpty(_session))
            return _session;

        var saved = SettingsService.LoadStatic()?.LastEmoji;
        _session = string.IsNullOrWhiteSpace(saved) ? Default : saved;
        return _session;
    }

    public static void Remember(string emoji)
    {
        if (string.IsNullOrEmpty(emoji))
            return;
        if (string.Equals(_session, emoji, StringComparison.Ordinal))
            return;

        _session = emoji;

        if (System.Windows.Application.Current is CyberSnap.App app)
            app.PersistLastEmoji(emoji);
        else
            SettingsService.SaveLastEmoji(emoji);
    }
}
