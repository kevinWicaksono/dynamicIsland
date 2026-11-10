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

    /// <summary>Reads the default playback device's master volume as a value from 0 to 1.</summary>
    public static float? GetVolume()
    {
        var ep = Activate<IAudioEndpointVolume>();
        if (ep == null) return null;
        try { return ep.GetMasterVolumeLevelScalar(out var v) == 0 ? v : null; }
        catch { return null; }
    }

    /// <summary>Sets the default playback device's master volume, clamped to 0 through 1.</summary>
    public static void SetVolume(float v01)
    {
        var ep = Activate<IAudioEndpointVolume>();
        if (ep == null) return;
        try { var g = Guid.Empty; ep.SetMasterVolumeLevelScalar(Math.Clamp(v01, 0f, 1f), ref g); } catch { }
    }

    /// <summary>Reads whether the default playback device is muted.</summary>
    public static bool? GetMute()
    {
        var ep = Activate<IAudioEndpointVolume>();
        if (ep == null) return null;
        try { return ep.GetMute(out var m) == 0 ? m : null; }
        catch { return null; }
    }

    /// <summary>Sets the mute state of the default playback device.</summary>
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
    /// <summary>Reads the current output peak level from 0 to 1, or null when unavailable.</summary>
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

    /// <summary>Gets a Core Audio interface for the default multimedia playback device.</summary>
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
        /// <summary>Enumerates audio endpoints matching the requested flow and state.</summary>
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        /// <summary>Gets the default audio endpoint for the requested flow and role.</summary>
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        /// <summary>Activates the requested COM interface on this audio device.</summary>
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        /// <summary>Registers a client for volume-change notifications.</summary>
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        /// <summary>Unregisters a client for volume-change notifications.</summary>
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        /// <summary>Gets the number of audio channels.</summary>
        [PreserveSig] int GetChannelCount(out uint count);
        /// <summary>Sets the master volume in decibels.</summary>
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        /// <summary>Sets the master volume as a normalized value.</summary>
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        /// <summary>Gets the master volume in decibels.</summary>
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        /// <summary>Gets the master volume as a normalized value.</summary>
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        /// <summary>Sets an individual channel's volume in decibels.</summary>
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        /// <summary>Sets an individual channel's normalized volume.</summary>
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        /// <summary>Gets an individual channel's volume in decibels.</summary>
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        /// <summary>Gets an individual channel's normalized volume.</summary>
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        /// <summary>Sets whether the device is muted.</summary>
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        /// <summary>Gets whether the device is muted.</summary>
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        /// <summary>Gets the current peak output level from 0 to 1.</summary>
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
