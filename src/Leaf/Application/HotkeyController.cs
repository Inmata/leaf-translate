using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;

namespace Leaf
{
    public sealed class HotkeyController : IDisposable
    {
        internal const int HotkeyId = 0x4C46;
        private readonly AppShell shell;
        private readonly PopupPresenter view;
        private IntPtr handle;
        private string registeredShortcut;
        private GlobalShortcut shortcutRegistration;
        private bool shortcutRecording, captureBusy;
        private CancellationTokenSource captureCancellation;
        public SelectionAcquirer Selection { get; set; }
        internal bool Recording { get { return shortcutRecording; } }
        internal bool CaptureBusy { get { return captureBusy; } }
        internal HotkeyController(AppShell shell, PopupPresenter view)
        {
            this.shell = shell; this.view = view;
            Selection = new SelectionAcquirer(new WindowsSelectionProbe());
        }
        internal async void CaptureAndTranslate() { await InvokeShortcutAsync(); }
        public void Dispose()
        {
            CancelCapture();
            if (shortcutRegistration != null) { shortcutRegistration.Dispose(); shortcutRegistration = null; registeredShortcut = null; }
        }

        internal void RegisterShortcut(string shortcut)
        {
            var specification = HotkeySpec.Parse(shortcut);
            if (!shell.NativeEnabled) return;
            if (registeredShortcut == shortcut) return;
            string previous = registeredShortcut;
            if (handle == IntPtr.Zero) handle = new WindowInteropHelper(shell.Popup).EnsureHandle();
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
            shell.Log.Event("shortcut_registered", null);
        }

        public void SetShortcutRecording(bool recording)
        {
            if (shortcutRecording == recording) return;
            shortcutRecording = recording;
            if (recording) {
                if (shortcutRegistration != null) { shortcutRegistration.Dispose(); shortcutRegistration = null; }
                registeredShortcut = null;
            } else if (!shell.IsDisposed && !shell.IsExiting) {
                try { RegisterShortcut(shell.Store.Settings.Shortcut); }
                catch (UserError error) { shell.Log.Event("shortcut_restore_failed", error); view.ShowError(error.Message, shell.OpenSettings); }
            }
        }

        // The shortcut is the only place a capture is started. Everything it needs is
        // snapshotted first, and no setting is re-read after the acquisition returns.
        public async Task InvokeShortcutAsync()
        {
            if (captureBusy || shortcutRecording || shell.IsExiting) return;
            // A shortcut is a new reading of its own, so an identification still running for the
            // page it is about to replace must not land on it or store its target language.
            shell.Workspace.CancelSwap();
            var target = Selection.Snapshot();
            var invocation = Json.Copy(shell.Store.Settings);
            if (!invocation.ClipboardMode && target.IsLeaf) {
                // Leaf itself is in front, so the page the user is looking at is read before
                // anything is revealed or focused: a selection made inside the popup is its own
                // new session, while no page selection at all keeps the existing behavior.
                var page = shell.InternalSelection != null ? shell.InternalSelection.Read() : InternalSelectionResult.NoPage;
                shell.Log.InternalSelection(page.Status, page.Reason, page.Source, page.TextLength);
                if (page.Status == InternalSelectionStatus.Text) {
                    // The reading page replaces whatever page is on screen, so an open settings
                    // page is left first; a failed flush keeps it and the current session.
                    if (!await shell.TryLeaveSettingsPageAsync()) return;
                    await shell.Workspace.TranslateInternalSelectionAsync(page.Text);
                    return;
                }
                // The settings page is not the reading page: a shortcut there stays on the page
                // and leaves the hidden session - its card, follow-up panel, draft and editor -
                // exactly as it was, instead of restoring that page behind the settings.
                if (shell.IsSettingsPageOpen) return;
                view.Source.HandleLeafSelectionShortcut(invocation.FocusInputOnShortcut);
                return;
            }
            // An external capture runs while the original foreground is intact and reveals the
            // popup immediately. Only its result decides what happens to an open settings
            // page: a text result is shown on the reading page and leaves the page first, so
            // pending preferences are applied then; inconclusive outcomes never touch it.
            long revision = view.Source.sourceRevision;
            var owner = new CancellationTokenSource();
            captureCancellation = owner; captureBusy = true;
            string captureId = Guid.NewGuid().ToString("N");
            SelectionCaptureResult result;
            try {
                // Start acquisition with the original foreground intact, then show immediately.
                var capture = Selection.CaptureAsync(target, invocation.ClipboardMode, invocation.Shortcut, owner.Token);
                shell.ShowPopup();
                result = await capture;
            } finally {
                if (ReferenceEquals(captureCancellation, owner)) captureCancellation = null;
                captureBusy = false; owner.Dispose();
            }
            shell.Log.Capture(CaptureTelemetry.For(captureId, target, invocation.ClipboardMode, invocation.Shortcut, result));
            if (view.Source.sourceRevision != revision || shell.IsExiting || result.Status == CaptureStatus.Cancelled) return;
            if (result.Status == CaptureStatus.Text) {
                // Only a real result needs the reading page, so only now is the settings page
                // left; a failed flush keeps that page and discards this result. The flush can
                // wait, so the session the capture started from is rechecked before it lands:
                // a revision change or an exit during that wait means this result is stale.
                if (!await shell.TryLeaveSettingsPageAsync()) return;
                if (view.Source.sourceRevision != revision || shell.IsExiting) return;
                var translation = shell.Workspace.TranslateAsync(result.Text, invocation.ClipboardMode ? "剪贴板" : "选中文字", false);
                if (invocation.FocusInputOnShortcut) view.Source.BeginSourceEdit(true);
                await translation;
                return;
            }
            if (result.Status == CaptureStatus.Failed) {
                if (!await shell.TryLeaveSettingsPageAsync()) return;
                view.Source.PrepareManualInput(result.Reason, invocation.ClipboardMode, invocation.FocusInputOnShortcut);
                view.ShowError(CaptureMessages.For(result.Reason), () => { var retried = InvokeShortcutAsync(); });
                return;
            }
            // Empty and Unavailable are inconclusive, not failures: the popup is revealed
            // quietly and whatever page, session, draft and editor state is open stays as it
            // is. The badge states what happened; manual input starts only through the
            // explicit edit entry, never automatically.
            view.Source.MarkInconclusiveCapture(result.Reason, invocation.ClipboardMode);
            shell.ShowPopup();
        }

        // Abandons an acquisition whose result the user has already made irrelevant.
        internal void CancelCapture()
        {
            var owner = captureCancellation;
            if (owner == null) return;
            try { owner.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
