using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Leaf
{
    // Settings live on a page inside the popup shell. The reading page is only hidden,
    // never rebuilt, so the current session survives the round trip. A fresh panel and
    // controller are created per visit: leaving discards unapplied service drafts exactly
    // like the old tray window's close did.
    public sealed partial class AppShell
    {
        private FrameworkElement settingsPanel;
        private SettingsController settingsController;

        public bool IsSettingsPageOpen
        {
            get { return settingsController != null && Ui.Get<Grid>(Popup, "SettingsPage").Visibility == Visibility.Visible; }
        }

        private void InitializeSettingsPage()
        {
            Ui.Click(Popup, "SettingsBackButton", () => { var ignored = TryLeaveSettingsPageAsync(); });
            Popup.Closed += (s, e) => { if (settingsController != null) settingsController.Detach(); };
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
            Ui.Get<TextBlock>(Popup, "SourceBadge").Text = "";
            settingsController.Activate();
            ShowPopup();
        }

        // Flushes debounced preference edits and waits for the settings commit before the
        // reading page returns; a failure keeps the settings page open with its status.
        public async Task<bool> TryLeaveSettingsPageAsync()
        {
            if (!IsSettingsPageOpen) return true;
            settingsController.OnHostDeactivated();
            if (!await settingsController.FlushPendingAsync()) return false;
            try { await WaitForSettingsAsync(); }
            catch (Exception error) {
                Log.Event("settings_page_leave_flush_failed", error);
                settingsController.ReportStatus(error is UserError ? error.Message : "保存未完成，请处理后重试。", true);
                return false;
            }
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
            badge.Text = Current == null ? "" : (demo ? "演示内容 · 未调用 API" : "来自" + SourceKindLabel(Current.SourceKind));
            settingsController.Detach();
            Ui.Get<ContentControl>(Popup, "SettingsPageHost").Content = null;
            settingsPanel = null; settingsController = null;
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
