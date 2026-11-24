using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Isle.Core;

/// <summary>
/// Turns an app's Windows ID (AppUserModelID — what media sessions and notifications report, e.g.
/// "app.ripple.desktop" or "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify") into its real name and
/// icon, exactly as the Start menu shows them (shell:AppsFolder). Works for Store apps and for
/// installed desktop apps whose shortcut carries the ID (Tauri, Electron and most installers).
/// Cached; UI thread (shell COM wants STA).
/// </summary>
public static class AppIdentity
{
    public sealed record Info(string? Name, ImageSource? Icon);

    static readonly Dictionary<string, Info> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Info Resolve(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return new Info(null, null);
        if (Cache.TryGetValue(aumid, out var hit)) return hit;

        Info info;
        try
        {
            info = Lookup(aumid);
        }
        catch (Exception ex)
        {
            Log.Info($"app identity {aumid}: {ex.Message}");
            info = new Info(null, null);
        }
        if (Cache.Count > 100) Cache.Clear();
        return Cache[aumid] = info;
    }

    static Info Lookup(string aumid)
    {
        var iid = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + aumid, IntPtr.Zero, ref iid, out var item) != 0 || item == null)
            return new Info(null, null);
        try
        {
            string? name = null;
            if (item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var p) == 0 && p != IntPtr.Zero)
            {
                name = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
            }
            // An unknown ID comes back as itself; that's not a name.
            if (string.IsNullOrWhiteSpace(name) || string.Equals(name, aumid, StringComparison.OrdinalIgnoreCase)) name = null;

            ImageSource? icon = null;
            if (item is IShellItemImageFactory factory &&
                factory.GetImage(new Size { Cx = 64, Cy = 64 }, 0x1 /* BIGGERSIZEOK */ | 0x4 /* ICONONLY */, out var hbmp) == 0 && hbmp != IntPtr.Zero)
            {
                try { icon = FromHBitmap(hbmp); }
                finally { DeleteObject(hbmp); }
            }
            return new Info(name, icon);
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>A 32-bit shell bitmap with its transparency kept (Imaging.CreateBitmapSourceFromHBitmap drops it).</summary>
    static BitmapSource? FromHBitmap(IntPtr hbmp)
    {
        if (GetObject(hbmp, Marshal.SizeOf<Bitmap>(), out var bm) == 0 || bm.Width <= 0 || bm.Height <= 0) return null;
        int w = bm.Width, h = bm.Height, stride = w * 4;
        var pixels = new byte[stride * h];
        var bi = new BitmapInfoHeader
        {
            Size = 40, // sizeof(BITMAPINFOHEADER), not counting the spare room below
            Width = w,
            Height = -h, // top-down
            Planes = 1,
            BitCount = 32,
        };
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbmp, 0, (uint)h, pixels, ref bi, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }

        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { anyAlpha = true; break; }

        var src = BitmapSource.Create(w, h, 96, 96, anyAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, pixels, stride);
        src.Freeze();
        return src;
    }

    // ── interop ──

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid, out IShellItem item);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    static extern int GetObject(IntPtr obj, int size, out Bitmap bitmap);

    [DllImport("gdi32.dll")]
    static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [StructLayout(LayoutKind.Sequential)]
    struct Size { public int Cx, Cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct Bitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public ushort Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        // GetDIBits writes a color table after the header for some formats; 32-bit BI_RGB has none,
        // but leave room so a driver that writes masks can't overrun.
        public int Pad0, Pad1, Pad2, Pad3;
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindCtx, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(int sigdn, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size size, int flags, out IntPtr hbitmap);
    }
}
