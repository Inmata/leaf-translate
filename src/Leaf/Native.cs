using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Leaf
{
    public sealed class HotkeySpec
    {
        public uint Modifiers; public uint VirtualKey;
        public static HotkeySpec Parse(string text)
        {
            var parts = (text ?? "").Split('+').Select(x => x.Trim()).ToArray();
            if (parts.Length < 2) throw new UserError("shortcut", "快捷键需包含 Ctrl、Alt 或 Win 与一个按键，例如 Ctrl+Alt+T。");
            uint modifiers = 0;
            for (int i = 0; i < parts.Length - 1; i++) {
                switch (parts[i].ToLowerInvariant()) {
                    case "alt": modifiers |= 1; break;
                    case "ctrl": case "control": modifiers |= 2; break;
                    case "shift": modifiers |= 4; break;
                    case "win": case "windows": modifiers |= 8; break;
                    default: throw new UserError("shortcut", "无法识别快捷键，请使用例如 Ctrl+Alt+T 的形式。");
                }
            }
            Key key;
            if ((modifiers & 11) == 0 || !Enum.TryParse<Key>(parts.Last(), true, out key) || key == Key.None)
                throw new UserError("shortcut", "快捷键需包含 Ctrl、Alt 或 Win 与一个有效按键。");
            int virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey == 0 || virtualKey == 16 || virtualKey == 17 || virtualKey == 18 || virtualKey == 91 || virtualKey == 92)
                throw new UserError("shortcut", "快捷键最后一项需为字母、数字或功能键。");
            return new HotkeySpec { Modifiers = modifiers | 0x4000, VirtualKey = (uint)virtualKey };
        }
    }
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public IntPtr Extra; }
        public delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string name, string title);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
        private static int automationRunning;

        public static string ClipboardText()
        {
            try {
                if (!Clipboard.ContainsText()) throw new UserError("clipboard", "剪贴板里没有文字。请先复制文字，再按快捷键。");
                return TextTools.ValidateInput(Clipboard.GetText());
            } catch (UserError) { throw; }
            catch { throw new UserError("clipboard", "剪贴板正被其他程序使用。请稍候再试。"); }
        }

        public static async Task<string> SelectedTextAsync()
        {
            IntPtr foreground = GetForegroundWindow();
            var automation = System.Threading.Interlocked.CompareExchange(ref automationRunning, 1, 0) == 0 ? Task.Run(() => {
                try {
                    var focused = AutomationElement.FocusedElement; object pattern;
                    if (focused != null && focused.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) {
                        return string.Join("", ((TextPattern)pattern).GetSelection().Select(range => range.GetText(6001)));
                    }
                } catch { }
                finally { System.Threading.Interlocked.Exchange(ref automationRunning, 0); }
                return "";
            }) : Task.FromResult("");
            if (await Task.WhenAny(automation, Task.Delay(250)) == automation) {
                string selected = await automation;
                if (!string.IsNullOrWhiteSpace(selected) && GetForegroundWindow() == foreground)
                    return TextTools.ValidateInput(selected);
            }
            for (int i = 0; i < 30 && ModifiersHeld(); i++) await Task.Delay(20);
            if (ModifiersHeld() || GetForegroundWindow() != foreground)
                throw new UserError("selection", "没有取得选中文字。请松开快捷键重试，或切到剪贴板模式。");
            IDataObject previous;
            try { previous = Clipboard.GetDataObject(); }
            catch { throw new UserError("clipboard", "剪贴板暂时不可用。请稍候再试。"); }
            uint before = GetClipboardSequenceNumber();
            var inputs = new[] { KeyInput(17, false), KeyInput(67, false), KeyInput(67, true), KeyInput(17, true) };
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) != inputs.Length)
                throw new UserError("selection", "当前程序不允许读取选区。请复制文字后使用剪贴板模式。");
            uint copied = before; string text = "";
            try {
                for (int i = 0; i < 35; i++) {
                    await Task.Delay(20);
                    copied = GetClipboardSequenceNumber();
                    if (copied == before) continue;
                    if (GetForegroundWindow() != foreground) break;
                    try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
                    if (!string.IsNullOrWhiteSpace(text)) break;
                }
            } finally {
                // Never overwrite a newer copy performed by the user or a different foreground app.
                if (copied != before && GetClipboardSequenceNumber() == copied && GetForegroundWindow() == foreground) {
                    try { if (previous == null) Clipboard.Clear(); else Clipboard.SetDataObject(previous, true); } catch { }
                }
            }
            if (string.IsNullOrWhiteSpace(text)) throw new UserError("selection", "未取得选中文字。当前软件可能不支持选区复制，请试试剪贴板模式。");
            return TextTools.ValidateInput(text);
        }
        private static bool ModifiersHeld() { return new[] { 16, 17, 18, 91, 92 }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0); }
        private static Input KeyInput(ushort key, bool up)
        {
            return new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u } } };
        }
        public static Forms.Screen CurrentScreen(Settings settings)
        {
            var configured = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == settings.Monitor);
            if (configured != null) return configured;
            Point point; GetCursorPos(out point); return Forms.Screen.FromPoint(new System.Drawing.Point(point.X, point.Y));
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
            var screen = CurrentScreen(settings); var area = screen.WorkingArea;
            double scale = Scale(screen); Point mouse; GetCursorPos(out mouse);
            int width = (int)(window.Width * scale), height = (int)(window.Height * scale);
            window.MaxHeight = Math.Max(300, area.Height / scale - 24);
            window.MaxWidth = Math.Max(340, area.Width / scale - 24);
            PointSetting saved;
            int x = mouse.X + 22, y = mouse.Y + 22;
            if (settings.Positions.TryGetValue(screen.DeviceName, out saved)) { x = (int)saved.X; y = (int)saved.Y; }
            else {
                if (x + width > area.Right) x = mouse.X - width - 22;
                if (y + height > area.Bottom) y = mouse.Y - height - 22;
            }
            x = Math.Max(area.Left + 12, Math.Min(x, area.Right - width - 12));
            y = Math.Max(area.Top + 12, Math.Min(y, area.Bottom - height - 12));
            SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-1), x, y, 0, 0, 0x0011);
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
    public sealed class OutsideClick : IDisposable
    {
        private IntPtr hook;
        private readonly Native.HookProc callback;
        public OutsideClick(Action outside)
        {
            uint own = (uint)Process.GetCurrentProcess().Id;
            callback = (code, message, data) => {
                if (code >= 0 && (message.ToInt32() == 0x0201 || message.ToInt32() == 0x0204 || message.ToInt32() == 0x0207 || message.ToInt32() == 0x020B)) {
                    var point = (Native.Point)Marshal.PtrToStructure(data, typeof(Native.Point));
                    uint process; Native.GetWindowThreadProcessId(Native.WindowFromPoint(point), out process);
                    if (process != own) outside();
                }
                return Native.CallNextHookEx(hook, code, message, data);
            };
        }
        public void Enable()
        {
            if (hook == IntPtr.Zero) hook = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(null), 0);
        }
        public void Dispose() { if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } }
    }
}
