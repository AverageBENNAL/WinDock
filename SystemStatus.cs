using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace WinDock;

/// <summary>Reads the bits of system state the dock shows (sound, network, battery) and runs power actions.</summary>
internal static class SystemStatus
{
    // ------------------------------------------------------------------ sound

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    /// <summary>Volume (0-1) and mute state of the default speakers; false if there is no audio device.</summary>
    public static bool TryGetVolume(out float level, out bool muted)
    {
        level = 0;
        muted = false;
        object? enumerator = null, device = null, volume = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            if (((IMMDeviceEnumerator)enumerator).GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var endpoint) != 0)
                return false;
            device = endpoint;
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            if (endpoint.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out volume) != 0) return false;
            var control = (IAudioEndpointVolume)volume;
            return control.GetMasterVolumeLevelScalar(out level) == 0 && control.GetMute(out muted) == 0;
        }
        catch { return false; }
        finally
        {
            if (volume != null) Marshal.ReleaseComObject(volume);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    // ---------------------------------------------------------------- network

    public enum Network { None, Wired, WiFi }

    public static Network GetNetwork(out string name)
    {
        name = "";
        try
        {
            var active = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                && n.GetIPProperties().GatewayAddresses.Count > 0);
            if (active == null) return Network.None;
            name = active.Name;
            return active.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? Network.WiFi : Network.Wired;
        }
        catch { return Network.None; }
    }

    // ---------------------------------------------------------------- battery

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>False on a PC with no battery.</summary>
    public static bool TryGetBattery(out int percent, out bool charging)
    {
        percent = 0;
        charging = false;
        if (!GetSystemPowerStatus(out var status) || (status.BatteryFlag & 128) != 0 || status.BatteryLifePercent > 100)
            return false;
        percent = status.BatteryLifePercent;
        charging = status.ACLineStatus == 1;
        return true;
    }

    // ------------------------------------------------------------------ power

    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvents);

    public static void Lock() => LockWorkStation();
    public static void Sleep() => SetSuspendState(false, false, false);
    public static void SignOut() => Shutdown("/l");
    public static void Restart() => Shutdown("/r /t 0");
    public static void ShutDown() => Shutdown("/s /t 0");

    static void Shutdown(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { }
    }
}
