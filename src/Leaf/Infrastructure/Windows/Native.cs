using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Leaf
{
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] internal struct InputUnion {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public IntPtr Extra; }
        // The subset of GUITHREADINFO the copy worker needs: the window this thread's keyboard
        // focus is on. Read only, and never to look at what is written in it.
        [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo {
            public int Size; public uint Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public Rect CaretRect;
        }
        public delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out uint value, uint size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] internal static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] internal static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string name, string title);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(Point point, uint flags);
        public static bool IsDesktop(IntPtr window)
        {
            var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
            string kind = name.ToString();
            return window == IntPtr.Zero || kind == "Progman" || kind == "WorkerW" || kind == "Shell_TrayWnd" || kind == "Shell_SecondaryTrayWnd";
        }
        public static void Reveal(Window window)
        {
            IntPtr target = new WindowInteropHelper(window).EnsureHandle();
            // Re-show even when WPF IsVisible is still true after Explorer's Show Desktop.
            uint cloaked;
            if (DwmGetWindowAttribute(target, 14, out cloaked, 4) == 0 && cloaked != 0) ShowWindow(target, 0);
            ShowWindow(target, 4); // SW_SHOWNOACTIVATE restores without taking keyboard focus.
            SetWindowPos(target, new IntPtr(-1), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002 | 0x0040);
        }
        // The keyboard focus of the foreground thread: the window a simulated copy reaches.
        // Zero means Windows did not report one, which is unknown rather than "somewhere else".
        // Also the independent native witness the UIA probe reads for cross-process focus.
        public static IntPtr ForegroundFocusWindow()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return IntPtr.Zero;
            uint process; uint thread = GetWindowThreadProcessId(foreground, out process);
            if (thread == 0) return IntPtr.Zero;
            var info = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
            if (!GetGUIThreadInfo(thread, ref info)) return IntPtr.Zero;
            return info.Focus;
        }
        public static Forms.Screen SavedScreen(Settings settings)
        {
            var saved = settings.Placement;
            return Forms.Screen.AllScreens.FirstOrDefault(s => saved != null && s.DeviceName == saved.Screen) ?? Forms.Screen.PrimaryScreen;
        }
        public static double Scale(Forms.Screen screen)
        {
            try {
                uint x, y;
                if (GetDpiForMonitor(MonitorFromPoint(new Point { X = screen.Bounds.Left + 1, Y = screen.Bounds.Top + 1 }, 2), 0, out x, out y) == 0)
                    return x / 96.0;
            } catch { }
            return 1;
        }
        public static void Position(Window window, Settings settings)
        {
            var screen = SavedScreen(settings); var area = screen.WorkingArea;
            double scale = Scale(screen);
            var placement = PopupLayout.Fit(settings.Placement, area.Left, area.Top, area.Width, area.Height, scale, screen.DeviceName);
            window.MinWidth = Math.Min(360, Math.Max(1, (area.Width - 24) / scale));
            window.MinHeight = Math.Min(380, Math.Max(1, (area.Height - 24) / scale));
            window.MaxWidth = Math.Max(window.MinWidth, (area.Width - 24) / scale);
            window.MaxHeight = Math.Max(window.MinHeight, (area.Height - 24) / scale);
            window.Width = placement.Width; window.Height = placement.Height;
            SetWindowPos(new WindowInteropHelper(window).EnsureHandle(), new IntPtr(-1), (int)placement.X, (int)placement.Y,
                (int)Math.Round(placement.Width * scale), (int)Math.Round(placement.Height * scale), 0x0010);
        }
        public static WindowPlacement CapturePlacement(Window window)
        {
            Rect rect;
            if (!GetWindowRect(new WindowInteropHelper(window).Handle, out rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return null;
            var screen = Forms.Screen.FromRectangle(new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
            return new WindowPlacement { X = rect.Left, Y = rect.Top, Width = window.ActualWidth,
                Height = window.ActualHeight, Screen = screen.DeviceName };
        }
        public static void AutoStart(bool enabled)
        {
            try {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
                    if (enabled) key.SetValue("LeafTranslate", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" --background");
                    else key.DeleteValue("LeafTranslate", false);
                }
            } catch { throw new UserError("startup", "无法更新开机启动设置，请检查系统权限。"); }
        }
    }
}
