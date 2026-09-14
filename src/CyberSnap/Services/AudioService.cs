using CyberSnap.Helpers;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CyberSnap.Services;

/// <summary>Lists audio devices and provides capture streams for recording.</summary>
public static class AudioService
{
    public sealed record AudioDevice(string Id, string Name, bool IsInput);

    // NOTE: the public wrappers below must not reference NAudio types themselves.
    // A missing NAudio.Wasapi.dll makes the runtime throw while JITting such a
    // method, before any try/catch inside it runs. The Inner methods are only
    // invoked (and JITted) when the probe succeeded.

    /// <summary>Get all active microphone input devices.</summary>
    public static List<AudioDevice> GetMicrophones()
    {
        if (!AudioDependencies.AreAvailable())
            return new List<AudioDevice>();

        return GetMicrophonesInner();
    }

    private static List<AudioDevice> GetMicrophonesInner()
    {
        var list = new List<AudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                list.Add(new AudioDevice(dev.ID, dev.FriendlyName, true));
                dev.Dispose();
            }
        }
        catch { }
        return list;
    }

    /// <summary>Get all active audio output devices (for desktop audio capture via loopback).</summary>
    public static List<AudioDevice> GetDesktopAudioDevices()
    {
        if (!AudioDependencies.AreAvailable())
            return new List<AudioDevice>();

        return GetDesktopAudioDevicesInner();
    }

    private static List<AudioDevice> GetDesktopAudioDevicesInner()
    {
        var list = new List<AudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new AudioDevice(dev.ID, dev.FriendlyName, false));
                dev.Dispose();
            }
        }
        catch { }
        return list;
    }

    /// <summary>Get the default microphone device ID, or null.</summary>
    public static string? GetDefaultMicrophoneId()
    {
        if (!AudioDependencies.AreAvailable())
            return null;

        return GetDefaultMicrophoneIdInner();
    }

    private static string? GetDefaultMicrophoneIdInner()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return dev.ID;
        }
        catch { return null; }
    }

    /// <summary>Get the default desktop audio device ID, or null.</summary>
    public static string? GetDefaultDesktopAudioId()
    {
        if (!AudioDependencies.AreAvailable())
            return null;

        return GetDefaultDesktopAudioIdInner();
    }

    private static string? GetDefaultDesktopAudioIdInner()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return dev.ID;
        }
        catch { return null; }
    }
}
