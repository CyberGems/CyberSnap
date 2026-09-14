using System;
using System.Reflection;

namespace CyberSnap.Helpers;

/// <summary>
/// Probes for the NAudio assemblies (Core/Wasapi/WinMM). NAudio 2.x ships one
/// assembly per API and the runtime throws FileNotFoundException while JITting
/// any method that references a missing one — a try/catch inside that method
/// cannot help. Probe via assembly names (no static NAudio type references, so
/// this JITs fine) and let callers degrade to no-audio instead of crashing.
/// </summary>
internal static class AudioDependencies
{
    private static bool? _available;

    public static bool AreAvailable()
    {
        if (_available.HasValue)
            return _available.Value;

        bool ok = false;
        try
        {
            Assembly.Load("NAudio.Core");
            Assembly.Load("NAudio.Wasapi");
            Assembly.Load("NAudio.WinMM");
            ok = true;
        }
        catch
        {
            ok = false;
        }

        _available = ok;
        return ok;
    }
}
