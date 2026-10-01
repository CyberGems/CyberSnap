namespace CyberSnap.UI;

/// <summary>Sister apps shown in the About window. CyberSnap itself is omitted.</summary>
internal sealed record SuiteApp(string Slug, string Name, string PitchEn, string PitchEs, string Site);

internal static class SuiteCatalog
{
    public static IReadOnlyList<SuiteApp> Others { get; } =
    [
        new("cyberclock", "CyberClock", "Desktop clock", "Reloj de escritorio", "https://cybergems.org/apps/cyberclock/"),
        new("cyberfeeds", "CyberFeeds", "RSS reader", "Lector RSS", "https://cybergems.org/apps/cyberfeeds/"),
        new("cyberlauncher", "CyberLauncher", "App launcher", "Lanzador de apps", "https://cybergems.org/apps/cyberlauncher/"),
        new("cybermanager", "CyberManager", "Task manager", "Administrador de tareas", "https://cybergems.org/apps/cybermanager/"),
        new("cybernotes", "CyberNotes", "Notes", "Notas", "https://cybergems.org/apps/cybernotes/"),
        new("cyberpaste", "CyberPaste", "Clipboard", "Portapapeles", "https://cybergems.org/apps/cyberpaste/"),
        new("cybertray", "CyberTray", "Shortcut manager", "Accesos directos", "https://cybergems.org/apps/cybertray/"),
        new("cyberviewer", "CyberViewer", "Image viewer", "Visor de imágenes", "https://cybergems.org/apps/cyberviewer/"),
        new("cyberwall", "CyberWall", "Firewall", "Firewall", "https://cybergems.org/apps/cyberwall/"),
    ];

    public static IReadOnlyList<SuiteApp> Pick(int count, Random? random = null)
    {
        random ??= Random.Shared;
        var pool = Others.ToList();
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        return pool.Take(Math.Min(count, pool.Count)).ToList();
    }
}
