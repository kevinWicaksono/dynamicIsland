using System;
using System.Runtime.InteropServices;

namespace DynamicIsland;

/// <summary>
/// Reads / sets the master volume and reads the current output peak level of the default
/// playback device using the Windows Core Audio COM API directly (no extra packages, no internet).
/// The peak level is a single number (0..1) – no audio data is ever captured or stored.
/// </summary>
internal static class AudioVolume
{
    // ───────── Volume ─────────

    public static float? GetVolume()
    {
        var ep = Activate<IAudioEndpointVolume>();
        if (ep == null) return null;
        try { return ep.GetMasterVolumeLevelScalar(out var v) == 0 ? v : null; }
        catch { return null; }
    }

    public static void SetVolume(float v01)
    {
        var ep = Activate<IAudioEndpointVolume>();
        if (ep == null) return;
        try { var g = Guid.Empty; ep.SetMasterVolumeLevelScalar(Math.Clamp(v01, 0f, 1f), ref g); } catch { }
    }

    public static bool? GetMute()
    {
        var ep = Activate<IAudioEndpointVolume>();
        if (ep == null) return null;
        try { return ep.GetMute(out var m) == 0 ? m : null; }
        catch { return null; }
    }

    public static void SetMute(bool mute)
    {
        var ep = Activate<IAudioEndpointVolume>();
        if (ep == null) return;
        try { var g = Guid.Empty; ep.SetMute(mute, ref g); } catch { }
    }

    // ───────── Peak level (for the visualizer) ─────────

    private static IAudioMeterInformation? _meter;
    private static DateTime _meterTime;

    /// <summary>Current peak output level 0..1, or null if unavailable.</summary>
    public static float? GetPeak()
    {
        try
        {
            // Re-acquire every few seconds so a changed default output device is picked up.
            if (_meter == null || (DateTime.UtcNow - _meterTime).TotalSeconds > 5)
            {
                _meter = Activate<IAudioMeterInformation>();
                _meterTime = DateTime.UtcNow;
            }
            if (_meter == null) return null;
            return _meter.GetPeakValue(out var p) == 0 ? p : null;
        }
        catch
        {
            _meter = null;
            return null;
        }
    }

    // ───────── COM plumbing ─────────

    private static T? Activate<T>() where T : class
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device);
            var iid = typeof(T).GUID;
            device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj);
            return (T)obj;
        }
        catch { return null; }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
