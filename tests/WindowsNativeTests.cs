using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using Leaf;

public static class WindowsNativeTests
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    private static int assertions;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAILED: " + label);
        assertions++; Console.WriteLine("PASS " + label);
    }
    public static int Run(string folder)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Ui.InitializeTheme();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
        try {
            var task = Scenarios(folder);
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
            timer.Tick += (s, e) => frame.Continue = false;
            timer.Start();
            task.ContinueWith(t => application.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
            Dispatcher.PushFrame(frame); timer.Stop();
            if (!task.IsCompleted) throw new Exception("Native integration checks timed out.");
            task.GetAwaiter().GetResult();
            Console.WriteLine("SUCCESS: " + assertions + " native assertions"); return 0;
        } finally { application.Shutdown(); }
    }
    private static async Task Scenarios(string folder)
    {
        var store = new LocalStore(folder);
        var settings = Settings.Defaults();
        string prefix = "regression-" + Guid.NewGuid().ToString("N") + "-";
        foreach (var provider in settings.Providers) provider.Id = prefix + provider.Id;
        settings.ProviderId = settings.Providers[0].Id;
        store.SaveSettings(settings);
        string sampleKey = "Leaf fake credential fixture 中文";
        try {
            using (var transport = new FixtureTransport())
            using (var shell = new AppShell(store, true, new LlmClient(transport))) {
                var window = new SettingsWindow(shell);
                Ui.Get<ComboBox>(window.Window, "ProviderCombo").SelectedIndex = 2;
                Ui.Get<ComboBox>(window.Window, "ModelInput").Text = "fixture-model";
                Ui.Get<PasswordBox>(window.Window, "ApiKeyInput").Password = sampleKey;
                Ui.Get<TextBox>(window.Window, "ShortcutInput").Text = "alt + space";
                Ui.Get<Button>(window.Window, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                string chosenId = settings.Providers[2].Id;
                Check(store.Settings.ProviderId == chosenId, "The chosen provider is saved with its API key");
                Check(Credentials.Read(chosenId) == sampleKey && Credentials.Read(settings.Providers[0].Id) == "", "Credential Manager retains a Unicode key under the selected provider");
                Check(!File.ReadAllText(Path.Combine(folder, "settings.json")).Contains(sampleKey), "Native credential saving never writes the key into local settings");
                Check(Ui.Get<PasswordBox>(window.Window, "ApiKeyInput").Password.Length == 0 &&
                    Ui.Get<TextBlock>(window.Window, "KeyPlaceholder").Text.Replace(" ", "") == "●●●●●●●●" &&
                    Ui.Get<TextBlock>(window.Window, "KeyHint").Text.Contains("已保存密钥"), "Applying a key clears the editor, shows only password dots and places its saved status below");
                window.Window.Close();
                window = new SettingsWindow(shell);
                Check(((ProviderProfile)Ui.Get<ComboBox>(window.Window, "ProviderCombo").SelectedItem).Id == chosenId &&
                    Ui.Get<TextBlock>(window.Window, "KeyHint").Text.Contains("已保存密钥"), "Reopened settings recognize the saved key for the active provider");
                var keyEditor = Ui.Get<PasswordBox>(window.Window, "ApiKeyInput");
                var mask = Ui.Get<TextBlock>(window.Window, "KeyPlaceholder");
                Check(keyEditor.Password.Length == 0 && keyEditor.PasswordChar == '●' && mask.Visibility == Visibility.Visible,
                    "Reopened saved credentials are represented by a visible separate dot mask, never filled back into the password editor");
                keyEditor.Password = "replacement-fixture";
                Check(mask.Visibility == Visibility.Collapsed && Credentials.Read(chosenId) == sampleKey,
                    "Typing a replacement hides the mask and keeps the saved key until explicitly applied");
                keyEditor.Clear();
                Check(mask.Visibility == Visibility.Visible && !mask.Text.Contains("已保存") && mask.Text.Replace(" ", "") == "●●●●●●●●",
                    "Clearing an uncommitted replacement restores a dots-only placeholder");
                Ui.Get<Button>(window.Window, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Credentials.Read(chosenId) == sampleKey, "Saving with a blank password preserves the existing credential");
                Ui.Get<ComboBox>(window.Window, "ModelInput").Text = "fixture-model-2"; await Task.Delay(650);
                Check(store.Settings.Provider.Model == "fixture-model-2" && Credentials.Read(chosenId) == sampleKey,
                    "Model auto-apply preserves the saved native key");
                window.Window.Close();

                window = new SettingsWindow(shell); window.Window.Show(); window.Window.Activate();
                var recorder = Ui.Get<TextBox>(window.Window, "ShortcutInput");
                recorder.Focus(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var registrationField = typeof(AppShell).GetField("shortcutRegistration", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(recorder.IsKeyboardFocused && recorder.IsReadOnly && recorder.Text.Contains("请按下") && registrationField.GetValue(shell) == null,
                    "Focusing the recorder releases the live shortcut so it cannot consume recorded keys");
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window.Window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                recorder.RaiseEvent(escape); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(escape.Handled && recorder.Text == "alt + space" && registrationField.GetValue(shell) != null,
                    "Escape cancels WPF key recording and restores the existing shortcut");
                recorder.Focus(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.Window.Close();
                Check(registrationField.GetValue(shell) != null, "Closing settings during recording restores the global shortcut");

                var registration = typeof(AppShell).GetField("shortcutRegistration", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shell);
                var hook = typeof(GlobalShortcut).GetField("hook", BindingFlags.Instance | BindingFlags.NonPublic);
                Check((IntPtr)hook.GetValue(registration) != IntPtr.Zero, "Alt+Space installs a Windows keyboard hook");
                var source = HwndSource.FromHwnd(new WindowInteropHelper(shell.Popup).Handle);
                int messages = 0;
                HwndSourceHook observer = delegate(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam, ref bool handled) {
                    if (msg == 0x0312) messages++; return IntPtr.Zero;
                };
                source.AddHook(observer);
                IntPtr data = Marshal.AllocHGlobal(32);
                try {
                    for (int i = 0; i < 32; i++) Marshal.WriteByte(data, i, 0);
                    Marshal.WriteInt32(data, 0, 32); Marshal.WriteInt32(data, 8, 0x20);
                    var callback = typeof(GlobalShortcut).GetMethod("KeyboardHook", BindingFlags.Instance | BindingFlags.NonPublic);
                    var first = (IntPtr)callback.Invoke(registration, new object[] { 0, new IntPtr(0x0104), data });
                    var repeated = (IntPtr)callback.Invoke(registration, new object[] { 0, new IntPtr(0x0104), data });
                    Marshal.WriteInt32(data, 8, 0x80);
                    var released = (IntPtr)callback.Invoke(registration, new object[] { 0, new IntPtr(0x0105), data });
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(first == new IntPtr(1) && repeated == new IntPtr(1) && released == new IntPtr(1) && messages == 1,
                        "Alt+Space suppresses the system-menu key and emits one command per press without injecting desktop input");
                } finally { Marshal.FreeHGlobal(data); source.RemoveHook(observer); }

                shell.PopulateDemo();
                IntPtr foreground = Native.GetForegroundWindow();
                shell.ShowPopup();
                Check(Native.GetForegroundWindow() == foreground, "Showing the taskbar-visible popup keeps focus in the previous application");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                shell.Popup.WindowState = WindowState.Minimized; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                foreground = Native.GetForegroundWindow();
                shell.ShowPopup(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(!Native.IsIconic(new WindowInteropHelper(shell.Popup).Handle) && Native.GetForegroundWindow() == foreground,
                    "Re-showing a minimized popup restores it without stealing foreground focus");
                IntPtr popupHandle = new WindowInteropHelper(shell.Popup).Handle;
                Native.ShowWindow(popupHandle, 0); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                shell.ShowPopup(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(Native.IsWindowVisible(popupHandle) && Native.GetForegroundWindow() == foreground,
                    "Re-showing restores an OS-hidden window even when WPF visibility is stale");
                shell.BeginSourceEdit(true); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(shell.Popup.IsActive && Ui.Get<TextBox>(shell.Popup, "SourceInput").IsKeyboardFocused &&
                    Ui.Get<TextBox>(shell.Popup, "SourceInput").SelectedText == shell.Current.Source,
                    "Explicit direct input focuses the editor and selects the original for replacement");
                if (!OpenClipboard(new WindowInteropHelper(shell.Popup).Handle)) throw new Exception("Could not reserve clipboard for the isolated contention check.");
                try {
                    var copy = Native.CopyTextAsync("Clipboard contention fixture");
                    bool responsive = false;
                    shell.Popup.Dispatcher.BeginInvoke(new Action(() => responsive = true));
                    bool failed = false; try { await copy; } catch (UserError error) { failed = error.Code == "clipboard"; }
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(failed && responsive, "Clipboard contention reports an error while the UI dispatcher remains responsive");
                } finally { CloseClipboard(); }
                shell.Popup.Width = 520; shell.Popup.Height = 570;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Native.Rect original;
                Native.GetWindowRect(new WindowInteropHelper(shell.Popup).Handle, out original);
                Native.SetWindowPos(new WindowInteropHelper(shell.Popup).Handle, new IntPtr(-1), original.Left - 60, original.Top + 12, 0, 0, 0x0011);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                shell.HidePopup();
                var placement = new LocalStore(folder).Settings.Placement;
                Check(Math.Abs(placement.Width - 520) < 1 && Math.Abs(placement.Height - 570) < 1, "A native resize persists the actual popup dimensions");
                shell.ShowPopup();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Native.Rect reopened; Native.GetWindowRect(new WindowInteropHelper(shell.Popup).Handle, out reopened);
                Check(Math.Abs(reopened.Left - placement.X) <= 1 && Math.Abs(reopened.Top - placement.Y) <= 1, "The popup reopens at its remembered position");
                Task retry = null;
                shell.ShowError("Fixture retry", () => retry = shell.TranslateAsync("Native regression sentence", "剪贴板", true));
                Ui.Get<Button>(shell.Popup, "RetryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await retry;
                Native.Rect after; Native.GetWindowRect(new WindowInteropHelper(shell.Popup).Handle, out after);
                Check(after.Left == reopened.Left && after.Top == reopened.Top && after.Right == reopened.Right && after.Bottom == reopened.Bottom,
                    "Retry keeps the visible popup's position and size");
                Check(transport.KeyMatched && shell.Current.Completed, "A translation request reads the key saved for the selected provider");
                shell.HidePopup();
                shell.Dispose();
                Check((IntPtr)hook.GetValue(registration) == IntPtr.Zero, "Exiting releases the Alt+Space keyboard hook");
            }
            using (var shell = new AppShell(new LocalStore(folder), true)) {
                var saved = shell.Store.Settings.Placement;
                shell.ShowPopup(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Native.Rect rect; Native.GetWindowRect(new WindowInteropHelper(shell.Popup).Handle, out rect);
                Check(Math.Abs(rect.Left - saved.X) <= 1 && Math.Abs(rect.Top - saved.Y) <= 1 &&
                    Math.Abs(shell.Popup.Width - saved.Width) < 1 && Math.Abs(shell.Popup.Height - saved.Height) < 1,
                    "A new application instance restores both position and size");
                shell.HidePopup();
            }
        } finally {
            foreach (var provider in settings.Providers) Credentials.Delete(provider.Id);
        }
    }
    private sealed class FixtureTransport : HttpMessageHandler
    {
        public bool KeyMatched;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            KeyMatched = request.Headers.Authorization.Parameter == "Leaf fake credential fixture 中文";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"测试译文\"},\"finish_reason\":\"stop\"}]}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
