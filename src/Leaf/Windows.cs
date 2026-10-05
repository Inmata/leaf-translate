using System;
using System.Collections.Generic;
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
using Forms = System.Windows.Forms;

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

    public sealed class MonitorOption
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public override string ToString() { return Name; }
    }

    public sealed class SettingsWindow
    {
        private readonly AppShell shell;
        private readonly Settings original;
        private readonly Settings draft;
        private ProviderProfile active;
        private bool loading;
        private readonly Dictionary<string, string> pendingKeys = new Dictionary<string, string>();
        private readonly HashSet<string> deletedKeys = new HashSet<string>();
        private CancellationTokenSource testCancellation;
        public Window Window { get; private set; }

        public SettingsWindow(AppShell owner)
        {
            shell = owner; original = Json.Copy(shell.Store.Settings); draft = Json.Copy(original);
            Window = Ui.Load("Settings"); loading = true;
            Ui.Get<ComboBox>(Window, "ProviderCombo").ItemsSource = draft.Providers;
            Ui.Get<ComboBox>(Window, "ProviderCombo").SelectionChanged += ProviderChanged;
            Ui.Get<ComboBox>(Window, "SceneCombo").ItemsSource = new[] { "通用", "书籍", "影视", "技术文档", "游戏", "自定义" };
            Ui.Get<ComboBox>(Window, "SceneCombo").SelectionChanged += (s, e) => {
                if (!loading) Ui.Get<TextBox>(Window, "SceneDetailInput").Text = "";
                SceneFields();
            };
            Ui.Get<ComboBox>(Window, "GameTypeCombo").ItemsSource = new[] { "自动判断", "角色对话", "物品与技能", "任务与剧情" };
            Ui.Get<ComboBox>(Window, "StyleCombo").ItemsSource = new[] { "自然准确", "尽量直译", "简洁口语" };
            Ui.Get<TextBox>(Window, "EndpointInput").TextChanged += (s, e) => KeyHint();
            var monitors = new List<MonitorOption> { new MonitorOption { Id = "", Name = "跟随鼠标所在屏幕" } };
            monitors.AddRange(Forms.Screen.AllScreens.Select((screen, index) => new MonitorOption {
                Id = screen.DeviceName, Name = "固定在屏幕 " + (index + 1) + (screen.Primary ? "（主屏）" : "（副屏）")
            }));
            Ui.Get<ComboBox>(Window, "MonitorCombo").ItemsSource = monitors;
            Ui.Get<ComboBox>(Window, "MonitorCombo").SelectedItem = monitors.FirstOrDefault(m => m.Id == draft.Monitor) ?? monitors[0];
            Ui.Get<ComboBox>(Window, "ProviderCombo").SelectedItem = draft.Provider;
            PopulatePreferences();
            Ui.Get<CheckBox>(Window, "ClipboardModeCheck").IsChecked = draft.ClipboardMode;
            Ui.Get<TextBox>(Window, "ShortcutInput").Text = draft.Shortcut;
            Ui.Get<CheckBox>(Window, "AutoStartCheck").IsChecked = draft.AutoStart;
            Ui.Get<CheckBox>(Window, "HistoryCheck").IsChecked = draft.HistoryEnabled;
            Ui.Get<TextBox>(Window, "HistoryLimitInput").Text = draft.HistoryLimit.ToString();
            DrawPresets(); loading = false; SceneFields(); KeyHint();
            Ui.Click(Window, "AddCustom", AddCustom);
            Ui.Click(Window, "SavePreset", SavePreset);
            Ui.Click(Window, "LoadPreset", LoadPreset);
            Ui.Click(Window, "RemovePreset", () => {
                var preset = Ui.Get<ComboBox>(Window, "PresetCombo").SelectedItem as LearningPreset;
                if (preset != null) { draft.Presets.Remove(preset); DrawPresets(); }
            });
            Ui.Click(Window, "DeleteKey", () => {
                if (active == null) return;
                deletedKeys.Add(active.Id); pendingKeys.Remove(active.Id);
                Ui.Get<PasswordBox>(Window, "ApiKeyInput").Clear(); KeyHint(); Status("保存设置后删除密钥。", false);
            });
            Ui.Click(Window, "TestConnection", async () => await TestConnection());
            Ui.Click(Window, "SaveSettingsButton", Save);
            Window.Closed += (s, e) => {
                if (testCancellation != null) testCancellation.Cancel();
                Ui.Get<PasswordBox>(Window, "ApiKeyInput").Clear(); pendingKeys.Clear();
            };
        }
        private void ProviderChanged(object sender, SelectionChangedEventArgs args)
        {
            if (active != null) CaptureProfile();
            active = Ui.Get<ComboBox>(Window, "ProviderCombo").SelectedItem as ProviderProfile;
            if (active == null) return;
            Ui.Get<TextBox>(Window, "ModelInput").Text = active.Model;
            Ui.Get<TextBox>(Window, "EndpointInput").Text = active.BaseUrl;
            string pending;
            Ui.Get<PasswordBox>(Window, "ApiKeyInput").Password = pendingKeys.TryGetValue(active.Id, out pending) ? pending : "";
            KeyHint();
        }
        private void CaptureProfile()
        {
            if (active == null) return;
            active.Model = Ui.Get<TextBox>(Window, "ModelInput").Text.Trim();
            active.BaseUrl = Ui.Get<TextBox>(Window, "EndpointInput").Text.Trim();
            string key = Ui.Get<PasswordBox>(Window, "ApiKeyInput").Password.Trim();
            if (key.Length > 0) { pendingKeys[active.Id] = key; deletedKeys.Remove(active.Id); }
        }
        private void KeyHint()
        {
            if (active == null) return;
            string url = Ui.Get<TextBox>(Window, "EndpointInput").Text.TrimEnd('/');
            var previous = original.Providers.FirstOrDefault(p => p.Id == active.Id);
            string hint = previous == null || url != previous.BaseUrl.TrimEnd('/') ?
                "接口地址已变化，请重新填写密钥。" :
                deletedKeys.Contains(active.Id) ? "保存后删除已存密钥。" :
                shell.NativeEnabled && Credentials.Read(active.Id).Length > 0 ? "已保存密钥；留空保持不变。" : "保存在 Windows 凭据管理器。";
            Ui.Get<TextBlock>(Window, "KeyHint").Text = hint;
        }
        private void PopulatePreferences()
        {
            loading = true;
            Ui.Get<TextBox>(Window, "TargetInput").Text = draft.TargetLanguage;
            Ui.Get<ComboBox>(Window, "SceneCombo").SelectedItem = draft.Scene;
            Ui.Get<TextBox>(Window, "SceneDetailInput").Text = draft.SceneDetail ?? "";
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
                check.Checked += (s, e) => captured.Enabled = true;
                check.Unchecked += (s, e) => captured.Enabled = false;
                if (option.Custom) {
                    var group = new StackPanel { Orientation = Orientation.Horizontal };
                    check.Margin = new Thickness(0, 0, 2, 12); group.Children.Add(check);
                    var remove = new Button { Content = "×", Style = (Style)Application.Current.Resources["GhostButton"], Margin = new Thickness(0, -4, 15, 8), ToolTip = "删除此学习项" };
                    remove.Click += (s, e) => { draft.Learning.Remove(captured); DrawLearning(); };
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
            Status("已添加。保存设置后生效。", false);
        }
        private void CapturePreferences()
        {
            draft.TargetLanguage = Ui.Get<TextBox>(Window, "TargetInput").Text.Trim();
            draft.Scene = Convert.ToString(Ui.Get<ComboBox>(Window, "SceneCombo").SelectedItem);
            draft.SceneDetail = draft.Scene == "通用" ? "" : Ui.Get<TextBox>(Window, "SceneDetailInput").Text.Trim();
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
            DrawPresets(); Status("已记录预设。保存设置后生效。", false);
        }
        private void LoadPreset()
        {
            var preset = Ui.Get<ComboBox>(Window, "PresetCombo").SelectedItem as LearningPreset;
            if (preset == null) return;
            draft.TargetLanguage = preset.TargetLanguage; draft.Scene = preset.Scene; draft.SceneDetail = preset.SceneDetail;
            draft.GameTextType = preset.GameTextType; draft.Style = preset.Style; draft.Learning = Json.Copy(preset.Learning);
            PopulatePreferences(); Status("已应用预设。保存设置后生效。", false);
        }
        private void Status(string text, bool error)
        {
            var label = Ui.Get<TextBlock>(Window, "SettingsStatus"); label.Text = text;
            label.Foreground = error ? new SolidColorBrush(Color.FromRgb(147, 94, 67)) : Ui.Brush("Muted");
        }
        private async Task TestConnection()
        {
            CaptureProfile();
            var button = Ui.Get<Button>(Window, "TestConnection"); button.IsEnabled = false;
            testCancellation = new CancellationTokenSource();
            try {
                string key; pendingKeys.TryGetValue(active.Id, out key);
                var previous = original.Providers.FirstOrDefault(p => p.Id == active.Id);
                if (string.IsNullOrEmpty(key) && !deletedKeys.Contains(active.Id) && previous != null &&
                    previous.BaseUrl.TrimEnd('/') == active.BaseUrl.TrimEnd('/') && shell.NativeEnabled) key = Credentials.Read(active.Id);
                Status("正在发送短请求…", false);
                string result = await shell.Client.CompleteAsync(Json.Copy(active), key, new List<ChatTurn> {
                    new ChatTurn { Role = "user", Content = "只回答 OK。" }
                }, false, false, null, testCancellation.Token);
                Status("连接成功，可以开始翻译。", false);
            } catch (OperationCanceledException) { }
            catch (Exception error) { Status(error is UserError ? error.Message : "测试失败，请检查网络和配置。", true); }
            finally { button.IsEnabled = true; testCancellation.Dispose(); testCancellation = null; }
        }
        private void Save()
        {
            try {
                CaptureProfile(); CapturePreferences();
                if (string.IsNullOrWhiteSpace(draft.TargetLanguage)) throw new UserError("target", "请填写目标语言。");
                foreach (var profile in draft.Providers) {
                    LlmClient.Endpoint(profile.BaseUrl);
                    if (string.IsNullOrWhiteSpace(profile.Model)) throw new UserError("model", "请填写模型名称。");
                    var previous = original.Providers.FirstOrDefault(p => p.Id == profile.Id);
                    if (previous != null && previous.BaseUrl.TrimEnd('/') != profile.BaseUrl.TrimEnd('/') && !pendingKeys.ContainsKey(profile.Id))
                        throw new UserError("key", "修改接口地址后，请为该服务重新填写密钥。");
                }
                draft.ClipboardMode = Ui.Get<CheckBox>(Window, "ClipboardModeCheck").IsChecked == true;
                draft.Shortcut = Ui.Get<TextBox>(Window, "ShortcutInput").Text.Trim(); HotkeySpec.Parse(draft.Shortcut);
                draft.HistoryEnabled = Ui.Get<CheckBox>(Window, "HistoryCheck").IsChecked == true;
                int limit;
                if (!int.TryParse(Ui.Get<TextBox>(Window, "HistoryLimitInput").Text, out limit) || limit < 20 || limit > 1000)
                    throw new UserError("history", "历史条数应为 20 到 1000。");
                draft.HistoryLimit = limit;
                draft.AutoStart = Ui.Get<CheckBox>(Window, "AutoStartCheck").IsChecked == true;
                draft.Monitor = ((MonitorOption)Ui.Get<ComboBox>(Window, "MonitorCombo").SelectedItem).Id;
                draft.Positions = Json.Copy(shell.Store.Settings.Positions);
                shell.ApplySettings(draft, pendingKeys, deletedKeys); Window.Close();
            } catch (Exception error) { Status(error is UserError ? error.Message : "保存失败，请检查配置后重试。", true); }
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
                if (MessageBox.Show(Window, "清空全部本地历史？", "叶译", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                Try(() => { shell.Store.Clear(); shell.Forget(null); Refresh(); });
            });
            Refresh();
        }
        private void Try(Action action)
        {
            try { action(); } catch (Exception error) {
                MessageBox.Show(Window, error is UserError ? error.Message : "无法更新本地历史。", "叶译");
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
