using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Leaf
{
    public sealed partial class AppShell : IDisposable
    {
        public LocalStore Store { get; private set; }
        public LlmClient Client { get; private set; }
        public DiagnosticLog Log { get; private set; }
        public Window Popup { get; private set; }
        public bool NativeEnabled { get; private set; }
        internal Func<ProviderProfile, string> CredentialReader;
        private readonly ICredentialProfiles credentialProfiles;
        private readonly bool migrateLegacyCredentials;
        private bool credentialMigrationDone;
        private Task lastSettingsTask = Task.FromResult(0);
        private long currentHistoryEpoch;
        public TranslationRecord Current { get; private set; }
        private readonly RequestGate generation = new RequestGate();
        private readonly RequestGate wordGeneration = new RequestGate();
        private CancellationTokenSource mainCancellation, wordCancellation, chatCancellation;
        private bool translating, wordBusy, chatBusy, captureBusy, pinned, exiting, demo, disposed;
        public bool Pinned { get { return pinned; } }
        private IntPtr handle;
        private string registeredShortcut;
        private GlobalShortcut shortcutRegistration;
        private bool shortcutRecording;
        private DispatcherTimer placementTimer;
        private bool restoringPlacement, placementInitialized;
        private OutsideClick outsideClick;
        private Forms.NotifyIcon tray;
        private Forms.ToolStripMenuItem clipboardMenu;
        private SettingsWindow settingsWindow;
        private HistoryWindow historyWindow;
        private TextPiece selectedWord;
        private WordCard selectedCard;
        private Action retry;
        private const int HotkeyId = 0x4C46;

        public AppShell(LocalStore store, bool native) : this(store, native, new LlmClient(), null) { }
        public AppShell(LocalStore store, bool native, LlmClient client) : this(store, native, client, null) { }
        public AppShell(LocalStore store, bool native, LlmClient client, ICredentialProfiles credentials)
        {
            Store = store; NativeEnabled = native; Client = client;
            if (credentials != null) credentialProfiles = credentials;
            else if (native) { credentialProfiles = new WindowsCredentialProfiles(); migrateLegacyCredentials = true; }
            Log = client.Log ?? new DiagnosticLog(System.IO.Path.Combine(store.Folder, "logs")); Client.Log = Log;
            Popup = Ui.Load("Popup"); InitializePopup();
        }
        public void Start(bool background, bool demonstration)
        {
            demo = demonstration;
            EnsureCredentialMigration();
            handle = new WindowInteropHelper(Popup).EnsureHandle();
            HwndSource.FromHwnd(handle).AddHook(Messages);
            outsideClick = new OutsideClick(() => Popup.Dispatcher.BeginInvoke(new Action(() => {
                if (Popup.IsVisible && !pinned) HidePopup();
            })));
            BuildTray();
            try { RegisterShortcut(Store.Settings.Shortcut); }
            catch (UserError error) { Log.Event("shortcut_register_failed", error); ShowError(error.Message, OpenSettings); }
            if (demo) { PopulateDemo(); ShowPopup(); }
            else if (!background && (ReadCredential(Store.Settings.Provider).Length == 0 || string.IsNullOrWhiteSpace(Store.Settings.Provider.Model))) OpenSettings();
            else if (!background) ShowPopup();
            if (!string.IsNullOrEmpty(Store.Warning)) {
                tray.ShowBalloonTip(5000, "Leaf", Store.Warning, Forms.ToolTipIcon.Warning);
            }
        }
        private IntPtr Messages(IntPtr window, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (message == 0x0312 && wparam.ToInt32() == HotkeyId) { handled = true; if (!shortcutRecording) CaptureAndTranslate(); }
            if (message == 0x8001) { handled = true; ShowPopup(); }
            return IntPtr.Zero;
        }
        private void BuildTray()
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示浮窗", null, (s, e) => ShowPopup());
            clipboardMenu = new Forms.ToolStripMenuItem("剪贴板模式") { Checked = Store.Settings.ClipboardMode };
            clipboardMenu.Click += async (s, e) => {
                try {
                    var next = Json.Copy(Store.Settings); next.ClipboardMode = !next.ClipboardMode;
                    await Store.SaveSettingsAsync(SettingsUpdate.Preferences(next));
                    clipboardMenu.Checked = next.ClipboardMode;
                    tray.ShowBalloonTip(2500, "Leaf", next.ClipboardMode ? "快捷键读取剪贴板，请先复制文字。" : "快捷键尝试读取当前选中文字。", Forms.ToolTipIcon.Info);
                } catch (UserError error) { ShowError(error.Message, null); }
            };
            menu.Items.Add(clipboardMenu); menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("历史记录", null, (s, e) => OpenHistory());
            menu.Items.Add("设置", null, (s, e) => OpenSettings());
            menu.Items.Add("打开日志", null, (s, e) => {
                try {
                    System.IO.Directory.CreateDirectory(Log.Folder);
                    Process.Start(new ProcessStartInfo(Log.Folder) { UseShellExecute = true });
                } catch (Exception error) { Log.Event("logs_open_failed", error); ShowError("无法打开日志目录。", null); ShowPopup(); }
            });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, async (s, e) => await ExitAsync());
            tray = new Forms.NotifyIcon { Icon = TrayIcon(), Text = "Leaf · " + Store.Settings.Shortcut, ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (s, e) => ShowPopup();
        }
        private static Icon TrayIcon()
        {
            using (var stream = typeof(AppShell).Assembly.GetManifestResourceStream("Leaf.Assets.Leaf.ico"))
            using (var icon = new Icon(stream, 32, 32)) return (Icon)icon.Clone();
        }
        private void RegisterShortcut(string shortcut)
        {
            var specification = HotkeySpec.Parse(shortcut);
            if (!NativeEnabled) return;
            if (registeredShortcut == shortcut) return;
            string previous = registeredShortcut;
            if (handle == IntPtr.Zero) handle = new WindowInteropHelper(Popup).EnsureHandle();
            if (shortcutRegistration != null) { shortcutRegistration.Dispose(); shortcutRegistration = null; }
            try { shortcutRegistration = new GlobalShortcut(handle, HotkeyId, specification); }
            catch {
                registeredShortcut = null;
                if (previous != null) {
                    try { shortcutRegistration = new GlobalShortcut(handle, HotkeyId, HotkeySpec.Parse(previous)); registeredShortcut = previous; } catch { }
                }
                throw;
            }
            registeredShortcut = shortcut;
            Log.Event("shortcut_registered", null);
        }
        public void SetShortcutRecording(bool recording)
        {
            if (shortcutRecording == recording) return;
            shortcutRecording = recording;
            if (recording) {
                if (shortcutRegistration != null) { shortcutRegistration.Dispose(); shortcutRegistration = null; }
                registeredShortcut = null;
            } else if (!disposed && !exiting) {
                try { RegisterShortcut(Store.Settings.Shortcut); }
                catch (UserError error) { Log.Event("shortcut_restore_failed", error); ShowError(error.Message, OpenSettings); }
            }
        }
        private async void CaptureAndTranslate()
        {
            if (captureBusy || shortcutRecording) return;
            uint foregroundProcess; Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out foregroundProcess);
            if (foregroundProcess == (uint)Process.GetCurrentProcess().Id) {
                ShowPopup(); BeginSourceEdit(Store.Settings.FocusInputOnShortcut); return;
            }
            captureBusy = true;
            long revision = sourceRevision;
            string text = null, kind = Store.Settings.ClipboardMode ? "剪贴板" : "选中文字";
            try {
                // Start acquisition with the original foreground intact, then show immediately.
                var capture = Store.Settings.ClipboardMode ? Native.ClipboardTextAsync() : Native.SelectedTextAsync();
                ShowPopup();
                text = await capture;
            } catch (Exception error) {
                Log.Event("capture_failed", error);
                if (!(error is OperationCanceledException) && sourceRevision == revision) {
                    CancelRequests(); Current = null; DisplayRecord();
                    Ui.Get<TextBlock>(Popup, "SourceBadge").Text = Store.Settings.ClipboardMode ? "剪贴板模式" : "选中模式";
                    if (!(error is UserError) || ((UserError)error).Code != "selection" && ((UserError)error).Code != "clipboard")
                        ShowError(error is UserError ? error.Message : "没有取得文字，请重试。", CaptureAndTranslate);
                    Ui.Get<TextBlock>(Popup, "SourceEditHint").Text = "未取得文字，可直接输入 · Enter 翻译";
                    ShowPopup();
                    BeginSourceEdit(Store.Settings.FocusInputOnShortcut);
                }
            } finally { captureBusy = false; }
            if (sourceRevision != revision) return;
            if (!string.IsNullOrWhiteSpace(text)) {
                var translation = TranslateAsync(text, kind, false);
                if (Store.Settings.FocusInputOnShortcut) BeginSourceEdit(true);
                await translation;
            } else if (text != null) {
                CancelRequests(); Current = null; DisplayRecord();
                Ui.Get<TextBlock>(Popup, "SourceBadge").Text = "输入翻译";
                ShowPopup(); BeginSourceEdit(Store.Settings.FocusInputOnShortcut);
            }
        }
        public void ShowPopup()
        {
            if (exiting) return;
            if (NativeEnabled) {
                if (Popup.IsVisible) { Native.Reveal(Popup); return; }
                restoringPlacement = true;
                try {
                    Popup.ShowActivated = false; Native.Position(Popup, Store.Settings); Popup.Show(); Native.Reveal(Popup); placementInitialized = true;
                } finally { restoringPlacement = false; }
                RememberPlacement();
                if (outsideClick != null) outsideClick.Enable();
            }
        }
        private void QueuePlacementSave()
        {
            if (!NativeEnabled || restoringPlacement || !placementInitialized || !Popup.IsVisible || Popup.WindowState == WindowState.Minimized) return;
            if (placementTimer == null) {
                placementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                placementTimer.Tick += (s, e) => { placementTimer.Stop(); RememberPlacement(); };
            }
            placementTimer.Stop(); placementTimer.Start();
        }
        private void RememberPlacement()
        {
            if (placementTimer != null) placementTimer.Stop();
            if (!NativeEnabled || restoringPlacement || !placementInitialized || Popup.WindowState == WindowState.Minimized) return;
            var placement = Native.CapturePlacement(Popup);
            if (placement == null || placement.Width <= 0 || placement.Height <= 0 || Json.Write(placement) == Json.Write(Store.Settings.Placement)) return;
            SavePlacementObserved(placement);
        }
        private async void SavePlacementObserved(WindowPlacement placement)
        {
            try { await Store.SavePlacementAsync(placement); }
            catch (UserError error) { Log.Event("placement_save_failed", error); ShowError(error.Message, null); }
        }
        public void HidePopup()
        {
            sourceRevision++;
            RememberPlacement();
            if (Current != null) {
                Current.Draft = Ui.Get<TextBox>(Popup, "QuestionInput").Text;
                var pendingSave = SaveCurrentAsync();
            }
            if (outsideClick != null) outsideClick.Dispose();
            Popup.Hide();
        }
        public void OpenSettings()
        {
            EnsureCredentialMigration();
            HidePopup();
            if (settingsWindow != null) { settingsWindow.Window.Activate(); return; }
            settingsWindow = new SettingsWindow(this);
            settingsWindow.Window.Closed += (s, e) => settingsWindow = null;
            settingsWindow.Window.Show(); settingsWindow.Window.Activate();
        }
        public void OpenHistory()
        {
            HidePopup();
            if (historyWindow != null) { historyWindow.Refresh(); historyWindow.Window.Activate(); return; }
            historyWindow = new HistoryWindow(this);
            historyWindow.Window.Closed += (s, e) => historyWindow = null;
            historyWindow.Window.Show(); historyWindow.Window.Activate();
        }
        public Task ApplySettingsAsync(SettingsUpdate update, Dictionary<string, string> keys, HashSet<string> deleted)
        {
            if (update == null) throw new ArgumentNullException("update");
            EnsureCredentialMigration();
            var previous = Json.Copy(Store.Settings);
            var changedKeys = keys == null ? new Dictionary<string, string>() : new Dictionary<string, string>(keys);
            var removedKeys = deleted == null ? new HashSet<string>() : new HashSet<string>(deleted);
            Task task = ApplySettingsCoreAsync(update, changedKeys, removedKeys, previous);
            lastSettingsTask = task;
            return task;
        }
        public async Task WaitForSettingsAsync()
        {
            Task pending;
            do {
                pending = lastSettingsTask;
                if (pending != null) await pending.ConfigureAwait(true);
                await Store.FlushAsync().ConfigureAwait(true);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            } while (!ReferenceEquals(pending, lastSettingsTask));
        }
        private async Task ApplySettingsCoreAsync(SettingsUpdate update, Dictionary<string, string> keys, HashSet<string> deleted, Settings previous)
        {
            var keyUndo = new List<Action>();
            var replaced = new List<KeyValuePair<ProviderProfile, ProviderProfile>>();
            Action<Settings, Settings> applyExternal = (committed, candidate) => {
                keyUndo.Clear(); replaced.Clear();
                if (credentialProfiles != null) {
                    foreach (string id in keys.Keys.Concat(deleted).Distinct().ToArray()) {
                        var oldProfile = committed.Providers.FirstOrDefault(p => p.Id == id);
                        var newProfile = candidate.Providers.FirstOrDefault(p => p.Id == id);
                        string oldKey = ""; bool hadOld = false;
                        if (oldProfile != null) {
                            try { oldKey = credentialProfiles.Read(oldProfile); hadOld = oldKey.Length > 0; }
                            catch (Exception error) {
                                throw new UserError("credentials", error is UserError ? ((UserError)error).Message : "无法读取旧密钥，已取消本次配置。");
                            }
                        }
                        var capturedOld = oldProfile; var capturedNew = newProfile;
                        string capturedKey = oldKey; bool capturedHad = hadOld;
                        keyUndo.Add(() => {
                            bool same = false;
                            try { same = capturedOld != null && capturedNew != null && Credentials.ScopedId(capturedOld) == Credentials.ScopedId(capturedNew); } catch { }
                            if (!same && capturedNew != null) { try { credentialProfiles.Delete(capturedNew); } catch { } }
                            if (capturedOld != null) {
                                try { if (capturedHad) credentialProfiles.Save(capturedOld, capturedKey); else credentialProfiles.Delete(capturedOld); } catch { }
                            }
                        });
                        if (deleted.Contains(id) && newProfile != null) credentialProfiles.Delete(newProfile);
                        string pending;
                        if (newProfile != null && keys.TryGetValue(id, out pending)) credentialProfiles.Save(newProfile, pending);
                        if (oldProfile != null && newProfile != null) replaced.Add(new KeyValuePair<ProviderProfile, ProviderProfile>(oldProfile, newProfile));
                    }
                }
                if (NativeEnabled && candidate.AutoStart != committed.AutoStart)
                    OnDispatcher(() => Native.AutoStart(candidate.AutoStart));
                if (NativeEnabled && candidate.Shortcut != committed.Shortcut)
                    OnDispatcher(() => RegisterShortcut(candidate.Shortcut));
            };
            Action<Settings, Settings> rollbackExternal = (committed, candidate) => {
                for (int i = keyUndo.Count - 1; i >= 0; i--) { try { keyUndo[i](); } catch { } }
                if (NativeEnabled && candidate.AutoStart != committed.AutoStart) {
                    try { OnDispatcher(() => Native.AutoStart(committed.AutoStart)); } catch { }
                }
                if (NativeEnabled && candidate.Shortcut != committed.Shortcut) {
                    try { OnDispatcher(() => RegisterShortcut(committed.Shortcut)); } catch { }
                }
            };
            await Store.CommitSettingsAsync(update, applyExternal, rollbackExternal).ConfigureAwait(true);
            if (credentialProfiles != null) CleanupReplacedCredentials(replaced, deleted);
            if (Store.Settings.ProviderId != previous.ProviderId || Json.Write(Store.Settings.Provider) != Json.Write(previous.Provider) || keys.Count > 0 || deleted.Count > 0) CancelRequests();
            if (!Store.Settings.HistoryEnabled) Forget(null);
            if (clipboardMenu != null) { clipboardMenu.Checked = Store.Settings.ClipboardMode; tray.Text = "Leaf · " + Store.Settings.Shortcut; }
            if (historyWindow != null) historyWindow.Refresh();
            Log.Event("settings_saved", null);
        }
        private void CleanupReplacedCredentials(List<KeyValuePair<ProviderProfile, ProviderProfile>> replaced, HashSet<string> deleted)
        {
            foreach (var pair in replaced) {
                string oldScoped, newScoped;
                try { oldScoped = Credentials.ScopedId(pair.Key); newScoped = Credentials.ScopedId(pair.Value); } catch { continue; }
                if (oldScoped == newScoped) continue;
                string newKey = "";
                try { newKey = credentialProfiles.Read(pair.Value); } catch { }
                if (deleted.Contains(pair.Value.Id) || newKey.Length > 0) {
                    try { credentialProfiles.Delete(pair.Key); } catch { }
                }
            }
        }
        internal string ReadCredential(ProviderProfile profile)
        {
            if (profile == null) return "";
            if (CredentialReader != null) return CredentialReader(profile);
            return credentialProfiles != null ? credentialProfiles.Read(profile) : "";
        }
        private void EnsureCredentialMigration()
        {
            if (!migrateLegacyCredentials || credentialMigrationDone) return;
            credentialMigrationDone = true;
            try { Credentials.MigrateLegacyProfiles(Store.Settings.Providers); }
            catch (Exception error) { Log.Event("credential_migration_failed", error); }
        }
        private void OnDispatcher(Action action)
        {
            if (Popup.Dispatcher.CheckAccess()) action(); else Popup.Dispatcher.Invoke(action);
        }
        public void OpenRecord(TranslationRecord record)
        {
            CancelRequests(); demo = false; Current = record; currentHistoryEpoch = Store.HistoryEpoch;
            DisplayRecord(); ShowPopup();
        }
        public void Forget(string id)
        {
            if (Current != null && (id == null || Current.Id == id)) {
                CancelRequests(); Current = null; DisplayRecord();
            }
        }
        public async Task WaitForPersistenceAsync()
        {
            Task pending;
            do {
                pending = lastSettingsTask;
                if (pending != null) { try { await pending.ConfigureAwait(true); } catch { } }
                await Store.FlushAsync().ConfigureAwait(true);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            } while (!ReferenceEquals(pending, lastSettingsTask));
        }
        public async Task SaveCurrentAsync()
        {
            var record = Current;
            if (record == null || demo) return;
            try {
                await Store.SaveAsync(record, currentHistoryEpoch).ConfigureAwait(true);
                if (historyWindow != null) historyWindow.Refresh();
            } catch (UserError error) {
                Log.Event("history_save_failed", error);
                ShowError(error.Message, RetryPersistence);
            }
        }
        private async void RetryPersistence()
        {
            try {
                await Store.RetryFailedWritesAsync();
                await Store.FlushAsync();
                if (historyWindow != null) historyWindow.Refresh();
            } catch (UserError error) {
                Log.Event("history_save_failed", error);
                ShowError(error.Message, RetryPersistence);
            }
        }
        private void CancelRequests()
        {
            generation.Next(); wordGeneration.Next();
            if (mainCancellation != null) mainCancellation.Cancel();
            if (wordCancellation != null) wordCancellation.Cancel();
            if (chatCancellation != null) chatCancellation.Cancel();
            translating = wordBusy = chatBusy = false; Busy();
        }
        private void Busy()
        {
            Ui.Visible(Ui.Get<Grid>(Popup, "BusyPanel"), translating || wordBusy || chatBusy);
            Ui.Get<TextBlock>(Popup, "BusyLabel").Text = translating ? "正在翻译…" : wordBusy ? "正在查词…" : "正在回答…";
            Ui.Get<Button>(Popup, "SendButton").IsEnabled = Current != null && Current.Completed && !chatBusy;
            Ui.Get<Button>(Popup, "AskButton").IsEnabled = Current != null && Current.Completed;
            Ui.Visible(Ui.Get<Button>(Popup, "AskButton"), Current != null && Ui.Get<Grid>(Popup, "SourceEditor").Visibility != Visibility.Visible && Ui.Get<Border>(Popup, "InputPanel").Visibility != Visibility.Visible);
        }
        public void Exit() { var pendingExit = ExitAsync(); }
        public async Task ExitAsync()
        {
            if (exiting) return;
            exiting = true;
            RememberPlacement();
            if (Current != null && !demo) Current.Draft = Ui.Get<TextBox>(Popup, "QuestionInput").Text;
            try {
                await SaveCurrentAsync();
                await Store.FlushAsync();
            } catch (UserError error) {
                exiting = false;
                Log.Event("exit_flush_failed", error);
                ShowError(error.Message, RetryExit);
                return;
            }
            Dispose();
            if (Application.Current != null) Application.Current.Shutdown();
        }
        private async void RetryExit()
        {
            try {
                await Store.RetryFailedWritesAsync();
                await Store.FlushAsync();
                Exit();
            } catch (UserError error) {
                Log.Event("exit_flush_failed", error);
                ShowError(error.Message, RetryExit);
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            RememberPlacement();
            disposed = true;
            if (copyFeedback != null) copyFeedback.Stop();
            Log.Event("app_exit", null);
            if (Current != null && !demo) {
                Current.Draft = Ui.Get<TextBox>(Popup, "QuestionInput").Text;
                try { Store.Save(Current); } catch { }
            }
            CancelRequests();
            if (outsideClick != null) outsideClick.Dispose();
            if (shortcutRegistration != null) { shortcutRegistration.Dispose(); shortcutRegistration = null; registeredShortcut = null; }
            if (tray != null) {
                tray.Visible = false; var icon = tray.Icon; tray.Dispose(); tray = null;
                if (icon != null) icon.Dispose();
            }
            if (mainCancellation != null) { mainCancellation.Dispose(); mainCancellation = null; }
            if (wordCancellation != null) { wordCancellation.Dispose(); wordCancellation = null; }
            if (chatCancellation != null) { chatCancellation.Dispose(); chatCancellation = null; }
            Client.Dispose();
            exiting = true;
            if (settingsWindow != null) settingsWindow.Window.Close();
            if (historyWindow != null) historyWindow.Window.Close();
            Popup.Close();
        }
    }
}
