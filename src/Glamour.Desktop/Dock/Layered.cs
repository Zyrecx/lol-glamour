using System.Runtime.InteropServices;
using Glamour.Host.Panel;

namespace Glamour.Desktop.Dock;

/// <summary>A draw-only window over the client: borderless, no taskbar button, never activated, and every click passes through it.</summary>
internal sealed class LayerForm : Form
{
    public LayerForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle = (cp.ExStyle | WsExLayered | WsExTransparent | Win32.WsExToolWindow | Win32.WsExNoActivate) & ~Win32.WsExAppWindow;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084 /* WM_NCHITTEST */) { m.Result = -1 /* HTTRANSPARENT */; return; }
        base.WndProc(ref m);
    }

    private const int WsExLayered = 0x80000, WsExTransparent = 0x20;
}

/// <summary>The pixels of a <see cref="LayerForm"/>: a top-down 32-bit premultiplied DIB, pushed to the window with <c>UpdateLayeredWindow</c>.</summary>
internal sealed class LayerSurface : IDisposable
{
    private IntPtr _dc, _dib, _old;

    public IntPtr Bits { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public void Ensure(int w, int h)
    {
        if (_dib != IntPtr.Zero && w == Width && h == Height) return;
        Free();
        var info = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = w, Height = -h /* top-down */, Planes = 1, BitCount = 32 };
        _dc = CreateCompatibleDC(IntPtr.Zero);
        _dib = CreateDIBSection(_dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        Bits = bits;
        _old = SelectObject(_dc, _dib);
        (Width, Height) = (w, h);
    }

    /// <summary>Shows the surface as the window's content at <paramref name="bounds"/>' top-left corner.</summary>
    public void Push(IntPtr window, PixelRect bounds)
    {
        var size = new NativeSize { Cx = Width, Cy = Height };
        var at = new NativePoint { X = bounds.Left, Y = bounds.Top };
        var src = new NativePoint();
        var blend = new BlendFunction { Op = 0 /* AC_SRC_OVER */, Alpha = 255, Format = 1 /* AC_SRC_ALPHA */ };
        UpdateLayeredWindow(window, IntPtr.Zero, ref at, ref size, _dc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
    }

    private void Free()
    {
        if (_dc != IntPtr.Zero) { SelectObject(_dc, _old); DeleteDC(_dc); }
        if (_dib != IntPtr.Zero) DeleteObject(_dib);
        _dc = _dib = _old = Bits = IntPtr.Zero;
    }

    public void Dispose() => Free();

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant; }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref NativePoint at, ref NativeSize size, IntPtr src, ref NativePoint srcAt, uint key, ref BlendFunction blend, uint flags);
}
