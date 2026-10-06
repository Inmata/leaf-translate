using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Leaf
{
    // Settings live on a page inside the popup shell. The reading page is only hidden,
    // never rebuilt, so the current session survives the round trip. A fresh panel and
    // controller are created per visit: leaving discards unapplied service drafts exactly
    // like the old tray window's close did, after waiting for a clicked apply to commit.
    public sealed partial class AppShell
    {
        private FrameworkElement settingsPanel;
        private SettingsController settingsController;
        // One page is left once. The back button, the window close and an exit entry can all
        // ask while the first attempt is still flushing, so the running attempt is shared for
        // the exact page instance it was started for.
        private LeaveAttempt leaveAttempt;

        private sealed class LeaveAttempt
        {
            public SettingsController Controller;
            public Task<bool> Completion;
        }

        public bool IsSettingsPageOpen
        {
            get { return settingsController != null && Ui.Get<Grid>(Popup, "SettingsPage").Visibility == Visibility.Visible; }
        }

        private void InitializeSettingsPage()
        {
            Ui.Click(Popup, "SettingsBackButton", () => { var ignored = TryLeaveSettingsPageAsync(); });
            // The popup is the settings host, so its own Deactivated event keeps the entry the
            // tray window's Deactivated had: a topmost popup can lose activation without the
            // page being left, and a recording started there must not stay open behind it.
            Popup.Deactivated += (s, e) => { if (settingsController != null) settingsController.OnHostDeactivated(); };
            Popup.Closed += (s, e) => { if (settingsController != null) settingsController.Detach(); };
        }

        // The top-bar entry is a toggle: while its page is open the same control leaves it,
        // through the exact flush the back entry runs, so a pending edit is applied and a
        // failed flush keeps the page open with its reason. Every other entry - the tray, the
        // error panel's 打开设置, the recovery barrier - still only opens the page.
        private void ToggleSettingsPage()
        {
            if (IsSettingsPageOpen) { var ignored = TryLeaveSettingsPageAsync(); return; }
            ShowSettingsPage();
        }

        public void ShowSettingsPage()
        {
            if (exiting || IsSettingsPageOpen) return;
            // Entering settings never cancels the current translation or touches the session.
            settingsPanel = Ui.LoadPanel("SettingsPanel");
            settingsController = new SettingsController(this, settingsPanel, () => Popup.IsVisible);
            Ui.Get<ContentControl>(Popup, "SettingsPageHost").Content = settingsPanel;
            var body = Ui.Get<ScrollViewer>(Popup, "BodyScroll");
            bodyScrollBackup = body.VerticalOffset;
            Ui.Visible(Ui.Get<Grid>(Popup, "TranslatePage"), false);
            Ui.Visible(Ui.Get<Grid>(Popup, "SettingsPage"), true);
            // The source badge stays as it is on the reading page: the origin survives
            // entering settings and is recomputed on the way back out.
            settingsController.Activate();
            UpdateTopBarState();
            ShowPopup();
        }

        // Flushes debounced preference edits, waits for a clicked service apply and for the
        // settings commit before the reading page returns; a failure keeps the settings page
        // open with its status, and the same entry can be retried.
        public Task<bool> TryLeaveSettingsPageAsync()
        {
            if (!IsSettingsPageOpen) return Task.FromResult(true);
            var controller = settingsController;
            var running = leaveAttempt;
            // Sharing the running attempt keeps a second entry from flushing the same page
            // twice, and from detaching whatever page is there when its await resumes.
            if (running != null && ReferenceEquals(running.Controller, controller)) return running.Completion;
            var attempt = new LeaveAttempt { Controller = controller };
            leaveAttempt = attempt;
            attempt.Completion = LeaveSettingsPageAsync(attempt);
            return attempt.Completion;
        }

        private async Task<bool> LeaveSettingsPageAsync(LeaveAttempt attempt)
        {
            try { return await LeavePageAsync(attempt.Controller); }
            finally { if (ReferenceEquals(leaveAttempt, attempt)) leaveAttempt = null; }
        }

        private async Task<bool> LeavePageAsync(SettingsController controller)
        {
            controller.OnHostDeactivated();
            if (!await controller.FlushPendingAsync()) return false;
            try { await WaitForSettingsAsync(); }
            catch (Exception error) {
                Log.Event("settings_page_leave_flush_failed", error);
                controller.ReportStatus(error is UserError ? error.Message : "保存未完成，请处理后重试。", true);
                return false;
            }
            // The page this attempt belongs to may already be gone; only the controller this
            // attempt captured is left, and a page that replaced it stays open.
            if (!ReferenceEquals(settingsController, controller)) return !IsSettingsPageOpen;
            LeaveSettingsPage();
            return true;
        }

        // Unapplied service drafts are discarded with the page instance: the password is
        // cleared and in-flight model/test requests are cancelled, matching the old
        // window-close semantics.
        private void LeaveSettingsPage()
        {
            Ui.Visible(Ui.Get<Grid>(Popup, "SettingsPage"), false);
            Ui.Visible(Ui.Get<Grid>(Popup, "TranslatePage"), true);
            var badge = Ui.Get<TextBlock>(Popup, "SourceBadge");
            badge.Text = Current == null ? "" : (demo ? "演示内容 · 未调用 API" : SourceKindLabel(Current.SourceKind));
            settingsController.Detach();
            Ui.Get<ContentControl>(Popup, "SettingsPageHost").Content = null;
            settingsPanel = null; settingsController = null;
            UpdateTopBarState();
            RestoreReadingScroll(bodyScrollBackup, 0);
            bodyScrollBackup = 0;
        }
        private async Task LeaveSettingsThenAsync(Action after)
        {
            if (IsSettingsPageOpen && !await TryLeaveSettingsPageAsync()) return;
            if (after != null) after();
        }
    }
}
