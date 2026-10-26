using System;
using System.Diagnostics;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace Leaf
{
    public sealed class TrayHost : IDisposable
    {
        private readonly AppShell shell;
        private Forms.NotifyIcon tray;
        private Forms.ToolStripMenuItem clipboardMenu;
        public TrayHost(AppShell shell) { this.shell = shell; BuildTray(); }
        public void RefreshSettings()
        {
            if (clipboardMenu != null) { clipboardMenu.Checked = shell.Store.Settings.ClipboardMode; tray.Text = "Leaf · " + shell.Store.Settings.Shortcut; }
        }
        public void ShowWarning(string warning) { tray.ShowBalloonTip(5000, "Leaf", warning, Forms.ToolTipIcon.Warning); }
        public void Dispose()
        {
            if (tray == null) return;
            tray.Visible = false; var icon = tray.Icon; tray.Dispose(); tray = null;
            if (icon != null) icon.Dispose();
        }

        private void BuildTray()
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示浮窗", null, (s, e) => shell.ShowPopup());
            clipboardMenu = new Forms.ToolStripMenuItem("剪贴板模式") { Checked = shell.Store.Settings.ClipboardMode };
            clipboardMenu.Click += async (s, e) => {
                try {
                    var next = Json.Copy(shell.Store.Settings); next.ClipboardMode = !next.ClipboardMode;
                    await shell.Store.SaveSettingsAsync(SettingsUpdate.Preferences(next));
                    clipboardMenu.Checked = next.ClipboardMode;
                    tray.ShowBalloonTip(2500, "Leaf", next.ClipboardMode ? "快捷键读取剪贴板，请先复制文字。" : "快捷键尝试读取当前选中文字。", Forms.ToolTipIcon.Info);
                } catch (UserError error) { shell.ShowError(error.Message, null); }
            };
            menu.Items.Add(clipboardMenu); menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("历史记录", null, (s, e) => shell.OpenHistory());
            menu.Items.Add("设置", null, (s, e) => shell.OpenSettings());
            menu.Items.Add("打开日志", null, (s, e) => {
                try {
                    System.IO.Directory.CreateDirectory(shell.Log.Folder);
                    Process.Start(new ProcessStartInfo(shell.Log.Folder) { UseShellExecute = true });
                } catch (Exception error) { shell.Log.Event("logs_open_failed", error); shell.ShowError("无法打开日志目录。", null); shell.ShowPopup(); }
            });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, async (s, e) => await shell.ExitAsync());
            tray = new Forms.NotifyIcon { Icon = TrayIcon(), Text = "Leaf · " + shell.Store.Settings.Shortcut, ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (s, e) => shell.ShowPopup();
        }

        private static Icon TrayIcon()
        {
            using (var stream = typeof(AppShell).Assembly.GetManifestResourceStream("Leaf.Assets.Leaf.ico"))
            using (var icon = new Icon(stream, 32, 32)) return (Icon)icon.Clone();
        }
    }
}
