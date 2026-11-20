using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Isle.Core;

/// <summary>One app in the sound mixer: every audio session it has on the current speakers.</summary>
public sealed class MixerApp
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public bool IsSystem { get; init; }
    /// <summary>Playing (or ready to play) right now, as opposed to idle.</summary>
    public bool Active { get; internal set; }

    internal readonly List<(CoreAudio.ISimpleAudioVolume Volume, CoreAudio.IAudioMeterInformation? Meter)> Parts = new();
    internal readonly List<object> Com = new();

    public float Volume
    {
        get
        {
            try { Parts[0].Volume.GetMasterVolume(out var v); return v; }
            catch { return 0; }
        }
    }

    public bool Muted
    {
        get
        {
            try { Parts[0].Volume.GetMute(out var m); return m; }
            catch { return false; }
        }
    }

    public float Peak()
    {
        float p = 0;
        foreach (var (_, meter) in Parts)
        {
            try { if (meter != null && meter.GetPeakValue(out var v) == 0 && v > p) p = v; }
            catch { }
        }
        return p;
    }

    public void SetVolume(float level)
    {
        level = Math.Clamp(level, 0, 1);
        var ctx = Guid.Empty;
        foreach (var (vol, _) in Parts)
        {
            try
            {
                vol.SetMasterVolume(level, ref ctx);
                if (level > 0) vol.SetMute(false, ref ctx);
            }
            catch { }
        }
    }

    public void SetMuted(bool mute)
    {
        var ctx = Guid.Empty;
        foreach (var (vol, _) in Parts)
        {
            try { vol.SetMute(mute, ref ctx); } catch { }
        }
    }
}

/// <summary>A speaker or microphone Windows knows about.</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// The sound mixer: per-app volume on the default speakers, the default microphone's level and
/// mute, and switching the default speakers / microphone. All of it is live only while the mixer
/// is open (<see cref="RefreshMixer"/> … <see cref="ReleaseMixer"/>), so it costs nothing otherwise.
/// UI thread only.
/// </summary>
public static partial class CoreAudio
{
    static List<MixerApp> _apps = new();
    static readonly Dictionary<string, (string name, ImageSource? icon)> AppInfoCache = new(StringComparer.OrdinalIgnoreCase);
    static IAudioEndpointVolume? _micVolume;
    static IAudioMeterInformation? _micMeter;
    static string? _micId;

    /// <summary>The apps with sound sessions, from the last <see cref="RefreshMixer"/>. Stable order.</summary>
    public static IReadOnlyList<MixerApp> MixerApps => _apps;

    /// <summary>Re-reads the apps and the default microphone. Call about once a second while the mixer is open.</summary>
    public static void RefreshMixer()
    {
        RefreshApps();
        RefreshMic();
    }

    public static void ReleaseMixer()
    {
        foreach (var a in _apps) ReleaseApp(a);
        _apps = new();
        ReleaseMic();
    }

    static IMMDeviceEnumerator NewEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    // ───────────────────────────── apps ─────────────────────────────

