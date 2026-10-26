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
}
