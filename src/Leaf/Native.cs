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
        public static bool IsModifier(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin;
        }
        public static string Record(Key key, ModifierKeys modifiers)
        {
            if (IsModifier(key) || key == Key.System || key == Key.ImeProcessed || key == Key.DeadCharProcessed)
                throw new UserError("shortcut", "继续按下字母、数字或功能键。");
            string combination = ((modifiers & ModifierKeys.Control) != 0 ? "Ctrl+" : "") +
                ((modifiers & ModifierKeys.Alt) != 0 ? "Alt+" : "") +
                ((modifiers & ModifierKeys.Shift) != 0 ? "Shift+" : "") +
                ((modifiers & ModifierKeys.Windows) != 0 ? "Win+" : "");
            string name = key >= Key.D0 && key <= Key.D9 ? ((int)key - (int)Key.D0).ToString() : key.ToString();
            combination += name; Parse(combination); return combination;
        }
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
            string keyName = parts.Last();
            if (keyName.Equals("spacebar", StringComparison.OrdinalIgnoreCase) || keyName == "空格" || keyName == "空格键") keyName = "Space";
            if (keyName.Length == 1 && char.IsDigit(keyName[0])) keyName = "D" + keyName;
            Key key;
            if ((modifiers & 11) == 0 || !Enum.TryParse<Key>(keyName, true, out key) || !Enum.IsDefined(typeof(Key), key) || key == Key.None)
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
    public static class PopupLayout
    {
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        public static WindowPlacement Fit(WindowPlacement saved, double left, double top, double areaWidth, double areaHeight, double scale, string screen)
        {
            if (!Finite(scale) || scale <= 0) scale = 1;
            double width = saved != null && Finite(saved.Width) && saved.Width > 0 ? saved.Width : 456;
            double height = saved != null && Finite(saved.Height) && saved.Height > 0 ? saved.Height : 620;
            width = Math.Min(Math.Max(360, width), Math.Max(1, (areaWidth - 24) / scale));
            height = Math.Min(Math.Max(380, height), Math.Max(1, (areaHeight - 24) / scale));
            bool restore = saved != null && saved.Screen == screen && Finite(saved.X) && Finite(saved.Y);
            double x = restore ? saved.X : left + areaWidth - width * scale - 32;
            double y = restore ? saved.Y : top + (areaHeight - height * scale) / 2;
            x = Math.Max(left + 12, Math.Min(x, left + areaWidth - width * scale - 12));
            y = Math.Max(top + 12, Math.Min(y, top + areaHeight - height * scale - 12));
            return new WindowPlacement { X = x, Y = y, Width = width, Height = height, Screen = screen };
        }
    }
    public sealed class GlobalShortcut : IDisposable
    {
        private readonly IntPtr window;
        private readonly int id;
        private IntPtr hook;
        private Native.HookProc callback;
        private bool registered, spaceDown;
        [StructLayout(LayoutKind.Sequential)] private struct KeyEvent
        {
            public uint Key, Scan, Flags, Time;
            public IntPtr Extra;
        }
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        public GlobalShortcut(IntPtr target, int identifier, HotkeySpec spec)
        {
            window = target; id = identifier;
            // Alt+Space is also a Windows system-menu shortcut. Capture this narrow
            // combination so it works without opening the foreground window's menu.
            if ((spec.Modifiers & 15) == 1 && spec.VirtualKey == 32) {
                callback = KeyboardHook;
                hook = Native.SetWindowsHookEx(13, callback, Native.GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero) throw new UserError("shortcut", "无法启用 Alt+Space，请检查系统权限或改用其他快捷键。");
            } else {
                registered = Native.RegisterHotKey(window, id, spec.Modifiers, spec.VirtualKey);
                if (!registered) throw new UserError("shortcut", "快捷键已被其他软件占用，请换一个组合。");
            }
        }
        private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0) {
                var key = (KeyEvent)Marshal.PtrToStructure(data, typeof(KeyEvent));
                int kind = message.ToInt32();
                if (key.Key == 32) {
                    bool down = kind == 0x0100 || kind == 0x0104;
                    bool up = kind == 0x0101 || kind == 0x0105;
                    bool alt = (key.Flags & 0x20) != 0;
                    bool extraModifier = new[] { 16, 17, 91, 92 }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0);
                    if (down && (spaceDown || (alt && !extraModifier))) {
                        if (!spaceDown) { spaceDown = true; Native.PostMessage(window, 0x0312, new IntPtr(id), IntPtr.Zero); }
                        return new IntPtr(1);
                    }
                    if (up && spaceDown) { spaceDown = false; return new IntPtr(1); }
                }
            }
            return Native.CallNextHookEx(hook, code, message, data);
        }
        public void Dispose()
        {
            if (registered) { Native.UnregisterHotKey(window, id); registered = false; }
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            spaceDown = false;
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
