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
        public static T Get<T>(Window window, string name) where T : class { return (T)window.FindName(name); }
        public static void Visible(UIElement control, bool visible) { control.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }
        public static Brush Brush(string resource) { return (Brush)Application.Current.Resources[resource]; }
        public static void Click(Window window, string name, Action action) { Get<Button>(window, name).Click += (s, e) => action(); }
        public static void Render(Window window, string path)
        {
            var element = (FrameworkElement)window.Content;
            element.Measure(new Size(window.Width, window.Height)); element.Arrange(new Rect(0, 0, window.Width, window.Height)); element.UpdateLayout();
            var image = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, window.Width, window.Height));
            image.Render(background);
            image.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(path)) encoder.Save(output);
        }
    }

    public sealed class SettingsWindow
    {
        private readonly AppShell shell;
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
        public Window Window { get; private set; }

        public SettingsWindow(AppShell owner)
        {
            shell = owner; original = Json.Copy(shell.Store.Settings); draft = Json.Copy(original);
            Window = Ui.Load("Settings"); loading = true;
            Ui.Get<ComboBox>(Window, "ProviderCombo").ItemsSource = draft.Providers;
            Ui.Get<ComboBox>(Window, "ProviderCombo").SelectionChanged += ProviderChanged;
            Ui.Get<ComboBox>(Window, "SceneCombo").ItemsSource = new[] { "通用", "书籍", "影视", "技术文档", "游戏", "自定义" };
            Ui.Get<ComboBox>(Window, "SceneCombo").SelectionChanged += (s, e) => {
                if (!loading) {
                    if (!string.IsNullOrEmpty(detailScene) && detailScene != "通用") draft.SceneDetails[detailScene] = Ui.Get<TextBox>(Window, "SceneDetailInput").Text;
                    detailScene = Convert.ToString(Ui.Get<ComboBox>(Window, "SceneCombo").SelectedItem);
                    string remembered; draft.SceneDetails.TryGetValue(detailScene, out remembered);
                    loading = true; Ui.Get<TextBox>(Window, "SceneDetailInput").Text = remembered ?? ""; loading = false;
                    SchedulePreferences();
                }
                SceneFields();
            };
            Ui.Get<ComboBox>(Window, "GameTypeCombo").ItemsSource = new[] { "自动判断", "角色对话", "物品与技能", "任务与剧情" };
            Ui.Get<ComboBox>(Window, "StyleCombo").ItemsSource = new[] { "自然准确", "尽量直译", "简洁口语" };
            modelDebounce.Tick += async (s, e) => { modelDebounce.Stop(); await FetchModels(false); };
            Ui.Get<TextBox>(Window, "EndpointInput").TextChanged += (s, e) => {
                if (loading || fillingProvider || closed) return;
                var endpoint = Ui.Get<TextBox>(Window, "EndpointInput"); string typed = endpoint.Text;
                if (active == null) {
                    Ui.Get<ComboBox>(Window, "ProviderCombo").SelectedItem = draft.Providers.First(p => p.Id == "custom");
                    fillingProvider = true; endpoint.Text = typed; fillingProvider = false;
                }
                InvalidateRequests();
                var previous = original.Providers.FirstOrDefault(p => p.Id == active.Id);
                if (previous != null && !string.IsNullOrWhiteSpace(previous.BaseUrl)) {
                    pendingKeys.Remove(active.Id);
                    fillingProvider = true; Ui.Get<PasswordBox>(Window, "ApiKeyInput").Clear(); fillingProvider = false;
                }
                active.BaseUrl = endpoint.Text.Trim(); modelOwners.Clear(); ModelOwnerHint();
                Ui.Get<ComboBox>(Window, "ModelInput").ItemsSource = null;
                KeyHint(); ScheduleModels();
            };
            Ui.Get<PasswordBox>(Window, "ApiKeyInput").PasswordChanged += (s, e) => {
                if (loading || fillingProvider || closed) return;
                InvalidateRequests();
                string edited = Ui.Get<PasswordBox>(Window, "ApiKeyInput").Password.Trim();
                if (active == null) unassignedKey = edited;
                else if (edited.Length == 0) pendingKeys.Remove(active.Id);
                else { pendingKeys[active.Id] = edited; deletedKeys.Remove(active.Id); }
                modelOwners.Clear(); ModelOwnerHint();
                Ui.Get<ComboBox>(Window, "ModelInput").ItemsSource = null;
                KeyHint(); ScheduleModels();
            };
            modelTextChanged = (s, e) => {
                ThinkingFields();
                ModelOwnerHint();
                if (!loading && !fillingProvider) { modelApplyDebounce.Stop(); modelApplyDebounce.Start(); }
                if (!fillingProvider && testCancellation != null) { testCancellation.Cancel(); Status("模型已变化，请重新测试连接。", false); }
            };
            modelTextProperty.AddValueChanged(Ui.Get<ComboBox>(Window, "ModelInput"), modelTextChanged);
            Ui.Get<ComboBox>(Window, "ThinkingModeCombo").SelectionChanged += (s, e) => ThinkingFields();
            bool configured = !string.IsNullOrWhiteSpace(draft.Provider.Model) ||
                (shell.NativeEnabled && Credentials.Read(draft.ProviderId).Length > 0);
            if (configured) Ui.Get<ComboBox>(Window, "ProviderCombo").SelectedItem = draft.Provider;
            Ui.Visible(Ui.Get<TextBlock>(Window, "ProviderPlaceholder"), !configured);
            Ui.Get<ComboBox>(Window, "ModelInput").IsEnabled = configured;
            PopulatePreferences();
            Ui.Get<CheckBox>(Window, "ClipboardModeCheck").IsChecked = draft.ClipboardMode;
            Ui.Get<CheckBox>(Window, "FocusInputCheck").IsChecked = draft.FocusInputOnShortcut;
            Ui.Get<TextBox>(Window, "ShortcutInput").Text = draft.Shortcut;
            var shortcut = Ui.Get<TextBox>(Window, "ShortcutInput");
            shortcut.GotKeyboardFocus += (s, e) => {
                if (recordingShortcut) return;
                recordingShortcut = true; shortcutBeforeRecording = shortcut.Text;
                shell.SetShortcutRecording(true); shortcut.Text = "请按下快捷键…";
                Ui.Get<TextBlock>(Window, "ShortcutHint").Text = "按下 Ctrl、Alt 或 Win 与另一个按键；Esc 取消。";
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
                } catch (UserError error) { Ui.Get<TextBlock>(Window, "ShortcutHint").Text = error.Message; }
            };
            Window.Deactivated += (s, e) => EndShortcutRecording(false);
            Ui.Get<CheckBox>(Window, "AutoStartCheck").IsChecked = draft.AutoStart;
            Ui.Get<CheckBox>(Window, "HistoryCheck").IsChecked = draft.HistoryEnabled;
            Ui.Get<TextBox>(Window, "HistoryLimitInput").Text = draft.HistoryLimit.ToString();
            DrawPresets(); loading = false; SceneFields(); KeyHint();
            Ui.Click(Window, "AddCustom", AddCustom);
            Ui.Click(Window, "SavePreset", SavePreset);
            Ui.Click(Window, "LoadPreset", LoadPreset);
            Ui.Click(Window, "RemovePreset", () => {
                var preset = Ui.Get<ComboBox>(Window, "PresetCombo").SelectedItem as LearningPreset;
                if (preset != null) { draft.Presets.Remove(preset); DrawPresets(); SchedulePreferences(); }
            });
            Ui.Click(Window, "DeleteKey", () => {
                if (active == null) return;
                deletedKeys.Add(active.Id); pendingKeys.Remove(active.Id);
                Ui.Get<PasswordBox>(Window, "ApiKeyInput").Clear(); KeyHint(); Status("应用服务配置后删除密钥。", false);
                InvalidateRequests(); ModelStatus("密钥待删除，已停止获取模型。", false);
            });
            Ui.Click(Window, "TestConnection", async () => await TestConnection());
            Ui.Click(Window, "FetchModels", async () => await FetchModels(true));
            Ui.Click(Window, "SaveSettingsButton", Save);
            Ui.Click(Window, "CloseSettingsButton", () => Window.Close());
            preferenceDebounce.Tick += (s, e) => { preferenceDebounce.Stop(); ApplyPreferences(); };
            modelApplyDebounce.Tick += (s, e) => { modelApplyDebounce.Stop(); AutoApplyModel(); };
            foreach (string name in new[] { "TargetInput", "SceneDetailInput", "HistoryLimitInput" })
                Ui.Get<TextBox>(Window, name).TextChanged += (s, e) => SchedulePreferences();
            foreach (string name in new[] { "GameTypeCombo", "StyleCombo" })
                Ui.Get<ComboBox>(Window, name).SelectionChanged += (s, e) => SchedulePreferences();
            foreach (string name in new[] { "ClipboardModeCheck", "FocusInputCheck", "AutoStartCheck", "HistoryCheck" }) {
                Ui.Get<CheckBox>(Window, name).Checked += (s, e) => SchedulePreferences();
                Ui.Get<CheckBox>(Window, name).Unchecked += (s, e) => SchedulePreferences();
            }
            var password = Ui.Get<PasswordBox>(Window, "ApiKeyInput");
            password.GotKeyboardFocus += (s, e) => KeyHint(); password.LostKeyboardFocus += (s, e) => KeyHint();
            Window.Closing += (s, e) => {
                EndShortcutRecording(false);
                if (preferenceDebounce.IsEnabled) { preferenceDebounce.Stop(); ApplyPreferences(); }
                if (modelApplyDebounce.IsEnabled) { modelApplyDebounce.Stop(); AutoApplyModel(); }
            };
            Window.Closed += (s, e) => {
                closed = true; providerGeneration.Next();
                modelDebounce.Stop(); EndShortcutRecording(false);
                preferenceDebounce.Stop(); modelApplyDebounce.Stop();
                modelTextProperty.RemoveValueChanged(Ui.Get<ComboBox>(Window, "ModelInput"), modelTextChanged);
                if (testCancellation != null) testCancellation.Cancel();
                if (modelCancellation != null) modelCancellation.Cancel();
                Ui.Get<PasswordBox>(Window, "ApiKeyInput").Clear(); pendingKeys.Clear();
                unassignedKey = "";
            };
            Window.Loaded += (s, e) => ScheduleModels();
        }
        private void EndShortcutRecording(bool accept)
        {
            if (!recordingShortcut) return;
            if (!accept) Ui.Get<TextBox>(Window, "ShortcutInput").Text = shortcutBeforeRecording;
            recordingShortcut = false; shell.SetShortcutRecording(false);
            Ui.Get<TextBlock>(Window, "ShortcutHint").Text = accept ? "已录入，正在应用…" : "点击上方，再按下组合键；Esc 取消。";
            if (accept) SchedulePreferences();
        }
        private void ProviderChanged(object sender, SelectionChangedEventArgs args)
        {
            // The editable model ComboBox also bubbles SelectionChanged.
            if (args.OriginalSource != Ui.Get<ComboBox>(Window, "ProviderCombo")) return;
            InvalidateRequests();
            if (active != null) CaptureProfile();
            fillingProvider = true;
            active = Ui.Get<ComboBox>(Window, "ProviderCombo").SelectedItem as ProviderProfile;
            if (active == null) { fillingProvider = false; return; }
            draft.ProviderId = active.Id;
            Ui.Visible(Ui.Get<TextBlock>(Window, "ProviderPlaceholder"), false);
            Ui.Get<ComboBox>(Window, "ModelInput").IsEnabled = true;
            modelOwners.Clear(); ModelOwnerHint();
            Ui.Get<ComboBox>(Window, "ModelInput").ItemsSource = null;
            Ui.Get<ComboBox>(Window, "ModelInput").Text = active.Model;
            SelectTag("ThinkingModeCombo", active.ThinkingMode ?? "auto");
            SelectTag("ReasoningCombo", active.ReasoningEffort ?? "low");
            Ui.Get<TextBox>(Window, "OutputLimitInput").Text = active.MaxOutputTokens == 0 ? "" : active.MaxOutputTokens.ToString();
            Ui.Get<TextBox>(Window, "EndpointInput").Text = active.BaseUrl;
            string pending;
            if (!string.IsNullOrEmpty(unassignedKey)) { pendingKeys[active.Id] = unassignedKey; unassignedKey = ""; }
            Ui.Get<PasswordBox>(Window, "ApiKeyInput").Password = pendingKeys.TryGetValue(active.Id, out pending) ? pending : "";
            Ui.Get<Expander>(Window, "EndpointExpander").IsExpanded = active.Id == "custom";
            fillingProvider = false; KeyHint(); ThinkingFields(); ScheduleModels();
            if (!loading) {
                Status("已切换服务。已配置的模型自动应用；新密钥和接口需应用服务配置。", false);
                modelApplyDebounce.Stop(); modelApplyDebounce.Start();
            }
        }
        private void CaptureProfile()
        {
            if (active == null) return;
            active.Model = Ui.Get<ComboBox>(Window, "ModelInput").Text.Trim();
            active.BaseUrl = Ui.Get<TextBox>(Window, "EndpointInput").Text.Trim();
            active.ThinkingMode = SelectedTag("ThinkingModeCombo", "auto");
            active.ReasoningEffort = SelectedTag("ReasoningCombo", "low");
            int budget; int.TryParse(Ui.Get<TextBox>(Window, "OutputLimitInput").Text.Trim(), out budget); active.MaxOutputTokens = budget;
            string key = Ui.Get<PasswordBox>(Window, "ApiKeyInput").Password.Trim();
            if (key.Length > 0) { pendingKeys[active.Id] = key; deletedKeys.Remove(active.Id); }
        }
        private string SelectedTag(string name, string fallback)
        {
            var item = Ui.Get<ComboBox>(Window, name).SelectedItem as ComboBoxItem;
            return item == null ? fallback : Convert.ToString(item.Tag);
        }
        private void SelectTag(string name, string value)
        {
            var input = Ui.Get<ComboBox>(Window, name);
            input.SelectedItem = input.Items.Cast<ComboBoxItem>().FirstOrDefault(x => Convert.ToString(x.Tag) == value) ?? input.Items[0];
        }
        private void ThinkingFields()
        {
            if (active == null || fillingProvider) return;
            bool defaultsOnly = active.Id == "openai" || active.Id == "custom";
            Ui.Get<ComboBox>(Window, "ThinkingModeCombo").IsEnabled = !defaultsOnly;
            if (defaultsOnly) SelectTag("ThinkingModeCombo", "auto");
            bool required = LlmClient.IsGlm53(new ProviderProfile { Id = active.Id, Model = Ui.Get<ComboBox>(Window, "ModelInput").Text });
            Ui.Get<ComboBoxItem>(Window, "DisableThinkingOption").IsEnabled = !required;
            if (required && SelectedTag("ThinkingModeCombo", "auto") == "disabled") SelectTag("ThinkingModeCombo", "auto");
            Ui.Visible(Ui.Get<StackPanel>(Window, "ReasoningPanel"), required);
            Ui.Get<TextBlock>(Window, "ThinkingHint").Text = defaultsOnly ?
                "此服务使用模型默认思考设置。不同模型参数不同，可调整下方输出上限。" : required ?
                "此模型必须开启思考。自动使用轻量思考，不能关闭。" :
                "自动按已知模型适配；未知 GLM 模型沿用服务默认。手动选择需模型支持。";
        }
        private void KeyHint()
        {
            var input = Ui.Get<PasswordBox>(Window, "ApiKeyInput");
            var placeholder = Ui.Get<TextBlock>(Window, "KeyPlaceholder");
            bool saved = active != null && !deletedKeys.Contains(active.Id) && shell.NativeEnabled &&
                original.Providers.Any(p => p.Id == active.Id && p.BaseUrl.TrimEnd('/') == Ui.Get<TextBox>(Window, "EndpointInput").Text.TrimEnd('/')) && Credentials.Read(active.Id).Length > 0;
            placeholder.Text = saved ? "••••••••  已保存 · 点击更换" : "输入此服务的 API Key…";
            Ui.Visible(placeholder, input.Password.Length == 0 && !input.IsKeyboardFocused);
            if (active == null) {
                Ui.Get<TextBlock>(Window, "KeyHint").Text = "先选择服务，密钥与模型随服务一起切换。"; return;
            }
            string url = Ui.Get<TextBox>(Window, "EndpointInput").Text.TrimEnd('/');
            var previous = original.Providers.FirstOrDefault(p => p.Id == active.Id);
            string hint = input.Password.Length > 0 ? "密钥已填写，点击「应用服务配置」生效。" :
                previous == null || url != previous.BaseUrl.TrimEnd('/') ?
                "接口地址已变化，请重新填写密钥。" :
                deletedKeys.Contains(active.Id) ? "应用服务配置后删除已存密钥。" :
                saved ? "已保存密钥；点击可替换，留空继续使用。" : "保存在 Windows 凭据管理器。";
            Ui.Get<TextBlock>(Window, "KeyHint").Text = hint;
        }
        private void PopulatePreferences()
        {
            loading = true;
            Ui.Get<TextBox>(Window, "TargetInput").Text = draft.TargetLanguage;
            Ui.Get<ComboBox>(Window, "SceneCombo").SelectedItem = draft.Scene;
            detailScene = draft.Scene;
            Ui.Get<TextBox>(Window, "SceneDetailInput").Text = draft.SceneDetail ?? "";
            if (draft.Scene != "通用") draft.SceneDetails[draft.Scene] = draft.SceneDetail ?? "";
            Ui.Get<ComboBox>(Window, "GameTypeCombo").SelectedItem = draft.GameTextType ?? "自动判断";
            Ui.Get<ComboBox>(Window, "StyleCombo").SelectedItem = draft.Style;
            DrawLearning(); loading = false; SceneFields();
        }
        private void SceneFields()
        {
            string scene = Convert.ToString(Ui.Get<ComboBox>(Window, "SceneCombo").SelectedItem);
            Ui.Visible(Ui.Get<StackPanel>(Window, "SceneDetailPanel"), scene != "通用" && scene.Length > 0);
            Ui.Visible(Ui.Get<StackPanel>(Window, "GameTypePanel"), scene == "游戏");
            Ui.Get<TextBlock>(Window, "SceneDetailLabel").Text =
                scene == "游戏" ? "游戏名称（选填）" : scene == "书籍" ? "书名（选填）" :
                scene == "影视" ? "影视名称（选填）" : scene == "技术文档" ? "技术领域（选填）" : "当前语境（选填）";
        }
        private void DrawLearning()
        {
            var panel = Ui.Get<WrapPanel>(Window, "LearningChoices"); panel.Children.Clear();
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
            string name = Ui.Get<TextBox>(Window, "CustomNameInput").Text.Trim();
            string instruction = Ui.Get<TextBox>(Window, "CustomInstructionInput").Text.Trim();
            if (name.Length == 0 || instruction.Length == 0) { Status("请填写名称和讲解方式。", true); return; }
            if (draft.Learning.Count >= 15) { Status("最多保留 15 个学习选项。", true); return; }
            if (draft.Learning.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { Status("这个选项名称已经存在。", true); return; }
            draft.Learning.Add(new LearningOption { Id = Guid.NewGuid().ToString("N"), Name = name, Instruction = instruction, Enabled = true, Custom = true });
            DrawLearning(); Ui.Get<TextBox>(Window, "CustomNameInput").Clear(); Ui.Get<TextBox>(Window, "CustomInstructionInput").Clear();
            SchedulePreferences();
        }
        private void CapturePreferences()
        {
            draft.TargetLanguage = Ui.Get<TextBox>(Window, "TargetInput").Text.Trim();
            draft.Scene = Convert.ToString(Ui.Get<ComboBox>(Window, "SceneCombo").SelectedItem);
            draft.SceneDetail = draft.Scene == "通用" ? "" : Ui.Get<TextBox>(Window, "SceneDetailInput").Text.Trim();
            if (draft.Scene != "通用") draft.SceneDetails[draft.Scene] = draft.SceneDetail;
            draft.GameTextType = Convert.ToString(Ui.Get<ComboBox>(Window, "GameTypeCombo").SelectedItem);
            draft.Style = Convert.ToString(Ui.Get<ComboBox>(Window, "StyleCombo").SelectedItem);
        }
        private void DrawPresets()
        {
            Ui.Get<ComboBox>(Window, "PresetCombo").ItemsSource = null;
            Ui.Get<ComboBox>(Window, "PresetCombo").ItemsSource = draft.Presets;
            if (draft.Presets.Count > 0) Ui.Get<ComboBox>(Window, "PresetCombo").SelectedIndex = 0;
        }
        private void SavePreset()
        {
            string name = Ui.Get<TextBox>(Window, "PresetNameInput").Text.Trim();
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
            var preset = Ui.Get<ComboBox>(Window, "PresetCombo").SelectedItem as LearningPreset;
            if (preset == null) return;
            draft.TargetLanguage = preset.TargetLanguage; draft.Scene = preset.Scene; draft.SceneDetail = preset.SceneDetail;
            draft.GameTextType = preset.GameTextType; draft.Style = preset.Style; draft.Learning = Json.Copy(preset.Learning);
            PopulatePreferences(); SchedulePreferences();
        }
        private void Status(string text, bool error)
        {
            var label = Ui.Get<TextBlock>(Window, "SettingsStatus"); label.Text = text;
            label.Foreground = error ? new SolidColorBrush(Color.FromRgb(147, 94, 67)) : Ui.Brush("Muted");
        }
        private void SchedulePreferences()
        {
            if (loading || closed || recordingShortcut) return;
            Status("正在应用…", false); preferenceDebounce.Stop(); preferenceDebounce.Start();
        }
        private bool ApplyPreferences()
        {
            try {
                CapturePreferences();
                if (string.IsNullOrWhiteSpace(draft.TargetLanguage)) throw new UserError("target", "目标语言不能为空，尚未应用。" );
                int limit;
                if (!int.TryParse(Ui.Get<TextBox>(Window, "HistoryLimitInput").Text, out limit) || limit < 20 || limit > 1000)
                    throw new UserError("history", "历史条数应为 20 到 1000，尚未应用。" );
                var next = Json.Copy(shell.Store.Settings);
                next.TargetLanguage = draft.TargetLanguage; next.Scene = draft.Scene; next.SceneDetail = draft.SceneDetail;
                next.SceneDetails = Json.Copy(draft.SceneDetails); next.GameTextType = draft.GameTextType; next.Style = draft.Style;
                next.Learning = Json.Copy(draft.Learning); next.Presets = Json.Copy(draft.Presets);
                next.ClipboardMode = Ui.Get<CheckBox>(Window, "ClipboardModeCheck").IsChecked == true;
                next.FocusInputOnShortcut = Ui.Get<CheckBox>(Window, "FocusInputCheck").IsChecked == true;
                next.Shortcut = Ui.Get<TextBox>(Window, "ShortcutInput").Text.Trim(); HotkeySpec.Parse(next.Shortcut);
                next.AutoStart = Ui.Get<CheckBox>(Window, "AutoStartCheck").IsChecked == true;
                next.HistoryEnabled = Ui.Get<CheckBox>(Window, "HistoryCheck").IsChecked == true; next.HistoryLimit = limit;
                if (Json.Write(next) != Json.Write(shell.Store.Settings)) shell.ApplySettings(next, new Dictionary<string, string>(), new HashSet<string>());
                Status("已应用并保存 · 下次翻译使用新偏好", false);
                Ui.Get<TextBlock>(Window, "ShortcutHint").Text = "点击上方，再按下组合键；Esc 取消。";
                return true;
            } catch (Exception error) {
                shell.Log.Event("preferences_save_failed", error);
                Status(error is UserError ? error.Message : "应用失败，请检查配置后重试。", true); return false;
            }
        }
        private void AutoApplyModel()
        {
            if (closed || loading || fillingProvider || active == null || pendingKeys.ContainsKey(active.Id) || deletedKeys.Contains(active.Id)) return;
            var committed = shell.Store.Settings.Providers.FirstOrDefault(p => p.Id == active.Id);
            if (committed == null || committed.BaseUrl.TrimEnd('/') != Ui.Get<TextBox>(Window, "EndpointInput").Text.TrimEnd('/') || string.IsNullOrWhiteSpace(committed.Model)) return;
            if (shell.NativeEnabled && string.IsNullOrEmpty(ActiveKey())) return;
            try {
                CaptureProfile();
                if (string.IsNullOrWhiteSpace(active.Model)) return;
                // Advanced parameters have their own explicit apply action.
                var next = Json.Copy(shell.Store.Settings);
                var profile = next.Providers.First(p => p.Id == active.Id); profile.Model = active.Model;
                if (LlmClient.IsGlm53(profile) && profile.ThinkingMode == "disabled") profile.ThinkingMode = "auto";
                LlmClient.ValidateProfile(profile); next.ProviderId = active.Id;
                if (Json.Write(next) == Json.Write(shell.Store.Settings)) return;
                shell.ApplySettings(next, new Dictionary<string, string>(), new HashSet<string>());
                original = Json.Copy(shell.Store.Settings); Status("已切换模型并保存", false);
            } catch (Exception error) {
                shell.Log.Event("model_apply_failed", error); Status(error is UserError ? error.Message : "模型切换失败。", true);
            }
        }
        private void InvalidateRequests()
        {
            modelDebounce.Stop();
            providerGeneration.Next();
            if (testCancellation != null) testCancellation.Cancel();
            if (modelCancellation != null) modelCancellation.Cancel();
            Ui.Get<Button>(Window, "TestConnection").IsEnabled = true;
            Ui.Get<Button>(Window, "FetchModels").IsEnabled = true;
        }
        private string ActiveKey()
        {
            if (active == null) return unassignedKey;
            string key = Ui.Get<PasswordBox>(Window, "ApiKeyInput").Password.Trim();
            var previous = original.Providers.FirstOrDefault(p => p.Id == active.Id);
            if (string.IsNullOrEmpty(key) && !deletedKeys.Contains(active.Id) && previous != null &&
                previous.BaseUrl.TrimEnd('/') == active.BaseUrl.TrimEnd('/') && shell.NativeEnabled) key = Credentials.Read(active.Id);
            return key;
        }
        private void ModelStatus(string text, bool error)
        {
            var label = Ui.Get<TextBlock>(Window, "ModelStatus"); label.Text = text;
            label.Foreground = error ? new SolidColorBrush(Color.FromRgb(147, 94, 67)) : Ui.Brush("Muted");
        }
        private void ScheduleModels()
        {
            if (loading || fillingProvider || closed) return;
            if (active == null) { ModelStatus("选择服务或填写接口地址后，自动加载模型。", false); return; }
            active.BaseUrl = Ui.Get<TextBox>(Window, "EndpointInput").Text.Trim();
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
            Ui.Get<Button>(Window, "FetchModels").IsEnabled = false;
            try {
                ModelStatus("正在获取模型…", false);
                var catalog = await shell.Client.ListCatalogAsync(profile, ActiveKey(), cancellation.Token);
                if (closed || !providerGeneration.IsCurrent(version)) return;
                var models = catalog.Models; modelOwners = catalog.Owners;
                var input = Ui.Get<ComboBox>(Window, "ModelInput");
                string typed = input.Text;
                input.ItemsSource = models; input.Text = typed;
                ModelOwnerHint();
                ModelStatus("已获取 " + models.Count + " 个模型。" + (typed.Length > 0 && !models.Contains(typed) ?
                    "当前 ID 未在列表中，请核对。" : "请选择支持文字对话的模型。"), false);
                if (manual && Window.IsVisible) input.IsDropDownOpen = true;
            } catch (OperationCanceledException) { }
            catch (Exception error) {
                shell.Log.Event("model_discovery_failed", error);
                if (!closed && providerGeneration.IsCurrent(version)) ModelStatus(error is UserError ? error.Message : "获取失败，可直接填写模型 ID。", true);
            } finally {
                if (modelCancellation == cancellation) modelCancellation = null;
                if (!closed && providerGeneration.IsCurrent(version)) Ui.Get<Button>(Window, "FetchModels").IsEnabled = true;
                cancellation.Dispose();
            }
        }
        private void ModelOwnerHint()
        {
            string owner;
            var hint = Ui.Get<TextBlock>(Window, "ModelVendorHint");
            bool supplied = modelOwners.TryGetValue(Ui.Get<ComboBox>(Window, "ModelInput").Text ?? "", out owner);
            hint.Text = supplied ? "接口提供的模型归属：" + owner : ""; Ui.Visible(hint, supplied);
        }
        private async Task TestConnection()
        {
            if (active == null) { Status("请先选择 API 服务或填写接口地址。", true); return; }
            CaptureProfile(); InvalidateRequests();
            long version = providerGeneration.Next();
            var profile = Json.Copy(active);
            var button = Ui.Get<Button>(Window, "TestConnection"); button.IsEnabled = false;
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
            try {
                EndShortcutRecording(false); InvalidateRequests();
                preferenceDebounce.Stop(); modelApplyDebounce.Stop();
                if (active == null) throw new UserError("endpoint", "请先选择 API 服务或填写接口地址。");
                CaptureProfile();
                string limitText = Ui.Get<TextBox>(Window, "OutputLimitInput").Text.Trim(); int outputLimit;
                if (limitText.Length > 0 && !int.TryParse(limitText, out outputLimit)) throw new UserError("parameter", "输出上限请填写整数，或留空自动适配。");
                LlmClient.Endpoint(active.BaseUrl); LlmClient.ValidateProfile(active);
                if (string.IsNullOrWhiteSpace(active.Model)) throw new UserError("model", "请选择或填写当前服务的模型 ID。");
                var previous = original.Providers.FirstOrDefault(p => p.Id == active.Id);
                if (previous != null && previous.BaseUrl.TrimEnd('/') != active.BaseUrl.TrimEnd('/') && !pendingKeys.ContainsKey(active.Id))
                    throw new UserError("key", "修改接口地址后，请为该服务重新填写密钥。");
                if (!ApplyPreferences()) return;
                var next = Json.Copy(shell.Store.Settings); next.ProviderId = active.Id;
                next.Providers[next.Providers.FindIndex(p => p.Id == active.Id)] = Json.Copy(active);
                var keys = new Dictionary<string, string>(); string pending;
                if (pendingKeys.TryGetValue(active.Id, out pending)) keys[active.Id] = pending;
                var deleted = new HashSet<string>(); if (deletedKeys.Contains(active.Id)) deleted.Add(active.Id);
                shell.ApplySettings(next, keys, deleted);
                original = Json.Copy(shell.Store.Settings); pendingKeys.Remove(active.Id); deletedKeys.Remove(active.Id);
                fillingProvider = true; Ui.Get<PasswordBox>(Window, "ApiKeyInput").Clear(); fillingProvider = false;
                KeyHint(); Status("服务配置已应用并保存", false);
            } catch (Exception error) { shell.Log.Event("settings_save_failed", error); Status(error is UserError ? error.Message : "保存失败，请检查配置后重试。", true); }
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
            Ui.Click(Window, "DeleteHistory", () => {
                var record = Ui.Get<ListBox>(Window, "HistoryList").SelectedItem as TranslationRecord;
                if (record == null) return;
                Try(() => { shell.Store.Delete(record.Id); shell.Forget(record.Id); Refresh(); });
            });
            Ui.Click(Window, "ClearHistory", () => {
                if (MessageBox.Show(Window, "清空全部本地历史？", "Leaf", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                Try(() => { shell.Store.Clear(); shell.Forget(null); Refresh(); });
            });
            Refresh();
        }
        private void Try(Action action)
        {
            try { action(); } catch (Exception error) {
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
