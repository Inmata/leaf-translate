using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Leaf
{
    public sealed class AppShell : IDisposable
    {
        public LocalStore Store { get; private set; }
        public LlmClient Client { get; private set; }
        public DiagnosticLog Log { get; private set; }
        public Window Popup { get; private set; }
        public bool NativeEnabled { get; private set; }
        public SelectionAcquirer Selection { get { return hotkeys.Selection; } set { hotkeys.Selection = value; } }
        public IInternalSelectionReader InternalSelection { get; set; }
        public Func<WindowPlacement> PlacementReader { get; set; }
        internal Func<ProviderProfile, string> CredentialReader;
        internal WorkspaceController Workspace { get; private set; }
        internal PopupPresenter Presenter { get; private set; }
        private readonly HotkeyController hotkeys;
        private readonly SettingsCoordinator settings;
        private readonly PopupSettingsPresenter settingsPage;
        private bool exiting, disposed, flushedForExit;
        internal bool pinned;
        public TranslationRecord Current { get { return Workspace.Current; } }
        public bool Pinned { get { return pinned; } }
        public bool IsExiting { get { return exiting; } }
        internal bool IsDisposed { get { return disposed; } }
        internal bool CaptureBusy { get { return hotkeys.CaptureBusy; } }
        private IntPtr handle;
        private DispatcherTimer placementTimer;
        private bool restoringPlacement, placementInitialized;
        private OutsideClick outsideClick;
        private TrayHost tray;
        private SettingsWindow settingsWindow;
        private HistoryWindow historyWindow;
        private const int WmSizing = 0x0214;
        private const int WmExitSizeMove = 0x0232;
        public event Action SwapRequested;

        public AppShell(LocalStore store, bool native) : this(store, native, new LlmClient(), null) { }
        public AppShell(LocalStore store, bool native, LlmClient client) : this(store, native, client, null) { }
        public AppShell(LocalStore store, bool native, LlmClient client, ICredentialProfiles credentials)
        {
            Store = store; NativeEnabled = native; Client = client;
            Log = client.Log ?? new DiagnosticLog(System.IO.Path.Combine(store.Folder, "logs")); Client.Log = Log;
            Popup = Ui.Load("Popup");
            RefreshTypeface();
            Presenter = new PopupPresenter(this);
            Workspace = new WorkspaceController(this, Presenter, new TranslationService(client), new LearningService(client));
            hotkeys = new HotkeyController(this, Presenter);
            settings = new SettingsCoordinator(this, credentials);
            settingsPage = new PopupSettingsPresenter(this, Presenter);
            SwapRequested += () => { var ignored = Workspace.RequestSwapAsync(); };
            Presenter.Initialize();
            InternalSelection = new InternalSelectionReader(Popup);
        }

        public void Start(bool background, bool demonstration)
        {
            Workspace.SetDemo(demonstration);
            EnsureCredentialMigration();
            handle = new WindowInteropHelper(Popup).EnsureHandle();
            HwndSource.FromHwnd(handle).AddHook(Messages);
            outsideClick = new OutsideClick(() => Popup.Dispatcher.BeginInvoke(new Action(() => {
                // A click outside is a real entry that leaves the settings page for the reading
                // page, so it takes the same leave; a failed flush keeps the page instead.
                if (Popup.IsVisible && !pinned) { var ignored = LeaveSettingsThenAsync(HidePopup); }
            })));
            tray = new TrayHost(this);
            try { RegisterShortcut(Store.Settings.Shortcut); }
            catch (UserError error) { Log.Event("shortcut_register_failed", error); ShowError(error.Message, OpenSettings); }
            if (Workspace.IsDemo) { PopulateDemo(); ShowPopup(); }
            else if (!background && (ReadCredential(Store.Settings.Provider).Length == 0 || string.IsNullOrWhiteSpace(Store.Settings.Provider.Model))) OpenSettings();
            else if (!background) ShowPopup();
            if (!string.IsNullOrEmpty(Store.Warning)) {
                tray.ShowWarning(Store.Warning);
            }
        }

        internal IntPtr Messages(IntPtr window, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (message == 0x0312 && wparam.ToInt32() == HotkeyController.HotkeyId) { handled = true; if (!hotkeys.Recording) hotkeys.CaptureAndTranslate(); }
            if (message == 0x8001) { handled = true; ShowPopup(); }
            // A drag of the window's edge is followed, not absorbed: the reading page holds its
            // scale and the highlight stays hidden until the drag ends, when the final size is
            // applied once. Neither message is marked handled, so the system's own sizing and
            // the coalesced size change behind it still run.
            if (message == WmSizing) Presenter.Typography.BeginInteractiveResize();
            if (message == WmExitSizeMove) Presenter.Typography.EndInteractiveResize();
            return IntPtr.Zero;
        }

        public void ShowPopup()
        {
            if (exiting) return;
            // A show that follows a drag interrupted by a hide pays the pass that drag never
            // wrote before anything is displayed; every other show pays one false read.
            if (Presenter.Typography.resizeTypographyPending) Presenter.Typography.ApplyPendingResizeTypography();
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

        internal void QueuePlacementSave()
        {
            if (!NativeEnabled || restoringPlacement || !placementInitialized || !Popup.IsVisible || Popup.WindowState == WindowState.Minimized) return;
            if (placementTimer == null) {
                placementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                placementTimer.Tick += (s, e) => { placementTimer.Stop(); RememberPlacement(); };
            }
            placementTimer.Stop(); placementTimer.Start();
        }

        internal WindowPlacement CapturePlacementToSave()
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

        internal void RememberPlacement()
        {
            if (placementTimer != null) placementTimer.Stop();
            SavePlacementObserved(CapturePlacementToSave());
        }

        internal async void SavePlacementObserved(WindowPlacement placement)
        {
            if (placement == null) return;
            try { await Store.SavePlacementAsync(placement); }
            catch (UserError error) { Log.Event("placement_save_failed", error); ShowError(error.Message, null); }
        }

        public void HidePopup()
        {
            CancelCapture();
            // A page nobody is looking at must not flip under the user, nor store the target
            // language a hidden identification happened to find.
            Workspace.CancelSwap();
            // Hiding ends a drag that was still running: the next show reads the window's own
            // size, and the highlight pass that was queued is dropped rather than painted.
            Presenter.Typography.CancelInteractiveResize();
            Presenter.Source.sourceRevision++;
            RememberPlacement();
            if (Current != null) {
                Current.Draft = Presenter.QuestionDraft;
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

        internal void OpenHistoryWindow()
        {
            HidePopup();
            if (historyWindow != null) { historyWindow.Refresh(); historyWindow.Window.Activate(); return; }
            historyWindow = new HistoryWindow(this);
            historyWindow.Window.Closed += (s, e) => historyWindow = null;
            historyWindow.Window.Show(); historyWindow.Window.Activate();
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
            var ignored = LeaveSettingsThenAsync(() => Workspace.OpenRecordNow(record, epoch));
        }

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
            settingsPage.OnHostDeactivated();
            exiting = true;
            Workspace.CancelRequests();
            if (Current != null && !Workspace.IsDemo) Current.Draft = Presenter.QuestionDraft;
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
                if (Current != null && !Workspace.IsDemo) await Workspace.SaveCurrentOrThrowAsync().ConfigureAwait(true);
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

        internal async void RetryExit()
        {
            if (exiting || disposed) return;
            try { await Store.RetryFailedWritesAsync().ConfigureAwait(true); }
            catch (Exception error) { Log.Event("exit_flush_failed", error); }
            await ExitAsync().ConfigureAwait(true);
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
            // A window that is closing for good owes nothing: the drag's freeze is released
            // and the pass it left owing is abandoned, the typography pass refuses every later
            // call, and the highlight layer stops watching its box - so the layout, size and
            // scroll ticks the close itself raises neither rebuild the reading page nor ask
            // for geometry.
            Presenter.Stop();
            Log.Event("app_exit", null);
            // The fallback path must never block the UI thread on the ordered queue and must
            // not start a save after a fully flushed exit already finished.
            if (!alreadyFlushed) {
                var placement = CapturePlacementToSave();
                if (placement != null) Observe(Store.SavePlacementAsync(placement), "placement_save_failed");
                if (Current != null && !Workspace.IsDemo && !Workspace.IsSwappedView(Current)) {
                    Current.Draft = Presenter.QuestionDraft;
                    Observe(Store.SaveAsync(Current, Workspace.HistoryEpoch), "history_save_failed");
                }
            }
            Workspace.CancelRequests();
            if (outsideClick != null) outsideClick.Dispose();
            hotkeys.Dispose();
            if (tray != null) { tray.Dispose(); tray = null; }
            Workspace.DisposeRequests();
            Client.Dispose();
            exiting = true;
            if (settingsWindow != null) settingsWindow.CloseForExit();
            if (historyWindow != null) historyWindow.Window.Close();
            Popup.Close();
        }

        internal void Observe(Task task, string eventName)
        {
            if (task == null) return;
            task.ContinueWith(finished => {
                var failure = finished.Exception;
                if (failure != null) Log.Event(eventName, failure.GetBaseException());
            }, TaskScheduler.Default);
        }

        internal void OnDispatcher(Action action)
        {
            if (Popup.Dispatcher.CheckAccess()) action(); else Popup.Dispatcher.Invoke(action);
        }
        internal void RaiseSwapRequested() { var handler = SwapRequested; if (handler != null) handler(); }
        internal void RefreshTypeface() { bool missingFont; Ui.ApplyPaperFont(Store.Settings.FontFamilyName, out missingFont); }
        internal void RefreshHistory() { if (historyWindow != null) historyWindow.Refresh(); }
        internal void RefreshTraySettings() { if (tray != null) tray.RefreshSettings(); }
        internal void RegisterShortcut(string shortcut) { hotkeys.RegisterShortcut(shortcut); }
        internal void CancelCapture() { hotkeys.CancelCapture(); }
        internal string ReadCredential(ProviderProfile profile) { return settings.ReadCredential(profile); }
        internal string KeyFor(ProviderProfile profile) { return settings.KeyFor(profile); }
        private void EnsureCredentialMigration() { settings.EnsureCredentialMigration(); }
        public void SetShortcutRecording(bool recording) { hotkeys.SetShortcutRecording(recording); }
        public Task InvokeShortcutAsync() { return hotkeys.InvokeShortcutAsync(); }
        public Task TranslateAsync(string text, string sourceKind, bool force) { return Workspace.TranslateAsync(text, sourceKind, force); }
        public Task SelectWordAsync(TextPiece word, bool force) { return Workspace.SelectWordAsync(word, force); }
        public Task SendChatAsync() { return Workspace.SendChatAsync(); }
        public void PopulateDemo() { Workspace.PopulateDemo(); }
        public void StopForRetry() { Workspace.StopForRetry(); }
        public void Forget(string id) { Workspace.Forget(id); }
        public Task SaveCurrentAsync() { return Workspace.SaveCurrentAsync(); }
        public Task TranslateInternalSelectionAsync(string text) { return Workspace.TranslateInternalSelectionAsync(text); }
        public void ReturnToPreviousSession() { Workspace.ReturnToPreviousSession(); }
        public bool CanReturnToPreviousSession { get { return Workspace.CanReturnToPreviousSession; } }
        public void BeginSourceEdit(bool focus) { Presenter.Source.BeginSourceEdit(focus); }
        public Task SubmitSourceAsync() { return Presenter.Source.SubmitSourceAsync(); }
        public TextPiece WordHitAt(int displayOffset) { return Presenter.Source.WordHitAt(displayOffset); }
        public bool WordHighlighted { get { return Presenter.Source.WordHighlighted; } }
        public void UpdatePopupTypography() { Presenter.UpdatePopupTypography(); }
        public void ShowError(string text, Action retryAction) { Presenter.ShowError(text, retryAction); }
        public static double TypographyScale(double width, double height) { return PopupTypography.TypographyScale(width, height); }
        public static void BuildOffsetMaps(string source, string display, int[] displayToSource, int[] sourceToDisplay) { SourceOffsetMap.BuildOffsetMaps(source, display, displayToSource, sourceToDisplay); }
        internal static string ReadingColumnReport { get { return PopupTypography.ReadingColumnReport; } }
        public bool IsSettingsPageOpen { get { return settingsPage.IsSettingsPageOpen; } }
        public void ShowSettingsPage() { settingsPage.ShowSettingsPage(); }
        public Task<bool> TryLeaveSettingsPageAsync() { return settingsPage.TryLeaveSettingsPageAsync(); }
        internal Task LeaveSettingsThenAsync(Action after) { return settingsPage.LeaveSettingsThenAsync(after); }
        internal void ToggleSettingsPage() { settingsPage.ToggleSettingsPage(); }
        internal void InitializeSettingsPage() { settingsPage.InitializeSettingsPage(); }
        public Task ApplySettingsAsync(SettingsUpdate update, Dictionary<string, string> keys, HashSet<string> deleted) { return settings.ApplySettingsAsync(update, keys, deleted); }
        public Task ApplySettingsAsync(SettingsUpdate update, Dictionary<string, string> keys, HashSet<string> deleted, bool explicitApply) { return settings.ApplySettingsAsync(update, keys, deleted, explicitApply); }
        public Task WaitForSettingsAsync() { return settings.WaitForSettingsAsync(); }
        public Task WaitForPersistenceAsync() { return settings.WaitForPersistenceAsync(); }
    }
}
