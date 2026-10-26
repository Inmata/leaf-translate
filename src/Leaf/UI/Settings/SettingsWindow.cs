using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Leaf
{
    // Tray-window chrome. All settings logic lives in SettingsPresenter so the same
    // panel can be hosted inside the popup shell without duplicating behavior.
    public sealed class SettingsWindow
    {
        private readonly AppShell shell;
        private readonly SettingsPresenter controller;
        private bool allowClose;
        public Window Window { get; private set; }
        public FrameworkElement Panel { get; private set; }

        public SettingsWindow(AppShell owner)
        {
            shell = owner;
            Window = Ui.Load("Settings");
            Panel = Ui.LoadPanel("SettingsPanel");
            Ui.Get<ContentControl>(Window, "PanelHost").Content = Panel;
            controller = new SettingsPresenter(owner, Panel, () => Window.IsVisible);
            owner.TrackSettingsWindow(this);
            Ui.Click(Window, "CloseSettingsButton", () => Window.Close());
            Window.Deactivated += (s, e) => controller.OnHostDeactivated();
            Window.Closing += async (s, e) => {
                if (allowClose) return;
                e.Cancel = true;
                controller.OnHostDeactivated();
                if (!await controller.FlushPendingAsync()) return;
                try { await shell.WaitForSettingsAsync(); }
                catch (Exception error) {
                    shell.Log.Event("settings_close_flush_failed", error);
                    controller.ReportStatus(error is UserError ? error.Message : "保存未完成，设置窗口保持打开。", true);
                    return;
                }
                allowClose = true;
                Window.Close();
            };
            Window.Closed += (s, e) => { controller.Detach(); owner.UntrackSettingsWindow(this); };
        }
        // Used by a flushed exit: the timers are already stopped, so closing must not run
        // the normal flush again or start a task that needs the dispatcher.
        public void CloseForExit()
        {
            controller.Detach();
            allowClose = true;
            Window.Close();
        }
        public Task<bool> FlushPendingAsync() { return controller.FlushPendingAsync(); }
    }
}
