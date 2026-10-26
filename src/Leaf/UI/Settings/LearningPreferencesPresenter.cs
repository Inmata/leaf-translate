using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Leaf
{
    internal sealed class LearningPreferencesPresenter
    {
        private readonly FrameworkElement scope;
        private readonly Settings draft;
        private readonly Func<bool> hostLoading;
        private readonly Action onChanged;
        private readonly Action<string, bool> reportStatus;
        private bool loading, fillingTarget;
        private string detailScene;
        private readonly List<FontEntry> fontEntries = new List<FontEntry>();
        internal bool IsLoading { get { return loading || hostLoading(); } }

        // One choice in the font picker: the embedded default carries an empty value, an
        // installed text font carries its family name. ToString is what the combo paints.
        private sealed class FontEntry
        {
            public string Value;
            public string Label;
            public override string ToString() { return Label; }
        }
        internal LearningPreferencesPresenter(FrameworkElement scope, Settings draft, Func<bool> hostLoading, Action onChanged, Action<string, bool> reportStatus)
        {
            this.scope = scope; this.draft = draft; this.hostLoading = hostLoading; this.onChanged = onChanged; this.reportStatus = reportStatus;
            Ui.Get<ComboBox>(scope, "SceneCombo").ItemsSource = new[] { "通用", "书籍", "影视", "技术文档", "游戏", "自定义" };
            Ui.Get<ComboBox>(scope, "SceneCombo").SelectionChanged += (s, e) => {
                if (!IsLoading) {
                    if (!string.IsNullOrEmpty(detailScene) && detailScene != "通用") draft.SceneDetails[detailScene] = Ui.Get<TextBox>(scope, "SceneDetailInput").Text;
                    detailScene = Convert.ToString(Ui.Get<ComboBox>(scope, "SceneCombo").SelectedItem);
                    string remembered; draft.SceneDetails.TryGetValue(detailScene, out remembered);
                    loading = true; Ui.Get<TextBox>(scope, "SceneDetailInput").Text = remembered ?? ""; loading = false;
                    SchedulePreferences();
                }
                SceneFields();
            };
            Ui.Get<ComboBox>(scope, "TargetLanguageCombo").ItemsSource = TargetLanguageChoices();
            Ui.Get<ComboBox>(scope, "TargetLanguageCombo").SelectionChanged += (s, e) => { if (!IsLoading) TargetLanguageChanged(); };
            Ui.Get<ComboBox>(scope, "GameTypeCombo").ItemsSource = new[] { "自动判断", "角色对话", "物品与技能", "任务与剧情" };
            Ui.Get<ComboBox>(scope, "StyleCombo").ItemsSource = new[] { "自然准确", "尽量直译", "简洁口语" };
            BuildFontEntries();
            Ui.Get<ComboBox>(scope, "FontCombo").ItemsSource = fontEntries;
            Ui.Get<ComboBox>(scope, "FontCombo").SelectionChanged += (s, e) => { if (!IsLoading) FontChanged(); };
            Ui.Get<ComboBox>(scope, "PresetCombo").SelectionChanged += (s, e) => PresetStateChanged();
            Ui.Click(scope, "AddCustom", AddCustom);
            Ui.Click(scope, "SavePreset", SavePreset);
            Ui.Click(scope, "LoadPreset", LoadPreset);
            Ui.Click(scope, "RemovePreset", () => {
                var preset = Ui.Get<ComboBox>(scope, "PresetCombo").SelectedItem as LearningPreset;
                if (preset != null) { draft.Presets.Remove(preset); DrawPresets(); SchedulePreferences(); }
            });
            // Typing a value is choosing the custom target language; the picker follows so the
            // pair never disagrees about what is really selected.
            Ui.Get<TextBox>(scope, "TargetInput").TextChanged += (s, e) => {
                if (IsLoading || fillingTarget) return;
                var combo = Ui.Get<ComboBox>(scope, "TargetLanguageCombo");
                if (Convert.ToString(combo.SelectedItem) != TargetLanguages.Custom) {
                    fillingTarget = true; combo.SelectedItem = TargetLanguages.Custom; fillingTarget = false;
                }
                Ui.Visible(Ui.Get<Grid>(scope, "TargetCustomPanel"), true);
            };
        }
        private void SchedulePreferences() { onChanged(); }
        private void Status(string text, bool error) { reportStatus(text, error); }

        internal void PopulatePreferences()
        {
            loading = true;
            PopulateTarget(draft.TargetLanguage);
            PopulateFont(draft.FontFamilyName);
            Ui.Get<ComboBox>(scope, "SceneCombo").SelectedItem = draft.Scene;
            detailScene = draft.Scene;
            Ui.Get<TextBox>(scope, "SceneDetailInput").Text = draft.SceneDetail ?? "";
            if (draft.Scene != "通用") draft.SceneDetails[draft.Scene] = draft.SceneDetail ?? "";
            Ui.Get<ComboBox>(scope, "GameTypeCombo").SelectedItem = draft.GameTextType ?? "自动判断";
            Ui.Get<ComboBox>(scope, "StyleCombo").SelectedItem = draft.Style;
            DrawLearning(); loading = false; SceneFields();
        }

        private static string[] TargetLanguageChoices()
        {
            var choices = new string[TargetLanguages.Common.Length + 1];
            Array.Copy(TargetLanguages.Common, choices, TargetLanguages.Common.Length);
            choices[TargetLanguages.Common.Length] = TargetLanguages.Custom;
            return choices;
        }

        // A stored value that is not one of the common languages is the user's own custom one:
        // the picker selects 自定义 and the conditional field carries the exact text, so an old
        // free-form target survives untouched.
        private void PopulateTarget(string value)
        {
            bool preset = Array.IndexOf(TargetLanguages.Common, value) >= 0;
            fillingTarget = true;
            Ui.Get<ComboBox>(scope, "TargetLanguageCombo").SelectedItem = preset ? value : TargetLanguages.Custom;
            Ui.Get<TextBox>(scope, "TargetInput").Text = value ?? "";
            fillingTarget = false;
            Ui.Visible(Ui.Get<Grid>(scope, "TargetCustomPanel"), !preset);
        }

        private void TargetLanguageChanged()
        {
            string selected = Convert.ToString(Ui.Get<ComboBox>(scope, "TargetLanguageCombo").SelectedItem);
            bool custom = selected == TargetLanguages.Custom;
            Ui.Visible(Ui.Get<Grid>(scope, "TargetCustomPanel"), custom);
            if (!custom) {
                fillingTarget = true; Ui.Get<TextBox>(scope, "TargetInput").Text = selected; fillingTarget = false;
            }
            SchedulePreferences();
        }

        private void BuildFontEntries()
        {
            fontEntries.Clear();
            fontEntries.Add(new FontEntry { Value = "", Label = "思源黑体 SC（内置）" });
            foreach (string name in Ui.InstalledTextFonts()) fontEntries.Add(new FontEntry { Value = name, Label = name });
        }

        // A stored name that is no longer installed falls back to the embedded default and says
        // so without blocking; the next real pick overwrites the stale value.
        private void PopulateFont(string value)
        {
            FontEntry match = null;
            foreach (var entry in fontEntries) if (entry.Value == (value ?? "")) { match = entry; break; }
            Ui.Get<ComboBox>(scope, "FontCombo").SelectedItem = match ?? (fontEntries.Count > 0 ? fontEntries[0] : null);
            if (match == null && !string.IsNullOrWhiteSpace(value))
                Status("未找到字体「" + value + "」，已使用内置思源黑体 SC。", false);
        }

        private void FontChanged()
        {
            var entry = Ui.Get<ComboBox>(scope, "FontCombo").SelectedItem as FontEntry;
            if (entry == null) return;
            bool missing;
            Ui.ApplyPaperFont(entry.Value, out missing);
            if (missing) Status("未找到字体「" + entry.Value + "」，已使用内置思源黑体 SC。", false);
            SchedulePreferences();
        }

        internal void SceneFields()
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

        internal void CapturePreferences()
        {
            string target = Convert.ToString(Ui.Get<ComboBox>(scope, "TargetLanguageCombo").SelectedItem);
            draft.TargetLanguage = target == TargetLanguages.Custom ? Ui.Get<TextBox>(scope, "TargetInput").Text.Trim() : target;
            var font = Ui.Get<ComboBox>(scope, "FontCombo").SelectedItem as FontEntry;
            draft.FontFamilyName = font == null ? "" : font.Value;
            draft.Scene = Convert.ToString(Ui.Get<ComboBox>(scope, "SceneCombo").SelectedItem);
            draft.SceneDetail = draft.Scene == "通用" ? "" : Ui.Get<TextBox>(scope, "SceneDetailInput").Text.Trim();
            if (draft.Scene != "通用") draft.SceneDetails[draft.Scene] = draft.SceneDetail;
            draft.GameTextType = Convert.ToString(Ui.Get<ComboBox>(scope, "GameTypeCombo").SelectedItem);
            draft.Style = Convert.ToString(Ui.Get<ComboBox>(scope, "StyleCombo").SelectedItem);
        }

        internal void DrawPresets()
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
    }
}
