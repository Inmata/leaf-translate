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
    public static class Ui
    {
        public static void InitializeTheme()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Leaf.Views.Theme.xaml"))
                Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
        }
        public static Window Load(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Leaf.Views." + name + ".xaml")) {
                var window = (Window)XamlReader.Load(stream);
                using (var icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("Leaf.Assets.Leaf.ico"))
                    window.Icon = BitmapFrame.Create(icon, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                double limit = SystemParameters.WorkArea.Height - 70;
                window.Height = Math.Max(window.MinHeight, Math.Min(window.Height, limit));
                return window;
            }
        }
        // Panel fragments load separately: each XamlReader root owns its own NameScope,
        // so lookups inside a panel must use the panel, not the hosting Window.
        public static FrameworkElement LoadPanel(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Leaf.Views." + name + ".xaml"))
                return (FrameworkElement)XamlReader.Load(stream);
        }
        public static T Get<T>(Window window, string name) where T : class { return (T)window.FindName(name); }
        public static T Get<T>(FrameworkElement scope, string name) where T : class { return (T)scope.FindName(name); }
        public static void Visible(UIElement control, bool visible) { control.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }
        public static Brush Brush(string resource) { return (Brush)Application.Current.Resources[resource]; }
        public static void Click(Window window, string name, Action action) { Get<Button>(window, name).Click += (s, e) => action(); }
        public static void Click(FrameworkElement scope, string name, Action action) { Get<Button>(scope, name).Click += (s, e) => action(); }
        public static void Render(Window window, string path) { Render(window, path, 96); }
        // Layout stays in device-independent units; only the output pixels follow the DPI.
        public static void Render(Window window, string path, double dpi)
        {
            if (dpi <= 0 || double.IsNaN(dpi) || double.IsInfinity(dpi)) throw new ArgumentOutOfRangeException("dpi");
            var element = (FrameworkElement)window.Content;
            element.Measure(new Size(window.Width, window.Height)); element.Arrange(new Rect(0, 0, window.Width, window.Height)); element.UpdateLayout();
            var image = new RenderTargetBitmap((int)Math.Ceiling(window.Width * dpi / 96), (int)Math.Ceiling(window.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, window.Width, window.Height));
            image.Render(background);
            image.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(path)) encoder.Save(output);
        }
    }

    // Tray-window chrome. All settings logic lives in SettingsController so the same
    // panel can be hosted inside the popup shell without duplicating behavior.
    public sealed class SettingsWindow
    {
        private readonly AppShell shell;
        private readonly SettingsController controller;
        private bool allowClose;
        public Window Window { get; private set; }
        public FrameworkElement Panel { get; private set; }

        public SettingsWindow(AppShell owner)
        {
            shell = owner;
            Window = Ui.Load("Settings");
            Panel = Ui.LoadPanel("SettingsPanel");
            Ui.Get<ContentControl>(Window, "PanelHost").Content = Panel;
            controller = new SettingsController(owner, Panel, () => Window.IsVisible);
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

    public sealed class SettingsController
    {
        private readonly AppShell shell;
        private readonly Func<bool> hostVisible;
        private readonly FrameworkElement scope;
        private Settings original;
        private readonly Settings draft;
        private ProviderProfile active;
        private bool loading;
        private readonly Dictionary<string, string> pendingKeys = new Dictionary<string, string>();
        private readonly HashSet<string> deletedKeys = new HashSet<string>();
        private CancellationTokenSource testCancellation;
        private CancellationTokenSource modelCancellation;
        private readonly RequestGate providerGeneration = new RequestGate();
        private bool closed;
        // Monotonic edit markers: a completion may only touch UI state that still belongs to
        // the exact edit it started from, never a newer draft or a different service.
        private long serviceRevision;
        private long preferenceApplySequence;
        private long modelApplySequence;
        private bool fillingProvider, recordingShortcut;
        private string shortcutBeforeRecording;
        private readonly DispatcherTimer modelDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        private readonly DependencyPropertyDescriptor modelTextProperty = DependencyPropertyDescriptor.FromProperty(ComboBox.TextProperty, typeof(ComboBox));
        private EventHandler modelTextChanged;
        private string unassignedKey;
        private Dictionary<string, string> modelOwners = new Dictionary<string, string>();
        private string detailScene;
        private readonly DispatcherTimer preferenceDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        private readonly DispatcherTimer modelApplyDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        // A clicked "apply service configuration" is one attempt with two commits (preferences,
        // then the provider and its key). Flushing and leaving wait for the whole attempt, so a
        // detach cannot drop the key it captured between the phases.
        private ApplyAttempt explicitApply;

        private sealed class ApplyAttempt
        {
            public Task<bool> Completion;
        }

        public SettingsController(AppShell owner, FrameworkElement panel, Func<bool> isHostVisible)
        {
            shell = owner; scope = panel; hostVisible = isHostVisible;
            original = Json.Copy(shell.Store.Settings); draft = Json.Copy(original);
            loading = true;
            Ui.Get<ComboBox>(scope, "ProviderCombo").ItemsSource = draft.Providers;
            Ui.Get<ComboBox>(scope, "ProviderCombo").SelectionChanged += ProviderChanged;
            Ui.Get<ComboBox>(scope, "SceneCombo").ItemsSource = new[] { "通用", "书籍", "影视", "技术文档", "游戏", "自定义" };
            Ui.Get<ComboBox>(scope, "SceneCombo").SelectionChanged += (s, e) => {
                if (!loading) {
                    if (!string.IsNullOrEmpty(detailScene) && detailScene != "通用") draft.SceneDetails[detailScene] = Ui.Get<TextBox>(scope, "SceneDetailInput").Text;
                    detailScene = Convert.ToString(Ui.Get<ComboBox>(scope, "SceneCombo").SelectedItem);
                    string remembered; draft.SceneDetails.TryGetValue(detailScene, out remembered);
                    loading = true; Ui.Get<TextBox>(scope, "SceneDetailInput").Text = remembered ?? ""; loading = false;
                    SchedulePreferences();
                }
                SceneFields();
            };
            Ui.Get<ComboBox>(scope, "GameTypeCombo").ItemsSource = new[] { "自动判断", "角色对话", "物品与技能", "任务与剧情" };
            Ui.Get<ComboBox>(scope, "StyleCombo").ItemsSource = new[] { "自然准确", "尽量直译", "简洁口语" };
            modelDebounce.Tick += async (s, e) => { modelDebounce.Stop(); if (shell.IsExiting) return; await FetchModels(false); };
            Ui.Get<TextBox>(scope, "EndpointInput").TextChanged += (s, e) => {
                if (loading || fillingProvider || closed) return;
                var endpoint = Ui.Get<TextBox>(scope, "EndpointInput"); string typed = endpoint.Text;
                if (active == null) {
                    Ui.Get<ComboBox>(scope, "ProviderCombo").SelectedItem = draft.Providers.First(p => p.Id == "custom");
                    fillingProvider = true; endpoint.Text = typed; fillingProvider = false;
                }
                InvalidateRequests();
                var previous = original.Providers.FirstOrDefault(p => p.Id == active.Id);
                if (previous != null && !string.IsNullOrWhiteSpace(previous.BaseUrl)) {
                    pendingKeys.Remove(active.Id);
                    fillingProvider = true; Ui.Get<PasswordBox>(scope, "ApiKeyInput").Clear(); fillingProvider = false;
                }
                active.BaseUrl = endpoint.Text.Trim(); modelOwners.Clear(); ModelOwnerHint();
                Ui.Get<ComboBox>(scope, "ModelInput").ItemsSource = null;
                serviceRevision++; KeyHint(); ScheduleModels();
            };
            Ui.Get<PasswordBox>(scope, "ApiKeyInput").PasswordChanged += (s, e) => {
                if (loading || fillingProvider || closed) return;
                InvalidateRequests();
                string edited = Ui.Get<PasswordBox>(scope, "ApiKeyInput").Password.Trim();
                if (active == null) unassignedKey = edited;
                else if (edited.Length == 0) pendingKeys.Remove(active.Id);
                else { pendingKeys[active.Id] = edited; deletedKeys.Remove(active.Id); }
                serviceRevision++;
                modelOwners.Clear(); ModelOwnerHint();
                Ui.Get<ComboBox>(scope, "ModelInput").ItemsSource = null;
                KeyHint(); ScheduleModels();
            };
            modelTextChanged = (s, e) => {
                ThinkingFields();
                ModelOwnerHint();
                if (!loading && !fillingProvider) { modelApplyDebounce.Stop(); modelApplyDebounce.Start(); }
                if (!fillingProvider && testCancellation != null) { testCancellation.Cancel(); Status("模型已变化，请重新测试连接。", false); }
            };
            modelTextProperty.AddValueChanged(Ui.Get<ComboBox>(scope, "ModelInput"), modelTextChanged);
            Ui.Get<ComboBox>(scope, "ThinkingModeCombo").SelectionChanged += (s, e) => { serviceRevision++; ThinkingFields(); };
            Ui.Get<ComboBox>(scope, "ReasoningCombo").SelectionChanged += (s, e) => serviceRevision++;
            Ui.Get<TextBox>(scope, "OutputLimitInput").TextChanged += (s, e) => serviceRevision++;
            bool configured = !string.IsNullOrWhiteSpace(draft.Provider.Model) ||
                (shell.NativeEnabled && shell.ReadCredential(draft.Provider).Length > 0);
            if (configured) Ui.Get<ComboBox>(scope, "ProviderCombo").SelectedItem = draft.Provider;
            Ui.Visible(Ui.Get<TextBlock>(scope, "ProviderPlaceholder"), !configured);
            Ui.Get<ComboBox>(scope, "ModelInput").IsEnabled = configured;
            PopulatePreferences();
            Ui.Get<CheckBox>(scope, "ClipboardModeCheck").IsChecked = draft.ClipboardMode;
            Ui.Get<CheckBox>(scope, "FocusInputCheck").IsChecked = draft.FocusInputOnShortcut;
            Ui.Get<TextBox>(scope, "ShortcutInput").Text = draft.Shortcut;
            var shortcut = Ui.Get<TextBox>(scope, "ShortcutInput");
            shortcut.GotKeyboardFocus += (s, e) => {
                if (recordingShortcut) return;
                recordingShortcut = true; shortcutBeforeRecording = shortcut.Text;
                shell.SetShortcutRecording(true); shortcut.Text = "请按下快捷键…";
                Ui.Get<TextBlock>(scope, "ShortcutHint").Text = "按下 Ctrl、Alt 或 Win 与另一个按键；Esc 取消。";
            };
            shortcut.LostKeyboardFocus += (s, e) => EndShortcutRecording(false);
            shortcut.PreviewKeyDown += (s, e) => {
                e.Handled = true; if (!recordingShortcut || e.IsRepeat) return;
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Escape) { EndShortcutRecording(false); shortcut.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); return; }
                try {
                    string recorded = HotkeySpec.Record(key, Keyboard.Modifiers);
                    shortcut.Text = recorded; EndShortcutRecording(true);
                    shortcut.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                } catch (UserError error) { Ui.Get<TextBlock>(scope, "ShortcutHint").Text = error.Message; }
            };
            Ui.Get<CheckBox>(scope, "AutoStartCheck").IsChecked = draft.AutoStart;
            Ui.Get<CheckBox>(scope, "HistoryCheck").IsChecked = draft.HistoryEnabled;
            Ui.Get<TextBox>(scope, "HistoryLimitInput").Text = draft.HistoryLimit.ToString();
            DrawPresets(); loading = false; SceneFields(); KeyHint();
            Ui.Get<ComboBox>(scope, "PresetCombo").SelectionChanged += (s, e) => PresetStateChanged();
            Ui.Click(scope, "AddCustom", AddCustom);
            Ui.Click(scope, "SavePreset", SavePreset);
            Ui.Click(scope, "LoadPreset", LoadPreset);
            Ui.Click(scope, "RemovePreset", () => {
                var preset = Ui.Get<ComboBox>(scope, "PresetCombo").SelectedItem as LearningPreset;
                if (preset != null) { draft.Presets.Remove(preset); DrawPresets(); SchedulePreferences(); }
            });
            Ui.Click(scope, "DeleteKey", () => {
                if (active == null) return;
                serviceRevision++;
                deletedKeys.Add(active.Id); pendingKeys.Remove(active.Id);
                Ui.Get<PasswordBox>(scope, "ApiKeyInput").Clear(); KeyHint(); Status("应用服务配置后删除密钥。", false);
                InvalidateRequests(); ModelStatus("密钥待删除，已停止获取模型。", false);
            });
            Ui.Click(scope, "TestConnection", async () => await TestConnection());
            Ui.Click(scope, "FetchModels", async () => await FetchModels(true));
            Ui.Click(scope, "SaveSettingsButton", Save);
            preferenceDebounce.Tick += async (s, e) => { preferenceDebounce.Stop(); await ApplyPreferencesAsync(); };
            modelApplyDebounce.Tick += async (s, e) => { modelApplyDebounce.Stop(); await AutoApplyModelAsync(); };
            foreach (string name in new[] { "TargetInput", "SceneDetailInput", "HistoryLimitInput" })
                Ui.Get<TextBox>(scope, name).TextChanged += (s, e) => SchedulePreferences();
            foreach (string name in new[] { "GameTypeCombo", "StyleCombo" })
                Ui.Get<ComboBox>(scope, name).SelectionChanged += (s, e) => SchedulePreferences();
            foreach (string name in new[] { "ClipboardModeCheck", "FocusInputCheck", "AutoStartCheck", "HistoryCheck" }) {
                Ui.Get<CheckBox>(scope, name).Checked += (s, e) => SchedulePreferences();
                Ui.Get<CheckBox>(scope, name).Unchecked += (s, e) => SchedulePreferences();
            }
            var password = Ui.Get<PasswordBox>(scope, "ApiKeyInput");
            password.GotKeyboardFocus += (s, e) => KeyHint(); password.LostKeyboardFocus += (s, e) => KeyHint();
        }
        // The host became visible again (or just appeared): auto-load the model list once.
        public void Activate()
        {
            if (closed) return;
            ScheduleModels();
        }
        // The host lost activation: stop any in-progress shortcut recording.
        public void OnHostDeactivated() { EndShortcutRecording(false); }
        // The host is going away for good: stop everything and drop transient secrets.
        public void Detach()
        {
            if (closed) return;
            closed = true; providerGeneration.Next();
            modelDebounce.Stop(); EndShortcutRecording(false);
            preferenceDebounce.Stop(); modelApplyDebounce.Stop();
            modelTextProperty.RemoveValueChanged(Ui.Get<ComboBox>(scope, "ModelInput"), modelTextChanged);
            if (testCancellation != null) testCancellation.Cancel();
            if (modelCancellation != null) modelCancellation.Cancel();
            Ui.Get<PasswordBox>(scope, "ApiKeyInput").Clear(); pendingKeys.Clear();
            unassignedKey = "";
        }
        private void EndShortcutRecording(bool accept)
        {
            if (!recordingShortcut) return;
            if (!accept) Ui.Get<TextBox>(scope, "ShortcutInput").Text = shortcutBeforeRecording;
            recordingShortcut = false; shell.SetShortcutRecording(false);
            Ui.Get<TextBlock>(scope, "ShortcutHint").Text = accept ? "已录入，正在应用…" : "点击上方，再按下组合键；Esc 取消。";
            if (accept) SchedulePreferences();
        }
        private void ProviderChanged(object sender, SelectionChangedEventArgs args)
        {
            // The editable model ComboBox also bubbles SelectionChanged.
            if (args.OriginalSource != Ui.Get<ComboBox>(scope, "ProviderCombo")) return;
            InvalidateRequests();
            if (active != null) CaptureProfile();
            fillingProvider = true;
            active = Ui.Get<ComboBox>(scope, "ProviderCombo").SelectedItem as ProviderProfile;
            if (active == null) { fillingProvider = false; return; }
            draft.ProviderId = active.Id;
            Ui.Visible(Ui.Get<TextBlock>(scope, "ProviderPlaceholder"), false);
            Ui.Get<ComboBox>(scope, "ModelInput").IsEnabled = true;
            modelOwners.Clear(); ModelOwnerHint();
            Ui.Get<ComboBox>(scope, "ModelInput").ItemsSource = null;
            Ui.Get<ComboBox>(scope, "ModelInput").Text = active.Model;
            SelectTag("ThinkingModeCombo", active.ThinkingMode ?? "auto");
            SelectTag("ReasoningCombo", active.ReasoningEffort ?? "low");
            Ui.Get<TextBox>(scope, "OutputLimitInput").Text = active.MaxOutputTokens == 0 ? "" : active.MaxOutputTokens.ToString();
            Ui.Get<TextBox>(scope, "EndpointInput").Text = active.BaseUrl;
            string pending;
            if (!string.IsNullOrEmpty(unassignedKey)) { pendingKeys[active.Id] = unassignedKey; unassignedKey = ""; }
            Ui.Get<PasswordBox>(scope, "ApiKeyInput").Password = pendingKeys.TryGetValue(active.Id, out pending) ? pending : "";
            Ui.Get<Expander>(scope, "EndpointExpander").IsExpanded = active.Id == "custom";
            // A custom service needs its address field reachable: open the advanced group too.
            if (active.Id == "custom") Ui.Get<Expander>(scope, "AdvancedExpander").IsExpanded = true;
            fillingProvider = false; KeyHint(); ThinkingFields(); ScheduleModels();
            if (!loading) {
                serviceRevision++;
                Status("已切换服务。已配置的模型自动应用；新密钥和接口需应用服务配置。", false);
                modelApplyDebounce.Stop(); modelApplyDebounce.Start();
            }
        }
        private void CaptureProfile()
        {
            if (active == null) return;
            active.Model = Ui.Get<ComboBox>(scope, "ModelInput").Text.Trim();
            active.BaseUrl = Ui.Get<TextBox>(scope, "EndpointInput").Text.Trim();
            active.ThinkingMode = SelectedTag("ThinkingModeCombo", "auto");
            active.ReasoningEffort = SelectedTag("ReasoningCombo", "low");
            int budget; int.TryParse(Ui.Get<TextBox>(scope, "OutputLimitInput").Text.Trim(), out budget); active.MaxOutputTokens = budget;
            string key = Ui.Get<PasswordBox>(scope, "ApiKeyInput").Password.Trim();
            if (key.Length > 0) { pendingKeys[active.Id] = key; deletedKeys.Remove(active.Id); }
        }
        private string SelectedTag(string name, string fallback)
        {
            var item = Ui.Get<ComboBox>(scope, name).SelectedItem as ComboBoxItem;
            return item == null ? fallback : Convert.ToString(item.Tag);
        }
        private void SelectTag(string name, string value)
        {
            var input = Ui.Get<ComboBox>(scope, name);
            input.SelectedItem = input.Items.Cast<ComboBoxItem>().FirstOrDefault(x => Convert.ToString(x.Tag) == value) ?? input.Items[0];
        }
        private void ThinkingFields()
        {
            if (active == null || fillingProvider) return;
            bool defaultsOnly = active.Id == "openai" || active.Id == "custom";
            Ui.Get<ComboBox>(scope, "ThinkingModeCombo").IsEnabled = !defaultsOnly;
            if (defaultsOnly) SelectTag("ThinkingModeCombo", "auto");
            bool required = LlmClient.IsGlm53(new ProviderProfile { Id = active.Id, Model = Ui.Get<ComboBox>(scope, "ModelInput").Text });
            Ui.Get<ComboBoxItem>(scope, "DisableThinkingOption").IsEnabled = !required;
            if (required && SelectedTag("ThinkingModeCombo", "auto") == "disabled") SelectTag("ThinkingModeCombo", "auto");
            Ui.Visible(Ui.Get<StackPanel>(scope, "ReasoningPanel"), required);
            Ui.Get<TextBlock>(scope, "ThinkingHint").Text = defaultsOnly ?
                "此服务使用模型默认思考设置。不同模型参数不同，可调整下方输出上限。" : required ?
                "此模型必须开启思考。自动使用轻量思考，不能关闭。" :
                "自动按已知模型适配；未知 GLM 模型沿用服务默认。手动选择需模型支持。";
        }
        private void KeyHint()
        {
            var input = Ui.Get<PasswordBox>(scope, "ApiKeyInput");
            var placeholder = Ui.Get<TextBlock>(scope, "KeyPlaceholder");
            bool saved = active != null && !deletedKeys.Contains(active.Id) && shell.NativeEnabled &&
                original.Providers.Any(p => p.Id == active.Id && p.BaseUrl.TrimEnd('/') == Ui.Get<TextBox>(scope, "EndpointInput").Text.TrimEnd('/')) && shell.ReadCredential(active).Length > 0;
            // The saved key is shown as eight fixed dots - a placeholder, never the real
            // key's length. Focusing or typing hides it; the genuine caret takes over.
            bool empty = input.Password.Length == 0;
            bool dots = saved && empty && !input.IsKeyboardFocused;
            Ui.Visible(Ui.Get<StackPanel>(scope, "KeyDots"), dots);
            Ui.Visible(placeholder, empty && !dots && !input.IsKeyboardFocused);
            if (active == null) {
                Ui.Get<TextBlock>(scope, "KeyHint").Text = "先选择服务，密钥与模型随服务一起切换。"; return;
            }
            string url = Ui.Get<TextBox>(scope, "EndpointInput").Text.TrimEnd('/');
            var previous = original.Providers.FirstOrDefault(p => p.Id == active.Id);
            string hint;
            if (!empty) hint = "已填写，待「应用服务配置」生效。";
            else if (input.IsKeyboardFocused) hint = saved ? "留空保留已保存密钥。" : "输入此服务的 API Key。";
            else if (previous == null || url != previous.BaseUrl.TrimEnd('/')) hint = "接口地址已变化，请重新填写密钥。";
            else if (deletedKeys.Contains(active.Id)) hint = "应用服务配置后删除已存密钥。";
            else if (saved) hint = "密钥已保存。";
            else hint = "保存在 Windows 凭据管理器。";
            Ui.Get<TextBlock>(scope, "KeyHint").Text = hint;
            System.Windows.Automation.AutomationProperties.SetHelpText(input, hint);
        }
        private void PopulatePreferences()
        {
            loading = true;
            Ui.Get<TextBox>(scope, "TargetInput").Text = draft.TargetLanguage;
            Ui.Get<ComboBox>(scope, "SceneCombo").SelectedItem = draft.Scene;
            detailScene = draft.Scene;
            Ui.Get<TextBox>(scope, "SceneDetailInput").Text = draft.SceneDetail ?? "";
            if (draft.Scene != "通用") draft.SceneDetails[draft.Scene] = draft.SceneDetail ?? "";
            Ui.Get<ComboBox>(scope, "GameTypeCombo").SelectedItem = draft.GameTextType ?? "自动判断";
            Ui.Get<ComboBox>(scope, "StyleCombo").SelectedItem = draft.Style;
            DrawLearning(); loading = false; SceneFields();
        }
        private void SceneFields()
        {
            string scene = Convert.ToString(Ui.Get<ComboBox>(scope, "SceneCombo").SelectedItem);
            Ui.Visible(Ui.Get<StackPanel>(scope, "SceneDetailPanel"), scene != "通用" && scene.Length > 0);
            Ui.Visible(Ui.Get<StackPanel>(scope, "GameTypePanel"), scene == "游戏");
            Ui.Get<TextBlock>(scope, "SceneDetailLabel").Text =
                scene == "游戏" ? "游戏名称（选填）" : scene == "书籍" ? "书名（选填）" :
                scene == "影视" ? "影视名称（选填）" : scene == "技术文档" ? "技术领域（选填）" : "当前语境（选填）";
        }
        private void DrawLearning()
        {
            var panel = Ui.Get<WrapPanel>(scope, "LearningChoices"); panel.Children.Clear();
            foreach (var option in draft.Learning) {
                var captured = option;
                var check = new CheckBox { Content = option.Name, IsChecked = option.Enabled, ToolTip = option.Instruction };
                check.Checked += (s, e) => { captured.Enabled = true; SchedulePreferences(); };
                check.Unchecked += (s, e) => { captured.Enabled = false; SchedulePreferences(); };
                if (option.Custom) {
                    var group = new StackPanel { Orientation = Orientation.Horizontal };
                    check.Margin = new Thickness(0, 0, 2, 12); group.Children.Add(check);
                    var remove = new Button { Content = "×", Style = (Style)Application.Current.Resources["GhostButton"], Margin = new Thickness(0, -4, 15, 8), ToolTip = "删除此学习项" };
                    remove.Click += (s, e) => { draft.Learning.Remove(captured); DrawLearning(); SchedulePreferences(); };
                    group.Children.Add(remove); panel.Children.Add(group);
                } else panel.Children.Add(check);
            }
        }
        private void AddCustom()
        {
            string name = Ui.Get<TextBox>(scope, "CustomNameInput").Text.Trim();
            string instruction = Ui.Get<TextBox>(scope, "CustomInstructionInput").Text.Trim();
            if (name.Length == 0 || instruction.Length == 0) { Status("请填写名称和讲解方式。", true); return; }
            if (draft.Learning.Count >= 15) { Status("最多保留 15 个学习选项。", true); return; }
            if (draft.Learning.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { Status("这个选项名称已经存在。", true); return; }
            draft.Learning.Add(new LearningOption { Id = Guid.NewGuid().ToString("N"), Name = name, Instruction = instruction, Enabled = true, Custom = true });
            DrawLearning(); Ui.Get<TextBox>(scope, "CustomNameInput").Clear(); Ui.Get<TextBox>(scope, "CustomInstructionInput").Clear();
            SchedulePreferences();
        }
        private void CapturePreferences()
        {
            draft.TargetLanguage = Ui.Get<TextBox>(scope, "TargetInput").Text.Trim();
            draft.Scene = Convert.ToString(Ui.Get<ComboBox>(scope, "SceneCombo").SelectedItem);
            draft.SceneDetail = draft.Scene == "通用" ? "" : Ui.Get<TextBox>(scope, "SceneDetailInput").Text.Trim();
            if (draft.Scene != "通用") draft.SceneDetails[draft.Scene] = draft.SceneDetail;
            draft.GameTextType = Convert.ToString(Ui.Get<ComboBox>(scope, "GameTypeCombo").SelectedItem);
            draft.Style = Convert.ToString(Ui.Get<ComboBox>(scope, "StyleCombo").SelectedItem);
        }
        private void DrawPresets()
        {
            var combo = Ui.Get<ComboBox>(scope, "PresetCombo");
            combo.ItemsSource = null;
            combo.ItemsSource = draft.Presets;
            if (draft.Presets.Count > 0) combo.SelectedIndex = 0;
            PresetStateChanged();
        }
        // Empty list: disable the dropdown and its actions and let the placeholder carry
        // the message; with presets present the placeholder only covers a missing selection.
        private void PresetStateChanged()
        {
            var combo = Ui.Get<ComboBox>(scope, "PresetCombo");
            bool selected = combo.SelectedItem as LearningPreset != null;
            var placeholder = Ui.Get<TextBlock>(scope, "PresetPlaceholder");
            placeholder.Text = draft.Presets.Count == 0 ? "暂无偏好预设" : "未选择预设";
            Ui.Visible(placeholder, !selected);
            combo.IsEnabled = draft.Presets.Count > 0;
            Ui.Get<Button>(scope, "LoadPreset").IsEnabled = selected;
            Ui.Get<Button>(scope, "RemovePreset").IsEnabled = selected;
        }
        private void SavePreset()
        {
            string name = Ui.Get<TextBox>(scope, "PresetNameInput").Text.Trim();
            if (name.Length == 0) { Status("给这组偏好起个名称。", true); return; }
            CapturePreferences();
            draft.Presets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (draft.Presets.Count >= 10) { Status("最多保存 10 组偏好预设。", true); return; }
            draft.Presets.Add(new LearningPreset {
                Name = name, TargetLanguage = draft.TargetLanguage, Scene = draft.Scene, SceneDetail = draft.SceneDetail,
                GameTextType = draft.GameTextType, Style = draft.Style, Learning = Json.Copy(draft.Learning)
            });
            DrawPresets(); SchedulePreferences();
        }
        private void LoadPreset()
        {
            var preset = Ui.Get<ComboBox>(scope, "PresetCombo").SelectedItem as LearningPreset;
            if (preset == null) return;
            draft.TargetLanguage = preset.TargetLanguage; draft.Scene = preset.Scene; draft.SceneDetail = preset.SceneDetail;
            draft.GameTextType = preset.GameTextType; draft.Style = preset.Style; draft.Learning = Json.Copy(preset.Learning);
            PopulatePreferences(); SchedulePreferences();
        }
        public void ReportStatus(string text, bool error) { Status(text, error); }
        private void Status(string text, bool error)
        {
            var label = Ui.Get<TextBlock>(scope, "SettingsStatus"); label.Text = text;
            label.Foreground = error ? new SolidColorBrush(Color.FromRgb(147, 94, 67)) : Ui.Brush("Muted");
        }
        private void SchedulePreferences()
        {
            if (loading || closed || recordingShortcut || shell.IsExiting) return;
            Status("正在应用…", false); preferenceDebounce.Stop(); preferenceDebounce.Start();
        }
        // Stops debounced patches and applies the latest draft; used by Closing and ExitAsync.
        // A clicked service apply still committing is part of the flush: leaving must wait for
        // it, and its failure fails the flush so the host stays open with the reported status.
        public async Task<bool> FlushPendingAsync()
        {
            if (closed) return true;
            bool ok = true;
            if (preferenceDebounce.IsEnabled) { preferenceDebounce.Stop(); if (!await ApplyPreferencesAsync()) ok = false; }
            if (modelApplyDebounce.IsEnabled) { modelApplyDebounce.Stop(); if (!await AutoApplyModelAsync()) ok = false; }
            while (true) {
                var attempt = explicitApply;
                if (attempt == null) break;
                if (!await attempt.Completion) ok = false;
            }
            return ok;
        }
        private Task<bool> ApplyPreferencesAsync() { return ApplyPreferencesAsync(false); }
        private async Task<bool> ApplyPreferencesAsync(bool explicitApply)
        {
            long version = ++preferenceApplySequence;
            try {
                CapturePreferences();
                if (string.IsNullOrWhiteSpace(draft.TargetLanguage)) throw new UserError("target", "目标语言不能为空，尚未应用。" );
                int limit;
                if (!int.TryParse(Ui.Get<TextBox>(scope, "HistoryLimitInput").Text, out limit) || limit < 20 || limit > 1000)
                    throw new UserError("history", "历史条数应为 20 到 1000，尚未应用。" );
                draft.ClipboardMode = Ui.Get<CheckBox>(scope, "ClipboardModeCheck").IsChecked == true;
                draft.FocusInputOnShortcut = Ui.Get<CheckBox>(scope, "FocusInputCheck").IsChecked == true;
                draft.Shortcut = Ui.Get<TextBox>(scope, "ShortcutInput").Text.Trim(); HotkeySpec.Parse(draft.Shortcut);
                draft.AutoStart = Ui.Get<CheckBox>(scope, "AutoStartCheck").IsChecked == true;
                draft.HistoryEnabled = Ui.Get<CheckBox>(scope, "HistoryCheck").IsChecked == true; draft.HistoryLimit = limit;
                await shell.ApplySettingsAsync(SettingsUpdate.Preferences(draft), new Dictionary<string, string>(), new HashSet<string>(), explicitApply);
                if (version == preferenceApplySequence) {
                    Status("已应用并保存 · 下次翻译使用新偏好", false);
                    Ui.Get<TextBlock>(scope, "ShortcutHint").Text = "点击上方，再按下组合键；Esc 取消。";
                }
                return true;
            } catch (Exception error) {
                shell.Log.Event("preferences_save_failed", error);
                if (version == preferenceApplySequence)
                    Status(error is UserError ? error.Message : "应用失败，请检查配置后重试。", true);
                return false;
            }
        }
        private async Task<bool> AutoApplyModelAsync()
        {
            if (closed || loading || fillingProvider || shell.IsExiting || active == null ||
                pendingKeys.ContainsKey(active.Id) || deletedKeys.Contains(active.Id)) return true;
            var committed = shell.Store.Settings.Providers.FirstOrDefault(p => p.Id == active.Id);
            if (committed == null || committed.BaseUrl.TrimEnd('/') != Ui.Get<TextBox>(scope, "EndpointInput").Text.TrimEnd('/') || string.IsNullOrWhiteSpace(committed.Model)) return true;
            if (shell.NativeEnabled && string.IsNullOrEmpty(ActiveKey())) return true;
            CaptureProfile();
            if (string.IsNullOrWhiteSpace(active.Model)) return true;
            var profile = Json.Copy(active);
            long version = ++modelApplySequence;
            try {
                // Advanced parameters have their own explicit apply action.
                await shell.ApplySettingsAsync(SettingsUpdate.Model(profile.Id, profile.Model), new Dictionary<string, string>(), new HashSet<string>());
                if (version == modelApplySequence && active != null && active.Id == profile.Id)
                    original = Json.Copy(shell.Store.Settings);
                if (version == modelApplySequence) Status("已切换模型并保存", false);
                return true;
            } catch (Exception error) {
                shell.Log.Event("model_apply_failed", error);
                if (version == modelApplySequence) Status(error is UserError ? error.Message : "模型切换失败。", true);
                return false;
            }
        }
        private void InvalidateRequests()
        {
            modelDebounce.Stop();
            providerGeneration.Next();
            if (testCancellation != null) testCancellation.Cancel();
            if (modelCancellation != null) modelCancellation.Cancel();
            Ui.Get<Button>(scope, "TestConnection").IsEnabled = true;
            Ui.Get<Button>(scope, "FetchModels").IsEnabled = true;
        }
        private string ActiveKey()
        {
            if (active == null) return unassignedKey;
            string key = Ui.Get<PasswordBox>(scope, "ApiKeyInput").Password.Trim();
            var previous = original.Providers.FirstOrDefault(p => p.Id == active.Id);
            if (string.IsNullOrEmpty(key) && !deletedKeys.Contains(active.Id) && previous != null &&
                previous.BaseUrl.TrimEnd('/') == active.BaseUrl.TrimEnd('/') && shell.NativeEnabled) key = shell.ReadCredential(active);
            return key;
        }
        private void ModelStatus(string text, bool error)
        {
            var label = Ui.Get<TextBlock>(scope, "ModelStatus"); label.Text = text;
            label.Foreground = error ? new SolidColorBrush(Color.FromRgb(147, 94, 67)) : Ui.Brush("Muted");
        }
        private void ScheduleModels()
        {
            if (loading || fillingProvider || closed || shell.IsExiting) return;
            if (active == null) { ModelStatus("选择服务或填写接口地址后，自动加载模型。", false); return; }
            active.BaseUrl = Ui.Get<TextBox>(scope, "EndpointInput").Text.Trim();
            try { LlmClient.Endpoint(active.BaseUrl); }
            catch (UserError) { ModelStatus("填写有效的接口地址后，自动加载模型。", false); return; }
            if (string.IsNullOrWhiteSpace(ActiveKey())) { ModelStatus("填入密钥后自动获取，也可直接填写模型 ID。", false); return; }
            ModelStatus("准备获取模型…", false); modelDebounce.Stop(); modelDebounce.Start();
        }
        private async Task FetchModels(bool manual)
        {
            if (active == null) { ModelStatus("请先选择服务或填写接口地址。", true); return; }
            CaptureProfile(); InvalidateRequests();
            long version = providerGeneration.Next();
            var profile = Json.Copy(active);
            var cancellation = new CancellationTokenSource(); modelCancellation = cancellation;
            Ui.Get<Button>(scope, "FetchModels").IsEnabled = false;
            try {
                ModelStatus("正在获取模型…", false);
                var catalog = await shell.Client.ListCatalogAsync(profile, ActiveKey(), cancellation.Token);
                if (closed || !providerGeneration.IsCurrent(version)) return;
                var models = catalog.Models; modelOwners = catalog.Owners;
                var input = Ui.Get<ComboBox>(scope, "ModelInput");
                string typed = input.Text;
                input.ItemsSource = models; input.Text = typed;
                ModelOwnerHint();
                ModelStatus("已获取 " + models.Count + " 个模型。" + (typed.Length > 0 && !models.Contains(typed) ?
                    "当前 ID 未在列表中，请核对。" : "请选择支持文字对话的模型。"), false);
                if (manual && hostVisible()) input.IsDropDownOpen = true;
            } catch (OperationCanceledException) { }
            catch (Exception error) {
                shell.Log.Event("model_discovery_failed", error);
                if (!closed && providerGeneration.IsCurrent(version)) {
                    ModelStatus(error is UserError ? error.Message : "获取失败，可直接填写模型 ID。", true);
                    // The model row lives in the collapsed advanced group; the page keeps one
                    // short visible status so a discovery failure is never silently swallowed.
                    Status("模型列表获取失败，可直接填写模型 ID。", true);
                }
            } finally {
                if (modelCancellation == cancellation) modelCancellation = null;
                if (!closed && providerGeneration.IsCurrent(version)) Ui.Get<Button>(scope, "FetchModels").IsEnabled = true;
                cancellation.Dispose();
            }
        }
        private void ModelOwnerHint()
        {
            string owner;
            var hint = Ui.Get<TextBlock>(scope, "ModelVendorHint");
            bool supplied = modelOwners.TryGetValue(Ui.Get<ComboBox>(scope, "ModelInput").Text ?? "", out owner);
            hint.Text = supplied ? "接口提供的模型归属：" + owner : ""; Ui.Visible(hint, supplied);
        }
        private async Task TestConnection()
        {
            if (active == null) { Status("请先选择 API 服务或填写接口地址。", true); return; }
            CaptureProfile(); InvalidateRequests();
            long version = providerGeneration.Next();
            var profile = Json.Copy(active);
            var button = Ui.Get<Button>(scope, "TestConnection"); button.IsEnabled = false;
            var cancellation = new CancellationTokenSource(); testCancellation = cancellation;
            try {
                string key = ActiveKey();
                Status("正在发送短请求…", false);
                string result = await shell.Client.CompleteAsync(profile, key, new List<ChatTurn> {
                    new ChatTurn { Role = "user", Content = "只回答 OK。" }
                }, false, false, null, cancellation.Token);
                if (!closed && !cancellation.IsCancellationRequested && providerGeneration.IsCurrent(version)) Status("连接成功。可继续编辑或应用服务配置。", false);
            } catch (OperationCanceledException) { }
            catch (Exception error) {
                shell.Log.Event("connection_test_failed", error);
                if (!closed && providerGeneration.IsCurrent(version)) Status(error is UserError ? error.Message : "测试失败，请检查网络和配置。", true);
            } finally {
                if (testCancellation == cancellation) testCancellation = null;
                if (!closed && providerGeneration.IsCurrent(version)) button.IsEnabled = true;
                cancellation.Dispose();
            }
        }
        private void Save()
        {
            if (explicitApply != null) return;
            var attempt = new ApplyAttempt();
            explicitApply = attempt;
            attempt.Completion = RunExplicitApplyAsync(attempt);
        }
        private async Task<bool> RunExplicitApplyAsync(ApplyAttempt attempt)
        {
            try { return await SaveCoreAsync(); }
            finally { if (ReferenceEquals(explicitApply, attempt)) explicitApply = null; }
        }
        // Returns whether the clicked configuration was committed. A failure reports its own
        // status and keeps the host open instead of leaving on a discarded draft.
        private async Task<bool> SaveCoreAsync()
        {
            var button = Ui.Get<Button>(scope, "SaveSettingsButton");
            if (!button.IsEnabled) return true;
            button.IsEnabled = false;
            try {
                EndShortcutRecording(false); InvalidateRequests();
                preferenceDebounce.Stop(); modelApplyDebounce.Stop();
                if (active == null) throw new UserError("endpoint", "请先选择 API 服务或填写接口地址。");
                CaptureProfile();
                // Bind the transaction to the exact captured profile and edit revision so a
                // provider/key/model change during the awaits cannot be overwritten or cleared.
                var profile = Json.Copy(active);
                string profileId = profile.Id;
                long revision = serviceRevision;
                string limitText = Ui.Get<TextBox>(scope, "OutputLimitInput").Text.Trim(); int outputLimit;
                if (limitText.Length > 0 && !int.TryParse(limitText, out outputLimit)) throw new UserError("parameter", "输出上限请填写整数，或留空自动适配。");
                LlmClient.Endpoint(profile.BaseUrl); LlmClient.ValidateProfile(profile);
                if (string.IsNullOrWhiteSpace(profile.Model)) throw new UserError("model", "请选择或填写当前服务的模型 ID。");
                var previous = original.Providers.FirstOrDefault(p => p.Id == profileId);
                if (previous != null && previous.BaseUrl.TrimEnd('/') != profile.BaseUrl.TrimEnd('/') && !pendingKeys.ContainsKey(profileId))
                    throw new UserError("key", "修改接口地址后，请为该服务重新填写密钥。");
                if (!await ApplyPreferencesAsync(true)) return false;
                var keys = new Dictionary<string, string>(); string pending;
                if (pendingKeys.TryGetValue(profileId, out pending)) keys[profileId] = pending;
                var deleted = new HashSet<string>(); if (deletedKeys.Contains(profileId)) deleted.Add(profileId);
                await shell.ApplySettingsAsync(SettingsUpdate.Provider(profile), keys, deleted, true);
                original = Json.Copy(shell.Store.Settings);
                bool pendingMatchesSaved = !pendingKeys.ContainsKey(profileId) ||
                    (keys.ContainsKey(profileId) && pendingKeys[profileId] == keys[profileId]);
                if (pendingMatchesSaved) pendingKeys.Remove(profileId);
                if (deletedKeys.Contains(profileId) && !pendingKeys.ContainsKey(profileId)) deletedKeys.Remove(profileId);
                if (revision == serviceRevision && active != null && active.Id == profileId) {
                    fillingProvider = true; Ui.Get<PasswordBox>(scope, "ApiKeyInput").Clear(); fillingProvider = false;
                    KeyHint(); Status("服务配置已应用并保存", false);
                } else {
                    KeyHint(); Status("已保存「" + profile.Name + "」的服务配置；当前编辑的改动仍待应用。", false);
                }
                return true;
            } catch (Exception error) { shell.Log.Event("settings_save_failed", error); Status(error is UserError ? error.Message : "保存失败，请检查配置后重试。", true); return false; }
            finally { button.IsEnabled = true; }
        }
    }

    public sealed class HistoryWindow
    {
        private readonly AppShell shell;
        public Window Window { get; private set; }
        public HistoryWindow(AppShell owner)
        {
            shell = owner; Window = Ui.Load("History");
            Ui.Get<TextBox>(Window, "HistorySearch").TextChanged += (s, e) => Refresh();
            Ui.Get<ListBox>(Window, "HistoryList").MouseDoubleClick += (s, e) => Open();
            Ui.Click(Window, "OpenHistory", Open);
            Ui.Click(Window, "DeleteHistory", DeleteSelectedAsync);
            Ui.Click(Window, "ClearHistory", ClearAllAsync);
            Refresh();
        }
        private async void DeleteSelectedAsync()
        {
            var record = Ui.Get<ListBox>(Window, "HistoryList").SelectedItem as TranslationRecord;
            if (record == null) return;
            try {
                await shell.Store.DeleteAsync(record.Id);
                shell.Forget(record.Id); Refresh();
            } catch (Exception error) {
                MessageBox.Show(Window, error is UserError ? error.Message : "无法更新本地历史。", "Leaf");
            }
        }
        private async void ClearAllAsync()
        {
            if (MessageBox.Show(Window, "清空全部本地历史？", "Leaf", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try {
                await shell.Store.ClearAsync();
                shell.Forget(null); Refresh();
            } catch (Exception error) {
                MessageBox.Show(Window, error is UserError ? error.Message : "无法更新本地历史。", "Leaf");
            }
        }
        public void Refresh()
        {
            var items = shell.Store.History(Ui.Get<TextBox>(Window, "HistorySearch").Text);
            Ui.Get<ListBox>(Window, "HistoryList").ItemsSource = items;
            if (items.Count > 0) Ui.Get<ListBox>(Window, "HistoryList").SelectedIndex = 0;
            Ui.Get<TextBlock>(Window, "HistoryCount").Text = "最近 " + items.Count + " 条";
            Ui.Visible(Ui.Get<TextBlock>(Window, "HistoryEmpty"), items.Count == 0);
            Ui.Get<Button>(Window, "OpenHistory").IsEnabled = items.Count > 0;
            Ui.Get<Button>(Window, "DeleteHistory").IsEnabled = items.Count > 0;
        }
        private void Open()
        {
            var record = Ui.Get<ListBox>(Window, "HistoryList").SelectedItem as TranslationRecord;
            if (record != null) { shell.OpenRecord(record); Window.Close(); }
        }
    }
}
