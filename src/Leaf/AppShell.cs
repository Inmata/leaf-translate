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
        public Window Popup { get; private set; }
        public bool NativeEnabled { get; private set; }
        public TranslationRecord Current { get; private set; }
        private readonly RequestGate generation = new RequestGate();
        private readonly RequestGate wordGeneration = new RequestGate();
        private CancellationTokenSource mainCancellation, wordCancellation, chatCancellation;
        private bool translating, wordBusy, chatBusy, captureBusy, pinned, exiting, demo, disposed;
        public bool Pinned { get { return pinned; } }
        private IntPtr handle;
        private string registeredShortcut;
        private OutsideClick outsideClick;
        private Forms.NotifyIcon tray;
        private Forms.ToolStripMenuItem clipboardMenu;
        private SettingsWindow settingsWindow;
        private HistoryWindow historyWindow;
        private TextPiece selectedWord;
        private WordCard selectedCard;
        private Action retry;
        private const int HotkeyId = 0x4C46;

        public AppShell(LocalStore store, bool native) : this(store, native, new LlmClient()) { }
        public AppShell(LocalStore store, bool native, LlmClient client)
        {
            Store = store; NativeEnabled = native; Client = client;
            Popup = Ui.Load("Popup"); InitializePopup();
        }
        public void Start(bool background, bool demonstration)
        {
            demo = demonstration;
            handle = new WindowInteropHelper(Popup).EnsureHandle();
            HwndSource.FromHwnd(handle).AddHook(Messages);
            outsideClick = new OutsideClick(() => Popup.Dispatcher.BeginInvoke(new Action(() => {
                if (Popup.IsVisible && !pinned) HidePopup();
            })));
            BuildTray();
            try { RegisterShortcut(Store.Settings.Shortcut); }
            catch (UserError error) { ShowError(error.Message, OpenSettings); }
            if (demo) { PopulateDemo(); ShowPopup(); }
            else if (!background && Credentials.Read(Store.Settings.ProviderId).Length == 0) OpenSettings();
            else if (!background) ShowPopup();
            if (!string.IsNullOrEmpty(Store.Warning)) {
                tray.ShowBalloonTip(5000, "叶译", Store.Warning, Forms.ToolTipIcon.Warning);
            }
        }
        private IntPtr Messages(IntPtr window, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (message == 0x0312 && wparam.ToInt32() == HotkeyId) { handled = true; CaptureAndTranslate(); }
            if (message == 0x8001) { handled = true; ShowPopup(); }
            return IntPtr.Zero;
        }
        private void BuildTray()
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示浮窗", null, (s, e) => ShowPopup());
            clipboardMenu = new Forms.ToolStripMenuItem("剪贴板模式") { Checked = Store.Settings.ClipboardMode };
            clipboardMenu.Click += (s, e) => {
                try {
                    var settings = Json.Copy(Store.Settings); settings.ClipboardMode = !settings.ClipboardMode;
                    Store.SaveSettings(settings); clipboardMenu.Checked = settings.ClipboardMode;
                    tray.ShowBalloonTip(2500, "叶译", settings.ClipboardMode ? "快捷键读取剪贴板，请先复制文字。" : "快捷键尝试读取当前选中文字。", Forms.ToolTipIcon.Info);
                } catch (UserError error) { ShowError(error.Message, null); }
            };
            menu.Items.Add(clipboardMenu); menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("历史记录", null, (s, e) => OpenHistory());
            menu.Items.Add("设置", null, (s, e) => OpenSettings());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => Exit());
            tray = new Forms.NotifyIcon { Icon = TrayIcon(), Text = "叶译 · " + Store.Settings.Shortcut, ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (s, e) => ShowPopup();
        }
        private static Icon TrayIcon()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var graphics = Graphics.FromImage(bitmap)) {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                using (var brush = new SolidBrush(Color.FromArgb(82, 119, 97))) graphics.FillEllipse(brush, 2, 2, 28, 28);
                using (var pen = new Pen(Color.FromArgb(247, 250, 241), 1.9f)) {
                    graphics.DrawBezier(pen, 11, 24, 6, 13, 15, 6, 24, 9);
                    graphics.DrawBezier(pen, 24, 9, 23, 18, 19, 23, 11, 24);
                    graphics.DrawLine(pen, 10, 25, 22, 11);
                }
                IntPtr pointer = bitmap.GetHicon();
                try { using (var borrowed = Icon.FromHandle(pointer)) return (Icon)borrowed.Clone(); }
                finally { Native.DestroyIcon(pointer); }
            }
        }
        private void RegisterShortcut(string shortcut)
        {
            var specification = HotkeySpec.Parse(shortcut);
            if (!NativeEnabled) return;
            if (registeredShortcut == shortcut) return;
            string previous = registeredShortcut;
            if (previous != null) Native.UnregisterHotKey(handle, HotkeyId);
            if (!Native.RegisterHotKey(handle, HotkeyId, specification.Modifiers, specification.VirtualKey)) {
                registeredShortcut = null;
                if (previous != null) {
                    var old = HotkeySpec.Parse(previous);
                    if (Native.RegisterHotKey(handle, HotkeyId, old.Modifiers, old.VirtualKey)) registeredShortcut = previous;
                }
                throw new UserError("shortcut", "快捷键已被其他软件占用。请在设置中换一个组合。");
            }
            registeredShortcut = shortcut;
        }
        private async void CaptureAndTranslate()
        {
            if (captureBusy) return;
            captureBusy = true;
            string text = null, kind = Store.Settings.ClipboardMode ? "剪贴板" : "选中文字";
            try {
                text = Store.Settings.ClipboardMode ? Native.ClipboardText() : await Native.SelectedTextAsync();
            } catch (Exception error) {
                if (!(error is OperationCanceledException)) {
                    CancelRequests(); Current = null; DisplayRecord();
                    Ui.Get<TextBlock>(Popup, "SourceBadge").Text = Store.Settings.ClipboardMode ? "剪贴板模式" : "选中模式";
                    ShowError(error is UserError ? error.Message : "没有取得文字，请重试。", CaptureAndTranslate);
                    ShowPopup();
                }
            } finally { captureBusy = false; }
            if (text != null) await TranslateAsync(text, kind, false);
        }
        public void ShowPopup()
        {
            if (exiting) return;
            if (NativeEnabled) {
                Popup.ShowActivated = false; Popup.Show(); Native.Position(Popup, Store.Settings);
                if (outsideClick != null) outsideClick.Enable();
            }
        }
        public void HidePopup()
        {
            if (Current != null) {
                Current.Draft = Ui.Get<TextBox>(Popup, "QuestionInput").Text; SaveCurrent();
            }
            if (outsideClick != null) outsideClick.Dispose();
            Popup.Hide();
        }
        public void OpenSettings()
        {
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
        public void ApplySettings(Settings settings, Dictionary<string, string> keys, HashSet<string> deleted)
        {
            var previous = Json.Copy(Store.Settings);
            var previousKeys = new Dictionary<string, string>();
            RegisterShortcut(settings.Shortcut);
            try {
                if (NativeEnabled) {
                    foreach (string id in keys.Keys.Concat(deleted).Distinct()) previousKeys[id] = Credentials.Read(id);
                    foreach (string id in deleted) Credentials.Delete(id);
                    foreach (var key in keys) Credentials.Save(key.Key, key.Value);
                    if (settings.AutoStart != previous.AutoStart) Native.AutoStart(settings.AutoStart);
                }
                Store.SaveSettings(settings);
            } catch {
                RegisterShortcut(previous.Shortcut);
                if (NativeEnabled) {
                    foreach (var key in previousKeys) {
                        try { if (key.Value.Length == 0) Credentials.Delete(key.Key); else Credentials.Save(key.Key, key.Value); } catch { }
                    }
                    try { if (settings.AutoStart != previous.AutoStart) Native.AutoStart(previous.AutoStart); } catch { }
                }
                throw;
            }
            CancelRequests();
            if (!settings.HistoryEnabled) Forget(null);
            if (clipboardMenu != null) { clipboardMenu.Checked = settings.ClipboardMode; tray.Text = "叶译 · " + settings.Shortcut; }
            if (historyWindow != null) historyWindow.Refresh();
        }
        public void OpenRecord(TranslationRecord record)
        {
            CancelRequests(); demo = false; Current = record; DisplayRecord(); ShowPopup();
        }
        public void Forget(string id)
        {
            if (Current != null && (id == null || Current.Id == id)) {
                CancelRequests(); Current = null; DisplayRecord();
            }
        }
        private void SaveCurrent()
        {
            if (Current == null || demo) return;
            try { Store.Save(Current); if (historyWindow != null) historyWindow.Refresh(); }
            catch (UserError error) { ShowError(error.Message, null); }
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
        }
        public void Exit() { exiting = true; Dispose(); Application.Current.Shutdown(); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (Current != null && !demo) {
                Current.Draft = Ui.Get<TextBox>(Popup, "QuestionInput").Text;
                try { Store.Save(Current); } catch { }
            }
            CancelRequests();
            if (outsideClick != null) outsideClick.Dispose();
            if (registeredShortcut != null) { Native.UnregisterHotKey(handle, HotkeyId); registeredShortcut = null; }
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
