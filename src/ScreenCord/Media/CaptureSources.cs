using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenCord.Media;

public enum CaptureSourceKind
{
    Screen,
    Window,
}

/// <summary>Tela (monitor) ou janela que pode ser compartilhada. <see cref="Detail"/> é o programa dono da janela.</summary>
public sealed record CaptureSource(CaptureSourceKind Kind, IntPtr Handle, string Title, string Detail, int Width, int Height, uint ProcessId)
{
    public bool IsScreen => Kind == CaptureSourceKind.Screen;

    /// <summary>Linha de baixo na lista: "1920 × 1080" ou "chrome · 1936 × 1048".</summary>
    public string Subtitle => string.IsNullOrEmpty(Detail) ? $"{Width} × {Height}" : $"{Detail} · {Width} × {Height}";
}

/// <summary>Lista as telas e as janelas visíveis que o Windows permite capturar.</summary>
public static class CaptureSources
{
    public static IReadOnlyList<CaptureSource> Screens()
    {
        var screens = new List<(CaptureSource Source, bool Primary)>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new Native.MonitorInfoEx { Size = Marshal.SizeOf<Native.MonitorInfoEx>() };
            if (Native.GetMonitorInfo(monitor, ref info))
            {
                var width = info.Monitor.Right - info.Monitor.Left;
                var height = info.Monitor.Bottom - info.Monitor.Top;
                var primary = (info.Flags & Native.MonitorInfoPrimary) != 0;
                screens.Add((new CaptureSource(
                    CaptureSourceKind.Screen,
                    monitor,
                    $"Tela {screens.Count + 1}{(primary ? " (principal)" : "")}",
                    "",
                    width,
                    height,
                    0), primary));
            }

            return true;
        }, IntPtr.Zero);

        // Principal primeiro, depois na ordem de enumeração.
        return screens.OrderByDescending(s => s.Primary).Select(s => s.Source).ToList();
    }

    /// <summary>Janelas de nível superior visíveis, com título, que não são ferramentas nem estão ocultas pelo sistema.</summary>
    public static IReadOnlyList<CaptureSource> Windows()
    {
        var ownProcess = (uint)Environment.ProcessId;
        var shell = Native.GetShellWindow();
        var windows = new List<CaptureSource>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (hwnd == shell || !Native.IsWindowVisible(hwnd))
            {
                return true;
            }

            var exStyle = Native.GetWindowLongPtr(hwnd, Native.GwlExStyle).ToInt64();
            if ((exStyle & Native.WsExToolWindow) != 0 || Native.GetWindow(hwnd, Native.GwOwner) != IntPtr.Zero && (exStyle & Native.WsExAppWindow) == 0)
            {
                return true;
            }

            if (Native.DwmGetWindowAttribute(hwnd, Native.DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            var length = Native.GetWindowTextLength(hwnd);
            if (length == 0)
            {
                return true;
            }

            var title = new StringBuilder(length + 1);
            Native.GetWindowText(hwnd, title, title.Capacity);
            Native.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == ownProcess)
            {
                return true;
            }

            // Minimizada (ex.: jogo em tela cheia após Alt+Tab), o retângulo vira um ícone de ~160 × 28 fora
            // da tela; o tamanho que conta é o de quando ela for restaurada.
            Native.Rect rect;
            if (Native.IsIconic(hwnd))
            {
                var placement = new Native.WindowPlacement { Length = Marshal.SizeOf<Native.WindowPlacement>() };
                if (!Native.GetWindowPlacement(hwnd, ref placement))
                {
                    return true;
                }

                rect = placement.NormalPosition;
            }
            else
            {
                Native.GetWindowRect(hwnd, out rect);
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 50 || height < 50)
            {
                return true;
            }

            windows.Add(new CaptureSource(CaptureSourceKind.Window, hwnd, title.ToString(), ProcessName(processId), width, height, processId));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public static bool IsAlive(CaptureSource source) =>
        source.Kind == CaptureSourceKind.Screen || Native.IsWindow(source.Handle);

    private static string ProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "";
        }
    }

    private static class Native
    {
        public const int MonitorInfoPrimary = 1;
        public const int GwlExStyle = -20;
        public const long WsExToolWindow = 0x80;
        public const long WsExAppWindow = 0x40000;
        public const uint GwOwner = 4;
        public const int DwmwaCloaked = 14;

        public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        public delegate bool WindowEnumProc(IntPtr hwnd, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WindowPlacement
        {
            public int Length;
            public int Flags;
            public int ShowCmd;
            public int MinPositionX;
            public int MinPositionY;
            public int MaxPositionX;
            public int MaxPositionY;
            public Rect NormalPosition;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MonitorInfoEx
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public int Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string Device;
        }

        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(WindowEnumProc callback, IntPtr data);

        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement placement);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    }
}