    static void RefreshApps()
    {
        var found = new Dictionary<string, MixerApp>(StringComparer.OrdinalIgnoreCase);
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        IAudioSessionManager2? mgr = null;
        IAudioSessionEnumerator? sessions = null;
        int self = Environment.ProcessId;
        try
        {
            en = NewEnumerator();
            en.GetDefaultAudioEndpoint(0, 1, out dev);
            var iid = typeof(IAudioSessionManager2).GUID;
            dev.Activate(ref iid, 23, IntPtr.Zero, out var o);
            mgr = (IAudioSessionManager2)o;
            mgr.GetSessionEnumerator(out sessions);
            sessions.GetCount(out int count);

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl? ctl = null;
                bool kept = false;
                try
                {
                    sessions.GetSession(i, out ctl);
                    var c2 = (IAudioSessionControl2)ctl;
                    ctl.GetState(out int state);
                    if (state == 2) continue; // expired
                    c2.GetProcessId(out uint pid);
                    bool system = c2.IsSystemSoundsSession() == 0;
                    if (!system && (pid == 0 || pid == self)) continue;

                    string key;
                    string name;
                    ImageSource? icon = null;
                    if (system)
                    {
                        key = "system";
                        name = "System sounds";
                    }
                    else
                    {
                        var path = ProcessPath(pid);
                        if (path == null) continue; // the app has closed
                        key = path;
                        (name, icon) = Describe(path);
                    }

                    if (!found.TryGetValue(key, out var app))
                        found[key] = app = new MixerApp { Key = key, Name = name, Icon = icon, IsSystem = system };
                    app.Parts.Add(((ISimpleAudioVolume)ctl, ctl as IAudioMeterInformation));
                    app.Com.Add(ctl);
                    if (state == 1) app.Active = true;
                    kept = true;
                }
                catch (Exception ex)
                {
                    Log.Info("mixer session: " + ex.Message);
                }
                finally
                {
                    if (!kept && ctl != null) Marshal.FinalReleaseComObject(ctl);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("mixer: " + ex.Message);
        }
        finally
        {
            if (sessions != null) Marshal.ReleaseComObject(sessions);
            if (mgr != null) Marshal.ReleaseComObject(mgr);
            if (dev != null) Marshal.ReleaseComObject(dev);
            if (en != null) Marshal.ReleaseComObject(en);
        }

        foreach (var a in _apps) ReleaseApp(a);
        _apps = found.Values
            .OrderBy(a => a.IsSystem)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    static void ReleaseApp(MixerApp a)
    {
        foreach (var c in a.Com)
        {
            try { Marshal.FinalReleaseComObject(c); } catch { }
        }
        a.Com.Clear();
        a.Parts.Clear();
    }

    /// <summary>The app's own name ("Google Chrome", "Spotify") and icon, from its .exe.</summary>
    static (string name, ImageSource? icon) Describe(string path)
    {
        if (AppInfoCache.TryGetValue(path, out var cached)) return cached;
        string name = Path.GetFileNameWithoutExtension(path);
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var desc = info.FileDescription?.Trim();
            var product = info.ProductName?.Trim();
            // Some apps describe the file ("Chrome Helper", "Discord.exe"); prefer the product then.
            if (!string.IsNullOrEmpty(desc) && !desc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && desc.Length <= 40) name = desc;
            else if (!string.IsNullOrEmpty(product) && product.Length <= 40) name = product;
        }
        catch { }
        if (name.Length > 0 && char.IsLower(name[0])) name = char.ToUpper(name[0]) + name[1..];

        ImageSource? icon = null;
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ico != null)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                src.Freeze();
                icon = src;
            }
        }
        catch { }

        if (AppInfoCache.Count > 200) AppInfoCache.Clear();
        return AppInfoCache[path] = (name, icon);
    }

    static string? ProcessPath(uint pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    // ───────────────────────────── microphone ─────────────────────────────

    static void RefreshMic()
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        try
        {
            en = NewEnumerator();
            en.GetDefaultAudioEndpoint(1 /* capture */, 0 /* console = "default device" */, out dev);
            dev.GetId(out var id);
            if (id == _micId && _micVolume != null) return;
            ReleaseMic();
            var iidVol = typeof(IAudioEndpointVolume).GUID;
            dev.Activate(ref iidVol, 23, IntPtr.Zero, out var vol);
            _micVolume = (IAudioEndpointVolume)vol;
            var iidMeter = typeof(IAudioMeterInformation).GUID;
            dev.Activate(ref iidMeter, 23, IntPtr.Zero, out var meter);
            _micMeter = (IAudioMeterInformation)meter;
            _micId = id;
        }
        catch
        {
            ReleaseMic(); // no microphone
        }
        finally
        {
            if (dev != null) Marshal.ReleaseComObject(dev);
            if (en != null) Marshal.ReleaseComObject(en);
        }
    }

    static void ReleaseMic()
    {
        if (_micVolume != null) Marshal.ReleaseComObject(_micVolume);
        if (_micMeter != null) Marshal.ReleaseComObject(_micMeter);
        _micVolume = null;
        _micMeter = null;
        _micId = null;
    }

    public static bool HasMic => _micVolume != null;

    public static (float level, bool muted) MicState()
    {
        try
        {
            if (_micVolume != null)
            {
                _micVolume.GetMasterVolumeLevelScalar(out var level);
                _micVolume.GetMute(out var muted);
                return (level, muted);
            }
        }
        catch { }
        return (0, false);
    }

    public static void SetMicLevel(float level)
    {
        try
        {
            var ctx = Guid.Empty;
            _micVolume?.SetMasterVolumeLevelScalar(Math.Clamp(level, 0, 1), ref ctx);
        }
        catch (Exception ex)
        {
            Log.Error("mic level", ex);
        }
    }

    public static void SetDefaultMicMuted(bool mute)
    {
        try
        {
            var ctx = Guid.Empty;
            _micVolume?.SetMute(mute, ref ctx);
        }
        catch (Exception ex)
        {
            Log.Error("mic mute", ex);
        }
    }

    /// <summary>The microphone's level right now (only moves while some app is listening to it).</summary>
    public static float MicPeak()
    {
        try
        {
            if (_micMeter != null && _micMeter.GetPeakValue(out var p) == 0) return p;
        }
        catch { }
        return 0;
    }

    // ───────────────────────────── devices ─────────────────────────────

    /// <summary>Connected speakers (or microphones), with the default marked.</summary>
    public static List<AudioDevice> Devices(bool capture)
    {
        var list = new List<AudioDevice>();
        IMMDeviceEnumerator? en = null;
        IMMDeviceCollection? all = null;
        try
        {
            en = NewEnumerator();
            string? defaultId = null;
            try
            {
                en.GetDefaultAudioEndpoint(capture ? 1 : 0, 0, out var def);
                def.GetId(out defaultId);
                Marshal.ReleaseComObject(def);
            }
            catch { }

            if (en.EnumAudioEndpoints(capture ? 1 : 0, 1 /* active */, out all) != 0) return list;
            all.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                IMMDevice? dev = null;
                try
                {
                    all.Item(i, out dev);
                    dev.GetId(out var id);
                    list.Add(new AudioDevice(id, FriendlyName(dev) ?? "Audio device", id == defaultId));
                }
                catch { }
                finally
                {
                    if (dev != null) Marshal.ReleaseComObject(dev);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("audio devices: " + ex.Message);
        }
        finally
        {
            if (all != null) Marshal.ReleaseComObject(all);
            if (en != null) Marshal.ReleaseComObject(en);
        }
        return list.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static string? DefaultDeviceName(bool capture)
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        try
        {
            en = NewEnumerator();
            en.GetDefaultAudioEndpoint(capture ? 1 : 0, 0, out dev);
            return FriendlyName(dev);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (dev != null) Marshal.ReleaseComObject(dev);
            if (en != null) Marshal.ReleaseComObject(en);
        }
    }

    /// <summary>Makes a device the Windows default — for apps, games and calls alike.</summary>
    public static bool SetDefaultDevice(string id)
    {
        IPolicyConfig? policy = null;
        try
        {
            policy = (IPolicyConfig)new PolicyConfigComObject();
            bool ok = true;
            for (int role = 0; role < 3; role++) ok &= policy.SetDefaultEndpoint(id, role) == 0;
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error("set default device", ex);
            return false;
        }
        finally
        {
            if (policy != null) Marshal.ReleaseComObject(policy);
        }
    }

    static readonly PropertyKey FriendlyNameKey = new() { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 };

    static string? FriendlyName(IMMDevice dev)
    {
        IPropertyStore? store = null;
        var pv = new PropVariant();
        try
        {
            if (dev.OpenPropertyStore(0 /* read */, out store) != 0) return null;
            var key = FriendlyNameKey;
            store.GetValue(ref key, out pv);
            return pv.Vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.Pointer) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            PropVariantClear(ref pv);
            if (store != null) Marshal.ReleaseComObject(store);
        }
    }

    // ───────────────────────────── interop ─────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant pv);

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey
    {
        public Guid Fmtid;
        public int Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant
    {
        public ushort Vt;
        public ushort R1, R2, R3;
        public IntPtr Pointer;
        public IntPtr Extra;
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl
    {
        void GetState(out int state);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        void GetState(out int state);
        void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        void GetGroupingParam(out Guid group);
        void SetGroupingParam(ref Guid group, ref Guid context);
        void RegisterAudioSessionNotification(IntPtr client);
        void UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        void SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        void SetMasterVolume(float level, ref Guid context);
        void GetMasterVolume(out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // Not documented by Microsoft, but stable since Windows 7 — the same call Windows' own Sound
    // settings make (EarTrumpet, SoundSwitch and others use it too).
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigComObject { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int defaultPeriod, IntPtr defaultP, IntPtr minP);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}
