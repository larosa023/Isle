using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Isle.Core;

/// <summary>
/// The default speakers' volume, mute and live output level, through Windows Core Audio.
/// Follows the default output device when it changes. UI thread only, except <see cref="Changed"/>
/// handlers, which are already marshalled to the UI thread.
/// </summary>
public static class CoreAudio
{
    static IMMDeviceEnumerator? _enumerator;
    static IAudioEndpointVolume? _volume;
    static IAudioMeterInformation? _meter;
    static VolumeCallback? _callback;
    static string? _deviceId;
    static DispatcherTimer? _deviceWatch;
    static int _users;

    /// <summary>Volume or mute changed (level 0–1, muted). Raised on the UI thread.</summary>
    public static event Action<float, bool>? Changed;

    public static bool Available => _volume != null;

    /// <summary>Start following the default device. Call <see cref="Release"/> when done.</summary>
    public static void Acquire()
    {
        if (_users++ > 0) return;
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            Bind();
            _deviceWatch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
            _deviceWatch.Tick += (_, _) => CheckDevice();
            _deviceWatch.Start();
        }
        catch (Exception ex)
        {
            Log.Error("core audio", ex);
        }
    }

    public static void Release()
    {
        if (_users == 0 || --_users > 0) return;
        _deviceWatch?.Stop();
        _deviceWatch = null;
        Unbind();
        if (_enumerator != null) Marshal.ReleaseComObject(_enumerator);
        _enumerator = null;
    }

    static void CheckDevice()
    {
        try
        {
            if (_enumerator == null) return;
            _enumerator.GetDefaultAudioEndpoint(0, 1, out var dev);
            dev.GetId(out var id);
            Marshal.ReleaseComObject(dev);
            if (id != _deviceId) Bind();
        }
        catch
        {
            // no output device right now
            if (_volume != null) Unbind();
        }
    }

    static void Bind()
    {
        Unbind();
        if (_enumerator == null) return;
        try
        {
            _enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var dev);
            dev.GetId(out _deviceId);

            var iidVol = typeof(IAudioEndpointVolume).GUID;
            dev.Activate(ref iidVol, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var vol);
            _volume = (IAudioEndpointVolume)vol;

            var iidMeter = typeof(IAudioMeterInformation).GUID;
            dev.Activate(ref iidMeter, 23, IntPtr.Zero, out var meter);
            _meter = (IAudioMeterInformation)meter;
            Marshal.ReleaseComObject(dev);

            _callback = new VolumeCallback();
            _volume.RegisterControlChangeNotify(_callback);
        }
        catch (Exception ex)
        {
            Log.Info("core audio bind: " + ex.Message);
            Unbind();
        }
    }

    static void Unbind()
    {
        try
        {
            if (_volume != null && _callback != null) _volume.UnregisterControlChangeNotify(_callback);
        }
        catch { }
        if (_volume != null) Marshal.ReleaseComObject(_volume);
        if (_meter != null) Marshal.ReleaseComObject(_meter);
        _volume = null;
        _meter = null;
        _callback = null;
        _deviceId = null;
    }

    /// <summary>Peak output level right now, 0–1 (0 when nothing is playing).</summary>
    public static float Peak()
    {
        try
        {
            if (_meter != null && _meter.GetPeakValue(out var p) == 0) return p;
        }
        catch { }
        return 0;
    }

    public static (float level, bool muted) Get()
    {
        try
        {
            if (_volume != null)
            {
                _volume.GetMasterVolumeLevelScalar(out var level);
                _volume.GetMute(out var muted);
                return (level, muted);
            }
        }
        catch { }
        return (0, false);
    }

    public static void Set(float level)
    {
        try
        {
            var ctx = Guid.Empty;
            _volume?.SetMasterVolumeLevelScalar(Math.Clamp(level, 0, 1), ref ctx);
            if (level > 0) _volume?.SetMute(false, ref ctx);
        }
        catch (Exception ex)
        {
            Log.Error("set volume", ex);
        }
    }

    /// <summary>Nudge volume by whole percent steps.</summary>
    public static void Step(int percent)
    {
        var (level, _) = Get();
        Set((float)Math.Round(level * 100 + percent) / 100f);
    }

    public static void ToggleMute()
    {
        try
        {
            var ctx = Guid.Empty;
            var (_, muted) = Get();
            _volume?.SetMute(!muted, ref ctx);
        }
        catch (Exception ex)
        {
            Log.Error("mute", ex);
        }
    }

    // ───────────── microphone (default communications mic), on demand ─────────────

    /// <summary>Runs <paramref name="use"/> with the default call microphone's volume control.</summary>
    static T WithMic<T>(Func<IAudioEndpointVolume, T> use, T fallback)
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        IAudioEndpointVolume? vol = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            en.GetDefaultAudioEndpoint(1 /* capture */, 2 /* communications */, out dev);
            var iid = typeof(IAudioEndpointVolume).GUID;
            dev.Activate(ref iid, 23, IntPtr.Zero, out var o);
            vol = (IAudioEndpointVolume)o;
            return use(vol);
        }
        catch
        {
            return fallback;
        }
        finally
        {
            if (vol != null) Marshal.ReleaseComObject(vol);
            if (dev != null) Marshal.ReleaseComObject(dev);
            if (en != null) Marshal.ReleaseComObject(en);
        }
    }

    /// <summary>Whether the default call mic is muted (null = no mic).</summary>
    public static bool? MicMuted() => WithMic<bool?>(v => { v.GetMute(out var m); return m; }, null);

    public static void SetMicMuted(bool mute) => WithMic(v =>
    {
        var ctx = Guid.Empty;
        v.SetMute(mute, ref ctx);
        return true;
    }, false);

    [ComVisible(true)]
    public sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr data)
        {
            try
            {
                var d = Marshal.PtrToStructure<VolumeNotification>(data);
                float level = d.MasterVolume;
                bool muted = d.Muted != 0;
                Ui.Post(() => Changed?.Invoke(level, muted));
            }
            catch { }
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct VolumeNotification
    {
        public Guid EventContext;
        public int Muted;
        public float MasterVolume;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float levelDb, ref Guid context);
        void SetMasterVolumeLevelScalar(float level, ref Guid context);
        void GetMasterVolumeLevel(out float levelDb);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        void GetChannelVolumeLevel(uint channel, out float levelDb);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
