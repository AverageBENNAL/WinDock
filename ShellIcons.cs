using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinDock;

/// <summary>Loads large shell icons for files, shortcuts and shell:AppsFolder items.</summary>
internal static class ShellIcons
{
    const int SIIGBF_ICONONLY = 0x4;

    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string parsingName, int size)
    {
        string key = parsingName + "|" + size;
        if (Cache.TryGetValue(key, out var image)) return image;
        image = Load(parsingName, size);
        if (image != null) Cache[key] = image;   // a failure may be temporary (e.g. right after sign-in), so retry later
        return image;
    }

    public static ImageSource? Load(string parsingName, int size)
    {
        try
        {
            Guid iid = typeof(Native.IShellItemImageFactory).GUID;
            if (Native.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null)
                return null;
            if (factory.GetImage(new Native.SIZE { cx = size, cy = size }, SIIGBF_ICONONLY, out IntPtr hbm) != 0)
                return null;
            try { return FromHBitmap(hbm); }
            finally { Native.DeleteObject(hbm); }
        }
        catch { return null; }
    }

    // Read the pixels ourselves so the alpha channel survives.
    static ImageSource? FromHBitmap(IntPtr hbm)
    {
        if (Native.GetObject(hbm, System.Runtime.InteropServices.Marshal.SizeOf<Native.BITMAP>(), out var bm) == 0)
            return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32
        };
        var bits = new byte[w * h * 4];
        IntPtr dc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(dc, hbm, 0, (uint)h, bits, ref header, 0) == 0) return null;
        }
        finally { Native.ReleaseDC(IntPtr.Zero, dc); }

        bool hasAlpha = false;
        for (int i = 3; i < bits.Length; i += 4)
            if (bits[i] != 0) { hasAlpha = true; break; }

        var source = BitmapSource.Create(w, h, 96, 96, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, bits, w * 4);
        source.Freeze();
        return source;
    }
}

/// <summary>A single background STA thread for shell COM work.</summary>
internal static class StaWorker
{
    static readonly BlockingCollection<Action> Queue = new();

    static StaWorker()
    {
        var thread = new Thread(() =>
        {
            foreach (var action in Queue.GetConsumingEnumerable())
            {
                try { action(); } catch { }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public static void Post(Action action) => Queue.Add(action);
}
