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
        public SelectionAcquirer Selection { get; set; }
        // Test seam: the in-window page read. Production reads the popup's own translate page;
        // a scripted reader lets the in-front branch be checked without a shown window.
        public IInternalSelectionReader InternalSelection { get; set; }
        // Test seam: supplies the placement a check wants to persist so the exit path can be
        // exercised without a real desktop. Null in production, where the native read is used.
        public Func<WindowPlacement> PlacementReader { get; set; }
        internal Func<ProviderProfile, string> CredentialReader;
        private readonly ICredentialProfiles credentialProfiles;
        private readonly bool migrateLegacyCredentials;
        private bool credentialMigrationDone;
        private Task lastSettingsTask = Task.FromResult(0);
        private long currentHistoryEpoch;
        public TranslationRecord Current { get; private set; }
        private readonly RequestGate generation = new RequestGate();
        private readonly RequestGate wordGeneration = new RequestGate();
        private CancellationTokenSource mainCancellation, wordCancellation, chatCancellation, captureCancellation;
        private bool translating, wordBusy, chatBusy, captureBusy, pinned, exiting, demo, disposed, flushedForExit;
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
        private RetryOperation activeTranslation, activeWord, activeFollowup;
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
            Selection = new SelectionAcquirer(new WindowsSelectionProbe());
            Popup = Ui.Load("Popup"); InitializePopup();
            InternalSelection = new InternalSelectionReader(Popup);
        }
        public void Start(bool background, bool demonstration)
        {
            demo = demonstration;
            EnsureCredentialMigration();
            handle = new WindowInteropHelper(Popup).EnsureHandle();
            HwndSource.FromHwnd(handle).AddHook(Messages);
            outsideClick = new OutsideClick(() => Popup.Dispatcher.BeginInvoke(new Action(() => {
                // A click outside is a real entry that leaves the settings page for the reading
                // page, so it takes the same leave; a failed flush keeps the page instead.
                if (Popup.IsVisible && !pinned) { var ignored = LeaveSettingsThenAsync(HidePopup); }
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
        private async void CaptureAndTranslate() { await InvokeShortcutAsync(); }
        // The shortcut is the only place a capture is started. Everything it needs is
        // snapshotted first, and no setting is re-read after the acquisition returns.
        public async Task InvokeShortcutAsync()
        {
            if (captureBusy || shortcutRecording || exiting) return;
            var target = Selection.Snapshot();
            var invocation = Json.Copy(Store.Settings);
            if (!invocation.ClipboardMode && target.IsLeaf) {
                // Leaf itself is in front, so the page the user is looking at is read before
                // anything is revealed or focused: a selection made inside the popup is its own
                // new session, while no page selection at all keeps the existing behavior.
                var page = InternalSelection != null ? InternalSelection.Read() : InternalSelectionResult.NoPage;
                Log.InternalSelection(page.Status, page.Reason, page.Source, page.TextLength);
                if (page.Status == InternalSelectionStatus.Text) {
                    // The reading page replaces whatever page is on screen, so an open settings
                    // page is left first; a failed flush keeps it and the current session.
                    if (!await TryLeaveSettingsPageAsync()) return;
                    await TranslateInternalSelectionAsync(page.Text);
                    return;
                }
                // The settings page is not the reading page: a shortcut there stays on the page
                // and leaves the hidden session - its card, follow-up panel, draft and editor -
                // exactly as it was, instead of restoring that page behind the settings.
                if (IsSettingsPageOpen) return;
                HandleLeafSelectionShortcut(invocation.FocusInputOnShortcut);
                return;
            }
            // An external capture runs while the original foreground is intact and reveals the
            // popup immediately. Only its result decides what happens to an open settings
            // page: a text result is shown on the reading page and leaves the page first, so
            // pending preferences are applied then; inconclusive outcomes never touch it.
            long revision = sourceRevision;
            var owner = new CancellationTokenSource();
            captureCancellation = owner; captureBusy = true;
            string captureId = Guid.NewGuid().ToString("N");
            SelectionCaptureResult result;
            try {
                // Start acquisition with the original foreground intact, then show immediately.
                var capture = Selection.CaptureAsync(target, invocation.ClipboardMode, invocation.Shortcut, owner.Token);
                ShowPopup();
                result = await capture;
            } finally {
                if (ReferenceEquals(captureCancellation, owner)) captureCancellation = null;
                captureBusy = false; owner.Dispose();
            }
            Log.Capture(CaptureTelemetry.For(captureId, target, invocation.ClipboardMode, invocation.Shortcut, result));
            if (sourceRevision != revision || exiting || result.Status == CaptureStatus.Cancelled) return;
            if (result.Status == CaptureStatus.Text) {
                // Only a real result needs the reading page, so only now is the settings page
                // left; a failed flush keeps that page and discards this result. The flush can
                // wait, so the session the capture started from is rechecked before it lands:
                // a revision change or an exit during that wait means this result is stale.
                if (!await TryLeaveSettingsPageAsync()) return;
                if (sourceRevision != revision || exiting) return;
                var translation = TranslateAsync(result.Text, invocation.ClipboardMode ? "剪贴板" : "选中文字", false);
                if (invocation.FocusInputOnShortcut) BeginSourceEdit(true);
                await translation;
                return;
            }
            if (result.Status == CaptureStatus.Failed) {
                if (!await TryLeaveSettingsPageAsync()) return;
                PrepareManualInput(result.Reason, invocation.ClipboardMode, invocation.FocusInputOnShortcut);
                ShowError(CaptureMessages.For(result.Reason), () => { var retried = InvokeShortcutAsync(); });
                return;
            }
            // Empty and Unavailable are inconclusive, not failures: the popup is revealed
            // quietly and whatever page, session, draft and editor state is open stays as it
            // is. The badge states what happened; manual input starts only through the
            // explicit edit entry, never automatically.
            MarkInconclusiveCapture(result.Reason, invocation.ClipboardMode);
            ShowPopup();
        }
        // With no selection of its own page to translate, a shortcut in front of Leaf has no
        // foreign selection either. An editor the user has not committed to stays untouched;
        // otherwise the current result comes back. With no result at all the empty state
        // stays as it is: the input is entered through the explicit edit control, never
        // opened automatically.
        private void HandleLeafSelectionShortcut(bool directInput)
        {
            if (Current == null) { MarkInconclusiveCapture(CaptureReason.None, false); ShowPopup(); return; }
            if (HasPendingDraft()) { ShowPopup(); return; }
            RestoreCurrentPresentation(false);
        }
        // A cleared editor is a draft action, not a new session; an unchanged prefill is not a draft.
        private bool HasPendingDraft()
        {
            if (Current == null) return false;
            var input = Ui.Get<TextBox>(Popup, "SourceInput");
            if (Ui.Get<Grid>(Popup, "SourceEditor").Visibility != Visibility.Visible) return false;
            if (input.Text.Length == 0) return false;
            return input.Text != Current.Source;
        }
        // States the capture outcome on the source badge without touching the page: the
        // session, its drafts and any open editor stay exactly as they are.
        private void MarkInconclusiveCapture(CaptureReason reason, bool clipboardMode)
        {
            bool inconclusive = reason != CaptureReason.Desktop && reason != CaptureReason.EmptySelection &&
                reason != CaptureReason.None;
            var badge = Ui.Get<TextBlock>(Popup, "SourceBadge");
            string mode = clipboardMode ? "剪贴板模式" : "选中模式";
            if (Current == null) badge.Text = inconclusive ? "未取得选区 · " + mode : mode;
            badge.ToolTip = inconclusive ? CaptureMessages.Hint(reason) : null;
        }
        // A definite failure still prepares the input surface it names; the specific reason
        // is reported by the caller.
        private void PrepareManualInput(CaptureReason reason, bool clipboardMode, bool directInput)
        {
            MarkInconclusiveCapture(reason, clipboardMode);
            ShowPopup();
            if (Ui.Get<Grid>(Popup, "SourceEditor").Visibility == Visibility.Visible &&
                Ui.Get<TextBox>(Popup, "SourceInput").Text.Length > 0) { Busy(); return; }
            BeginSourceEdit(directInput);
        }
        // Abandons an acquisition whose result the user has already made irrelevant.
        private void CancelCapture()
        {
            var owner = captureCancellation;
            if (owner == null) return;
            try { owner.Cancel(); } catch (ObjectDisposedException) { }
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
        private WindowPlacement CapturePlacementToSave()
        {
            if (restoringPlacement || Popup.WindowState == WindowState.Minimized) return null;
            WindowPlacement placement;
            if (PlacementReader != null) placement = PlacementReader();
            else {
                if (!NativeEnabled || !placementInitialized) return null;
                placement = Native.CapturePlacement(Popup);
            }
            if (placement == null || placement.Width <= 0 || placement.Height <= 0 || Json.Write(placement) == Json.Write(Store.Settings.Placement)) return null;
            return placement;
        }
        private void RememberPlacement()
        {
            if (placementTimer != null) placementTimer.Stop();
            SavePlacementObserved(CapturePlacementToSave());
        }
        private async void SavePlacementObserved(WindowPlacement placement)
        {
            if (placement == null) return;
            try { await Store.SavePlacementAsync(placement); }
            catch (UserError error) { Log.Event("placement_save_failed", error); ShowError(error.Message, null); }
        }
        public void HidePopup()
        {
            CancelCapture();
            sourceRevision++;
            RememberPlacement();
            if (Current != null) {
                Current.Draft = Ui.Get<TextBox>(Popup, "QuestionInput").Text;
                var pendingSave = SaveCurrentAsync();
            }
            if (outsideClick != null) outsideClick.Dispose();
            Popup.Hide();
        }
        // The user's explicit settings entry: the shell has one window, so this reveals the
        // popup's own settings page instead of opening a second window and instead of hiding
        // the popup first. The page is revealed without activation, so a plain capture never
        // takes focus to reach it.
        public void OpenSettings()
        {
            EnsureCredentialMigration();
            ShowSettingsPage();
        }
        // Production never opens the tray settings window any more; a window created directly
        // (legacy code or a check) still registers itself so the exit path flushes and closes it.
        internal void TrackSettingsWindow(SettingsWindow window) { settingsWindow = window; }
        internal void UntrackSettingsWindow(SettingsWindow window) { if (ReferenceEquals(settingsWindow, window)) settingsWindow = null; }
        public void OpenHistory()
        {
            // The history window is reached from a page the user leaves; the settings page
            // applies its pending edits first and stays open if that cannot be saved.
            var ignored = LeaveSettingsThenAsync(OpenHistoryWindow);
        }
        private void OpenHistoryWindow()
        {
            HidePopup();
            if (historyWindow != null) { historyWindow.Refresh(); historyWindow.Window.Activate(); return; }
            historyWindow = new HistoryWindow(this);
            historyWindow.Window.Closed += (s, e) => historyWindow = null;
            historyWindow.Window.Show(); historyWindow.Window.Activate();
        }
        public Task ApplySettingsAsync(SettingsUpdate update, Dictionary<string, string> keys, HashSet<string> deleted)
        {
            return ApplySettingsAsync(update, keys, deleted, false);
        }

        // explicitApply is set only by the settings Save button: it resolves a pending external
        // rollback or an unreadable transaction marker, and is never implied by an auto-save.
        public Task ApplySettingsAsync(SettingsUpdate update, Dictionary<string, string> keys, HashSet<string> deleted, bool explicitApply)
        {
            if (update == null) throw new ArgumentNullException("update");
            EnsureCredentialMigration();
            var previous = Json.Copy(Store.Settings);
            var changedKeys = keys == null ? new Dictionary<string, string>() : new Dictionary<string, string>(keys);
            var removedKeys = deleted == null ? new HashSet<string>() : new HashSet<string>(deleted);
            Task task = ApplySettingsCoreAsync(update, changedKeys, removedKeys, previous, explicitApply);
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
        private async Task ApplySettingsCoreAsync(SettingsUpdate update, Dictionary<string, string> keys, HashSet<string> deleted, Settings previous, bool explicitApply)
        {
            var keyUndo = new List<Action>();
            var replaced = new List<KeyValuePair<ProviderProfile, ProviderProfile>>();
            Action<Settings, Settings> applyExternal = (committed, candidate) => {
                keyUndo.Clear(); replaced.Clear();
                var failures = new List<string>();
                if (credentialProfiles != null) {
                    // Capture the original value of every scope this change can touch before any
                    // write, including a key that may already exist at the new endpoint.
                    foreach (string id in keys.Keys.Concat(deleted).Distinct().ToArray()) {
                        var oldProfile = committed.Providers.FirstOrDefault(p => p.Id == id);
                        var newProfile = candidate.Providers.FirstOrDefault(p => p.Id == id);
                        string oldKey = "", newOriginalKey = "";
                        if (oldProfile != null) oldKey = ReadCredentialOrThrow(oldProfile, "旧");
                        if (newProfile != null && !SameCredentialScope(oldProfile, newProfile))
                            newOriginalKey = ReadCredentialOrThrow(newProfile, "当前");
                        var capturedOld = oldProfile; var capturedNew = newProfile;
                        string capturedOldKey = oldKey, capturedNewKey = newOriginalKey;
                        keyUndo.Add(() => {
                            var problems = new List<string>();
                            try { RestoreCredential(capturedNew, capturedNewKey); } catch { problems.Add("新地址"); }
                            if (!SameCredentialScope(capturedOld, capturedNew))
                                try { RestoreCredential(capturedOld, capturedOldKey); } catch { problems.Add("旧地址"); }
                            if (problems.Count > 0) throw new UserError("credentials", "未能恢复密钥作用域。");
                        });
                        if (deleted.Contains(id) && newProfile != null) {
                            try { credentialProfiles.Delete(newProfile); } catch { failures.Add("删除密钥"); }
                        }
                        string pending;
                        if (newProfile != null && keys.TryGetValue(id, out pending)) {
                            try { credentialProfiles.Save(newProfile, pending); } catch { failures.Add("保存密钥"); }
                        }
                        if (oldProfile != null && newProfile != null)
                            replaced.Add(new KeyValuePair<ProviderProfile, ProviderProfile>(oldProfile, newProfile));
                    }
                }
                if (NativeEnabled && candidate.AutoStart != committed.AutoStart) {
                    try { OnDispatcher(() => Native.AutoStart(candidate.AutoStart)); } catch { failures.Add("开机启动"); }
                }
                if (NativeEnabled && candidate.Shortcut != committed.Shortcut) {
                    try { OnDispatcher(() => RegisterShortcut(candidate.Shortcut)); } catch { failures.Add("快捷键"); }
                }
                if (failures.Count > 0)
                    throw new UserError("storage", "部分设置未能应用（" + string.Join("、", failures.ToArray()) + "），正在恢复原状态。");
            };
            Action<Settings, Settings> rollbackExternal = (committed, candidate) => {
                // Every rollback item is attempted even when an earlier one fails; the
                // aggregate failure reaches the transaction so it is never reported as restored.
                var problems = new List<string>();
                for (int i = keyUndo.Count - 1; i >= 0; i--) {
                    try { keyUndo[i](); } catch { problems.Add("密钥"); }
                }
                if (NativeEnabled && candidate.AutoStart != committed.AutoStart) {
                    try { OnDispatcher(() => Native.AutoStart(committed.AutoStart)); } catch { problems.Add("开机启动"); }
                }
                if (NativeEnabled && candidate.Shortcut != committed.Shortcut) {
                    try { OnDispatcher(() => RegisterShortcut(committed.Shortcut)); } catch { problems.Add("快捷键"); }
                }
                if (problems.Count > 0)
                    throw new UserError("storage", "未能恢复先前的密钥或系统设置，请重新应用配置。");
            };
            await Store.CommitSettingsAsync(update, applyExternal, rollbackExternal, explicitApply,
                () => { if (credentialProfiles != null) CleanupReplacedCredentials(replaced, deleted); }).ConfigureAwait(true);
            if (Store.Settings.ProviderId != previous.ProviderId || Json.Write(Store.Settings.Provider) != Json.Write(previous.Provider) || keys.Count > 0 || deleted.Count > 0) CancelRequests();
            if (!Store.Settings.HistoryEnabled) Forget(null);
            if (clipboardMenu != null) { clipboardMenu.Checked = Store.Settings.ClipboardMode; tray.Text = "Leaf · " + Store.Settings.Shortcut; }
            if (historyWindow != null) historyWindow.Refresh();
            Log.Event("settings_saved", null);
        }
        private static bool SameCredentialScope(ProviderProfile oldProfile, ProviderProfile newProfile)
        {
            if (oldProfile == null || newProfile == null) return false;
            try { return Credentials.ScopedId(oldProfile) == Credentials.ScopedId(newProfile); } catch { return false; }
        }
        private string ReadCredentialOrThrow(ProviderProfile profile, string label)
        {
            try { return credentialProfiles.Read(profile); }
            catch (Exception error) {
                throw new UserError("credentials", error is UserError ? ((UserError)error).Message : "无法读取" + label + "密钥，已取消本次配置。");
            }
        }
        private void RestoreCredential(ProviderProfile profile, string originalKey)
        {
            if (profile == null) return;
            if (originalKey.Length > 0) credentialProfiles.Save(profile, originalKey);
            else credentialProfiles.Delete(profile);
        }
        private void CleanupReplacedCredentials(List<KeyValuePair<ProviderProfile, ProviderProfile>> replaced, HashSet<string> deleted)
        {
            var failures = new List<string>();
            foreach (var pair in replaced) {
                string oldScoped, newScoped;
                try { oldScoped = Credentials.ScopedId(pair.Key); newScoped = Credentials.ScopedId(pair.Value); }
                catch { failures.Add("地址"); continue; }
                if (oldScoped == newScoped) continue;
                string newKey = "";
                try { newKey = credentialProfiles.Read(pair.Value); }
                catch { failures.Add("读取"); continue; }
                if (deleted.Contains(pair.Value.Id) || newKey.Length > 0) {
                    try { credentialProfiles.Delete(pair.Key); }
                    catch { failures.Add("删除"); }
                }
            }
            if (failures.Count > 0)
                throw new UserError("credentials", "未能清理不再使用的旧地址密钥，请重试。");
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
            // Opening a record shows it on the reading page, so an open settings page is left
            // first; a failed flush keeps that page instead of opening behind it. The epoch the
            // record was found under is captured here: a clear, a delete or a history disable
            // while that leave is still flushing invalidates the record, and the delayed entry
            // must not re-save it under the newer epoch. With no settings page open the entry
            // runs immediately, exactly as before.
            long epoch = Store.HistoryEpoch;
            var ignored = LeaveSettingsThenAsync(() => OpenRecordNow(record, epoch));
        }
        private void OpenRecordNow(TranslationRecord record, long epoch)
        {
            // An opened history record is a root session of its own: the return relation
            // the reading page held is replaced by the record being opened.
            CancelRequests(); demo = false; Current = record; currentHistoryEpoch = epoch;
            ClearPreviousSession();
            DisplayRecord(); ShowPopup();
        }
        public void Forget(string id)
        {
            if (Current != null && (id == null || Current.Id == id)) {
                CancelRequests(); Current = null; ClearPreviousSession(); DisplayRecord();
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
        // Records what was running before cancelling it, so the single retry entry can
        // repeat those operations instead of starting a new translation.
        public void StopForRetry()
        {
            var bundle = new RetryBundle(new[] { activeTranslation, activeWord, activeFollowup });
            CancelRequests();
            DrawChat();
            if (selectedCard != null) DrawCard(selectedCard);
            else if (selectedWord != null) {
                selectedWord = null; HighlightSource(); Topic();
                Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), false);
            }
            Busy();
            if (bundle.Count == 0) { ShowError("已停止。", null); return; }
            ShowError("已停止。尚未完成的结果不会保存。", () => { var ignored = RetryAsync(bundle); });
        }
        private async Task RetryAsync(RetryBundle bundle)
        {
            var record = Current;
            await bundle.RunAsync(record == null ? "" : record.Id);
        }
        private void Busy()
        {
            Ui.Visible(Ui.Get<Grid>(Popup, "BusyPanel"), translating || wordBusy || chatBusy);
            Ui.Get<TextBlock>(Popup, "BusyLabel").Text = translating ? "正在翻译…" : wordBusy ? "正在查词…" : "正在回答…";
            Ui.Get<Button>(Popup, "SendButton").IsEnabled = Current != null && Current.Completed && !chatBusy;
            Ui.Get<Button>(Popup, "AskButton").IsEnabled = Current != null && Current.Completed;
            Ui.Visible(Ui.Get<Button>(Popup, "AskButton"), Current != null && Ui.Get<Grid>(Popup, "SourceEditor").Visibility != Visibility.Visible && Ui.Get<Border>(Popup, "InputPanel").Visibility != Visibility.Visible);
        }
        public bool IsExiting { get { return exiting; } }
        public void Exit()
        {
            var pendingExit = ExitAsync();
            pendingExit.ContinueWith(task => {
                var failure = task.Exception;
                if (failure != null) Log.Event("exit_failed", failure.GetBaseException());
            }, TaskScheduler.Default);
        }
        // Unified exit: refuse new work, cancel what is active, flush pending settings edits
        // and the final snapshot/placement, wait for the ordered queue, then release resources.
        // A failed flush keeps the app alive with a real retry; it never pretends to be saved.
        public async Task ExitAsync()
        {
            if (exiting) return;
            // A shortcut being recorded is ended while the app is still leaving normally: the
            // recording holds the global shortcut released, and ending it once `exiting` is set
            // would skip re-registering it. A flush that fails keeps the app alive, so it must
            // not be left without its shortcut; the leave's own deactivation is a no-op then.
            if (settingsController != null) settingsController.OnHostDeactivated();
            exiting = true;
            CancelRequests();
            if (Current != null && !demo) Current.Draft = Ui.Get<TextBox>(Popup, "QuestionInput").Text;
            var placement = CapturePlacementToSave();
            try {
                // The popup's own settings page is flushed and left before anything is disposed,
                // so a pending preference, a debounced edit or a clicked service apply is saved
                // first. A page that cannot be saved keeps the app alive with its own reason,
                // and a leave already running for this page is shared instead of run twice.
                if (!await TryLeaveSettingsPageAsync().ConfigureAwait(true))
                    throw new UserError("storage", "设置尚未保存完成，请在设置页修正提示后重试退出。");
                if (settingsWindow != null) {
                    bool flushed = await settingsWindow.FlushPendingAsync().ConfigureAwait(true);
                    if (!flushed) throw new UserError("storage", "设置尚未保存完成，请在设置窗口修正提示后重试退出。");
                }
                if (placement != null) await Store.SavePlacementAsync(placement).ConfigureAwait(true);
                if (Current != null && !demo) await SaveCurrentOrThrowAsync().ConfigureAwait(true);
                await WaitForSettingsAsync().ConfigureAwait(true);
            } catch (Exception error) {
                exiting = false;
                Log.Event("exit_flush_failed", error);
                ShowError(error is UserError ? error.Message : "退出前保存失败，应用仍保持打开。", RetryExit);
                ShowPopup();
                return;
            }
            flushedForExit = true;
            Dispose();
            if (Application.Current != null) Application.Current.Shutdown();
        }
        private async void RetryExit()
        {
            if (exiting || disposed) return;
            try { await Store.RetryFailedWritesAsync().ConfigureAwait(true); }
            catch (Exception error) { Log.Event("exit_flush_failed", error); }
            await ExitAsync().ConfigureAwait(true);
        }
        private async Task SaveCurrentOrThrowAsync()
        {
            var record = Current;
            if (record == null || demo) return;
            await Store.SaveAsync(record, currentHistoryEpoch).ConfigureAwait(true);
        }
        public void Dispose()
        {
            if (disposed) return;
            bool alreadyFlushed = flushedForExit;
            flushedForExit = false;
            CancelCapture();
            // A fully flushed exit must not start another save while it disposes; the debounce
            // timer is stopped here, and the fallback persists one placement only once.
            if (placementTimer != null) placementTimer.Stop();
            disposed = true;
            if (copyFeedback != null) copyFeedback.Stop();
            Log.Event("app_exit", null);
            // The fallback path must never block the UI thread on the ordered queue and must
            // not start a save after a fully flushed exit already finished.
            if (!alreadyFlushed) {
                var placement = CapturePlacementToSave();
                if (placement != null) Observe(Store.SavePlacementAsync(placement), "placement_save_failed");
                if (Current != null && !demo) {
                    Current.Draft = Ui.Get<TextBox>(Popup, "QuestionInput").Text;
                    Observe(Store.SaveAsync(Current, currentHistoryEpoch), "history_save_failed");
                }
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
            if (settingsWindow != null) settingsWindow.CloseForExit();
            if (historyWindow != null) historyWindow.Window.Close();
            Popup.Close();
        }
        private void Observe(Task task, string eventName)
        {
            if (task == null) return;
            task.ContinueWith(finished => {
                var failure = finished.Exception;
                if (failure != null) Log.Event(eventName, failure.GetBaseException());
            }, TaskScheduler.Default);
        }
    }
}
