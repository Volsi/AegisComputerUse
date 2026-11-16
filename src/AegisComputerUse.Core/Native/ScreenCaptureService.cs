using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AegisComputerUse.Core.Native;

namespace AegisComputerUse.Core.Native;

public sealed record ScreenCaptureFrame(
    byte[] ImageBytes,
    string MimeType,
    int OriginX,
    int OriginY,
    int ScreenWidth,
    int ScreenHeight);

public sealed record WindowTargetInfo(
    IntPtr Handle,
    string Title,
    Win32Interop.RECT Bounds)
{
    public override string ToString() => Title;
}

public sealed record MonitorTargetInfo(
    int Index,
    string Name,
    Win32Interop.RECT Bounds,
    bool IsPrimary)
{
    public override string ToString() => IsPrimary ? $"{Name} (Основной)" : Name;
}

public interface IScreenCaptureService
{
    ScreenCaptureFrame CaptureDesktopOrWindow(Win32Interop.RECT? targetRect = null, int maxOutputWidth = 1280);
}

public sealed class ScreenCaptureService : IScreenCaptureService
{
    public static IReadOnlyList<MonitorTargetInfo> GetMonitors()
    {
        var list = new List<MonitorTargetInfo>();
        int idx = 1;
        Win32Interop.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref Win32Interop.RECT _, IntPtr _) =>
        {
            var mi = new Win32Interop.MONITORINFOEX { cbSize = Marshal.SizeOf<Win32Interop.MONITORINFOEX>() };
            if (Win32Interop.GetMonitorInfo(hMon, ref mi))
            {
                bool isPrimary = (mi.dwFlags & Win32Interop.MONITORINFOF_PRIMARY) != 0;
                string device = mi.szDevice?.Trim() ?? string.Empty;
                string name = string.IsNullOrWhiteSpace(device) ? $"Дисплей {idx}" : $"Дисплей {idx} ({device})";
                list.Add(new MonitorTargetInfo(idx++, name, mi.rcMonitor, isPrimary));
            }
            return true;
        }, IntPtr.Zero);

        if (list.Count == 0)
        {
            int vx = Win32Interop.GetSystemMetrics(Win32Interop.SM_XVIRTUALSCREEN);
            int vy = Win32Interop.GetSystemMetrics(Win32Interop.SM_YVIRTUALSCREEN);
            int vw = Math.Max(1, Win32Interop.GetSystemMetrics(Win32Interop.SM_CXVIRTUALSCREEN));
            int vh = Math.Max(1, Win32Interop.GetSystemMetrics(Win32Interop.SM_CYVIRTUALSCREEN));
            list.Add(new MonitorTargetInfo(1, "Дисплей 1", new Win32Interop.RECT { Left = vx, Top = vy, Right = vx + vw, Bottom = vy + vh }, true));
        }

        return list;
    }

    public static IReadOnlyList<WindowTargetInfo> GetVisibleWindows(IntPtr excludeHwnd)
    {
        var list = new List<WindowTargetInfo>();
        Win32Interop.EnumWindows((hwnd, _) =>
        {
            if (hwnd == excludeHwnd || !Win32Interop.IsWindowVisible(hwnd))
                return true;

            string title = Win32Interop.GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
                return true;

            if (Win32Interop.GetWindowRect(hwnd, out var rect) &&
                rect.Right - rect.Left > 100 &&
                rect.Bottom - rect.Top > 80)
            {
                list.Add(new WindowTargetInfo(hwnd, title, rect));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public ScreenCaptureFrame CaptureDesktopOrWindow(
        Win32Interop.RECT? targetRect = null,
        int maxOutputWidth = 1280)
    {
        int originX;
        int originY;
        int width;
        int height;

        if (targetRect is { } r && r.Right > r.Left && r.Bottom > r.Top)
        {
            originX = r.Left;
            originY = r.Top;
            width = r.Right - r.Left;
            height = r.Bottom - r.Top;
        }
        else
        {
            originX = Win32Interop.GetSystemMetrics(Win32Interop.SM_XVIRTUALSCREEN);
            originY = Win32Interop.GetSystemMetrics(Win32Interop.SM_YVIRTUALSCREEN);
            width = Math.Max(1, Win32Interop.GetSystemMetrics(Win32Interop.SM_CXVIRTUALSCREEN));
            height = Math.Max(1, Win32Interop.GetSystemMetrics(Win32Interop.SM_CYVIRTUALSCREEN));
        }

        IntPtr hdcSrc = IntPtr.Zero;
        IntPtr hdcDest = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;

        try
        {
            hdcSrc = Win32Interop.GetDC(IntPtr.Zero);
            if (hdcSrc == IntPtr.Zero)
                throw new InvalidOperationException($"Screen DC unavailable (error {Marshal.GetLastWin32Error()}).");

            hdcDest = Win32Interop.CreateCompatibleDC(hdcSrc);
            hBitmap = Win32Interop.CreateCompatibleBitmap(hdcSrc, width, height);
            oldObj = Win32Interop.SelectObject(hdcDest, hBitmap);

            bool bltOk = Win32Interop.BitBlt(hdcDest, 0, 0, width, height, hdcSrc, originX, originY,
                (uint)(Win32Interop.SRCCOPY | Win32Interop.CAPTUREBLT));
            if (!bltOk)
            {
                bltOk = Win32Interop.BitBlt(hdcDest, 0, 0, width, height, hdcSrc, originX, originY, (uint)Win32Interop.SRCCOPY);
            }

            if (!bltOk)
            {
                int err = Marshal.GetLastWin32Error();
                string detail = err is 5 or 6
                    ? $"Screen capture unavailable due to Windows desktop policy or disconnected/sandboxed display handle (error {err})."
                    : $"Screen capture BitBlt failed (error {err}).";
                throw new InvalidOperationException(detail);
            }

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            if (maxOutputWidth > 0 && width > maxOutputWidth)
            {
                double scale = (double)maxOutputWidth / width;
                source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }
            source.Freeze();

            byte[] pngBytes = EncodePng(source);
            if (pngBytes.Length <= 220_000)
            {
                return new ScreenCaptureFrame(pngBytes, "image/png", originX, originY, width, height);
            }

            byte[] jpgBytes = EncodeJpeg(source, 82);
            return new ScreenCaptureFrame(jpgBytes, "image/jpeg", originX, originY, width, height);
        }
        finally
        {
            if (oldObj != IntPtr.Zero && hdcDest != IntPtr.Zero)
                Win32Interop.SelectObject(hdcDest, oldObj);
            if (hBitmap != IntPtr.Zero)
                Win32Interop.DeleteObject(hBitmap);
            if (hdcDest != IntPtr.Zero)
                Win32Interop.DeleteDC(hdcDest);
            if (hdcSrc != IntPtr.Zero)
                Win32Interop.ReleaseDC(IntPtr.Zero, hdcSrc);
        }
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static byte[] EncodeJpeg(BitmapSource source, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
