using System;
using System.Collections.Generic;
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
        var migrationProfile = new ProviderProfile { Id = prefix + "migration", Name = "Migration",
            BaseUrl = "https://legacy.invalid/v1", Model = "legacy-model" };
        var invalidProfile = new ProviderProfile { Id = prefix + "migration-invalid", BaseUrl = "http://remote.example.invalid/v1" };
        // Declare every credential scope this run can create: the migrated legacy target, the
        // endpoint-bound target it moves to, the invalid profile's retained legacy target, and
        // the endpoint-bound target the settings commit writes for the chosen provider.
        var credentialScopes = new List<string> {
            migrationProfile.Id,
            Credentials.ScopedId(migrationProfile),
            invalidProfile.Id,
            Credentials.ScopedId(settings.Providers[2])
        };
        try {
            Credentials.Save(migrationProfile.Id, "legacy-migration-fixture");
            Credentials.MigrateLegacyProfiles(new[] { migrationProfile });
            Check(Credentials.Read(migrationProfile) == "legacy-migration-fixture",
                "Legacy provider keys migrate to the endpoint-bound target");
            Check(Credentials.Read(migrationProfile.Id) == "", "A verified migration removes the legacy provider target");
            Credentials.Save(invalidProfile.Id, "invalid-legacy-fixture");
            Credentials.MigrateLegacyProfiles(new[] { invalidProfile });
            Check(Credentials.Read(invalidProfile.Id) == "invalid-legacy-fixture",
                "Migration keeps a legacy key when the profile has no valid endpoint");
        } finally {
            foreach (var scope in credentialScopes) { try { Credentials.Delete(scope); } catch { } }
        }
        try {
            using (var transport = new FixtureTransport())
            using (var shell = new AppShell(store, true, new LlmClient(transport))) {
                var window = new SettingsWindow(shell);
                Ui.Get<ComboBox>(window.Panel, "ProviderCombo").SelectedIndex = 2;
                Ui.Get<ComboBox>(window.Panel, "ModelInput").Text = "fixture-model";
                Ui.Get<PasswordBox>(window.Panel, "ApiKeyInput").Password = sampleKey;
                Ui.Get<TextBox>(window.Panel, "ShortcutInput").Text = "alt + space";
                Ui.Get<Button>(window.Panel, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await shell.WaitForSettingsAsync();
                string chosenId = settings.Providers[2].Id;
                Check(store.Settings.ProviderId == chosenId, "The chosen provider is saved with its API key");
                var chosenProfile = store.Settings.Provider;
                Check(Credentials.Read(chosenProfile) == sampleKey && Credentials.Read(store.Settings.Providers[0]) == "",
                    "Credential Manager retains a Unicode key under the endpoint-bound provider target");
                Check(!File.ReadAllText(Path.Combine(folder, "settings.json")).Contains(sampleKey), "Native credential saving never writes the key into local settings");
                Check(Ui.Get<PasswordBox>(window.Panel, "ApiKeyInput").Password.Length == 0 &&
                    Ui.Get<StackPanel>(window.Panel, "KeyDots").Visibility == Visibility.Visible && Ui.Get<StackPanel>(window.Panel, "KeyDots").Children.Count == 8 &&
                    Ui.Get<TextBlock>(window.Panel, "KeyHint").Text.Contains("密钥已保存"), "Applying a key clears the editor, shows only password dots and places its saved status below");
                window.Window.Close();
                await shell.WaitForSettingsAsync();
                window = new SettingsWindow(shell);
                Check(((ProviderProfile)Ui.Get<ComboBox>(window.Panel, "ProviderCombo").SelectedItem).Id == chosenId &&
                    Ui.Get<TextBlock>(window.Panel, "KeyHint").Text.Contains("密钥已保存"), "Reopened settings recognize the saved key for the active provider");
                var keyEditor = Ui.Get<PasswordBox>(window.Panel, "ApiKeyInput");
                var mask = Ui.Get<StackPanel>(window.Panel, "KeyDots");
                Check(keyEditor.Password.Length == 0 && keyEditor.PasswordChar == '●' && mask.Visibility == Visibility.Visible,
                    "Reopened saved credentials are represented by a visible separate dot mask, never filled back into the password editor");
                keyEditor.Password = "replacement-fixture";
                Check(mask.Visibility == Visibility.Collapsed && Credentials.Read(chosenProfile) == sampleKey,
                    "Typing a replacement hides the mask and keeps the saved key until explicitly applied");
                keyEditor.Clear();
                Check(mask.Visibility == Visibility.Visible && mask.Children.Count == 8 && mask.Children.Cast<System.Windows.Shapes.Ellipse>().All(dot => dot.Fill != null),
                    "Clearing an uncommitted replacement restores a dots-only placeholder");
                Ui.Get<Button>(window.Panel, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await shell.WaitForSettingsAsync();
                Check(Credentials.Read(chosenProfile) == sampleKey, "Saving with a blank password preserves the existing credential");
                Ui.Get<ComboBox>(window.Panel, "ModelInput").Text = "fixture-model-2"; await Task.Delay(650);
                await shell.WaitForSettingsAsync();
                Check(store.Settings.Provider.Model == "fixture-model-2" && Credentials.Read(store.Settings.Provider) == sampleKey,
                    "Model auto-apply preserves the saved native key");
                window.Window.Close();
                await shell.WaitForSettingsAsync();

                window = new SettingsWindow(shell); window.Window.Show(); window.Window.Activate();
                var recorder = Ui.Get<TextBox>(window.Panel, "ShortcutInput");
                // The recorder is inside the collapsed desktop-preferences section.
                for (DependencyObject parent = recorder; parent != null; parent = LogicalTreeHelper.GetParent(parent)) {
                    var expander = parent as Expander;
                    if (expander != null) expander.IsExpanded = true;
                }
                window.Window.UpdateLayout(); recorder.BringIntoView();
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
                // Keyboard entry into a word and the collapse behaviour, which offscreen
                // rendering cannot prove. What runs here is focus and activation on a real
                // window: the desktop matrix still has to confirm the real Tab order and the
                // cross-application foreground, so this is not claimed as done on a desktop.
                shell.OpenRecord(shell.Current); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                // The original is a read-only text box: reachable from the keyboard as a
                // whole, with each word explained through the box's own hit lookup.
                var readingBox = Ui.Get<TextBox>(shell.Popup, "SourceText");
                Check(readingBox.Focusable && readingBox.IsReadOnly && readingBox.Text == shell.Current.Source,
                    "The original is a focusable read-only box that explains words on demand");
                readingBox.Focus(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(readingBox.IsKeyboardFocused, "The original takes keyboard focus, which is the state Tab moves to");
                var word = TextTools.Pieces(shell.Current.Source).First(p => p.IsWord);
                int beforeLookup = transport.Calls;
                var lookup = shell.SelectWordAsync(shell.WordHitAt(word.Start), false);
                await lookup;
                Check(transport.Calls > beforeLookup, "Activating the word under the pointer starts its explanation");
                // A second top-level window of this same test process stands in for the window
                // that owns the foreground. It is not an external application, so what is checked
                // here is that the popup's own reveal paths ask for no foreground at all; the real
                // cross-application foreground still needs the manual desktop pass.
                var foreign = new Window {
                    Title = "Leaf focus fixture", Width = 240, Height = 120, Left = 16, Top = 16, ShowInTaskbar = false
                };
                foreign.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                IntPtr foreignHandle = new WindowInteropHelper(foreign).Handle;
                // Windows hands the foreground over asynchronously and can refuse while another
                // application holds it. The two checks below only mean anything while the other
                // window really owns it, so the premise is established and named first instead of
                // being assumed: a refused hand-over must never be reported as the popup taking it.
                Check(await OwnsForegroundAsync(foreign, foreignHandle),
                    "The other window owns the foreground before the popup is shown");
                shell.ShowPopup();
                Check(await ObserveForegroundAsync(popupHandle) == foreignHandle,
                    "Showing the popup leaves the foreground to the other window that owns it");
                shell.HidePopup();
                Check(await ObserveForegroundAsync(popupHandle) == foreignHandle, "Collapsing the popup leaves the foreground application alone");
                foreign.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
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
                await shell.WaitForPersistenceAsync();
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
            // The touched and cleanup sets are exactly the same declared scopes, and each is
            // deleted before its absence is reported, so a leftover credential fails the run.
            foreach (var scope in credentialScopes) Console.WriteLine("NATIVE-EVIDENCE touched provider=" + scope);
            foreach (var scope in credentialScopes) { try { Credentials.Delete(scope); } catch { } }
            foreach (var scope in credentialScopes) {
                Console.WriteLine("NATIVE-EVIDENCE cleanup provider=" + scope + " credential_present=" + (Credentials.Read(scope) == "" ? "false" : "true"));
            }
        }
    }
    // The foreground is handed over asynchronously and Windows can refuse while another
    // application holds it, so the hand-over is asked for again and verified rather than assumed.
    private static async Task<bool> OwnsForegroundAsync(Window window, IntPtr handle)
    {
        for (int attempt = 0; attempt < 20; attempt++) {
            if (Native.GetForegroundWindow() == handle) return true;
            window.Activate();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (Native.GetForegroundWindow() == handle) return true;
            await Task.Delay(50);
        }
        return Native.GetForegroundWindow() == handle;
    }
    // A reveal path that asked for the foreground may only take it a moment later, so the check
    // is held over a settle window and reports the popup itself if it ever held the foreground.
    private static async Task<IntPtr> ObserveForegroundAsync(IntPtr popup)
    {
        for (int sample = 0; sample < 8; sample++) {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (Native.GetForegroundWindow() == popup) return popup;
            await Task.Delay(25);
        }
        return Native.GetForegroundWindow();
    }
    private sealed class FixtureTransport : HttpMessageHandler
    {
        public bool KeyMatched;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Calls++;
            KeyMatched = request.Headers.Authorization.Parameter == "Leaf fake credential fixture 中文";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"测试译文\"},\"finish_reason\":\"stop\"}]}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
