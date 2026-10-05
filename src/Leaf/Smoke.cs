using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Leaf
{
    public static class Smoke
    {
        private static void Check(bool condition, string message, List<string> checks)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        public static int Run(string folder)
        {
            Directory.CreateDirectory(folder);
            var checks = new List<string>(); var watch = Stopwatch.StartNew();
            try {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Ui.InitializeTheme();
                var store = new LocalStore(Path.Combine(folder, "data"));
                store.Clear(); store.SaveSettings(Settings.Defaults());
                using (var shell = new AppShell(store, false)) {
                    shell.PopulateDemo();
                    Check(Ui.Get<Border>(shell.Popup, "InputPanel").Visibility == Visibility.Collapsed, "Input is hidden before Ask", checks);
                    Check(Ui.Get<Border>(shell.Popup, "WordPanel").Visibility == Visibility.Visible, "Word card is visible for the selected occurrence", checks);
                    Check(shell.Popup.FindName("SceneCombo") == null, "Popup contains no scene picker", checks);
                    Check(!shell.Popup.ShowActivated, "Popup does not activate on Show", checks);
                    Check(shell.Popup.ShowInTaskbar && shell.Popup.Icon != null, "Popup has a branded taskbar entry", checks);
                    Ui.Render(shell.Popup, Path.Combine(folder, "popup.png"));
                    Ui.Get<Button>(shell.Popup, "AskButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(Ui.Get<Border>(shell.Popup, "InputPanel").Visibility == Visibility.Visible, "Ask reveals the input", checks);
                    Check(!shell.Pinned, "Asking does not pin the popup", checks);
                    Check(Ui.Get<TextBlock>(shell.Popup, "TopicLabel").Text.Contains("prowess"), "Follow-up identifies the selected word", checks);
                    Ui.Render(shell.Popup, Path.Combine(folder, "popup-question.png"));
                    shell.Popup.Width = 360; shell.Popup.Height = 380; shell.UpdatePopupTypography();
                    var translation = Ui.Get<RichTextBox>(shell.Popup, "TranslationText");
                    Check(translation.FontSize < 17 && translation.FontSize >= 14, "Compact popup reduces translation typography within readable bounds", checks);
                    Check(translation.IsReadOnly && shell.Popup.FindName("CopyButton") == null, "Translation supports selection with no redundant copy button", checks);
                    Check(shell.Popup.FindName("SettingsButton") != null, "Settings is available beside pin in the popup", checks);
                    Check(Ui.Get<RichTextBox>(shell.Popup, "SourceText").Document.FontSize < 16, "Compact popup scales the FlowDocument source too", checks);
                    Check(Ui.Get<StackPanel>(shell.Popup, "LearningSections").Children.OfType<TextBlock>().All(x => x.FontSize >= 11), "Learning text has a readable minimum", checks);
                    Ui.Render(shell.Popup, Path.Combine(folder, "popup-small-question.png"));
                    Ui.Visible(Ui.Get<Border>(shell.Popup, "InputPanel"), false);
                    Ui.Render(shell.Popup, Path.Combine(folder, "popup-small.png"));
                    shell.Popup.Width = 700; shell.Popup.Height = 900; shell.UpdatePopupTypography();
                    Check(translation.FontSize == 17, "Large popup never excessively enlarges the text", checks);
                    Ui.Render(shell.Popup, Path.Combine(folder, "popup-large.png"));
                    shell.Popup.Width = 456; shell.Popup.Height = 620; shell.UpdatePopupTypography();
                    Check(translation.FontSize == 17, "Resizing back restores the original typography without cumulative scaling", checks);

                    var settings = new SettingsWindow(shell);
                    Check(Ui.Get<ComboBox>(settings.Window, "ProviderCombo").SelectedIndex == -1, "New users choose an endpoint explicitly rather than guessing from a key", checks);
                    Ui.Get<ComboBox>(settings.Window, "ProviderCombo").SelectedIndex = 0;
                    Check(settings.Window.FindName("MonitorCombo") == null, "Popup-position selection is removed from settings", checks);
                    var model = Ui.Get<ComboBox>(settings.Window, "ModelInput");
                    Check(model.IsEditable, "Model selection accepts a manual ID", checks);
                    Check(Ui.Get<TextBox>(settings.Window, "ShortcutInput").IsReadOnly, "Shortcut settings record keys instead of accepting typed names", checks);
                    Check(Ui.Get<CheckBox>(settings.Window, "ClipboardModeCheck").IsChecked == false, "The default clipboard checkbox is off", checks);
                    model.Text = "glm-5.3-flash";
                    Check(!Ui.Get<ComboBoxItem>(settings.Window, "DisableThinkingOption").IsEnabled && Ui.Get<StackPanel>(settings.Window, "ReasoningPanel").Visibility == Visibility.Visible,
                        "GLM-5.3 advanced options explain mandatory reasoning and expose effort", checks);
                    model.Text = "example-model";
                    var scene = Ui.Get<ComboBox>(settings.Window, "SceneCombo");
                    scene.SelectedItem = "游戏";
                    Check(Ui.Get<StackPanel>(settings.Window, "GameTypePanel").Visibility == Visibility.Visible, "Game fields appear only in the game scene", checks);
                    Ui.Get<TextBox>(settings.Window, "SceneDetailInput").Text = "Example game";
                    scene.SelectedItem = "书籍";
                    Check(Ui.Get<StackPanel>(settings.Window, "GameTypePanel").Visibility == Visibility.Collapsed, "Game fields disappear in books", checks);
                    Check(Ui.Get<TextBox>(settings.Window, "SceneDetailInput").Text.Length == 0, "A new scene starts with its own empty detail", checks);
                    scene.SelectedItem = "游戏";
                    Check(Ui.Get<TextBox>(settings.Window, "SceneDetailInput").Text == "Example game", "Changing back restores the scene's remembered name", checks);
                    scene.SelectedItem = "通用";
                    Check(Ui.Get<StackPanel>(settings.Window, "SceneDetailPanel").Visibility == Visibility.Collapsed, "General scene has no irrelevant detail field", checks);
                    Ui.Get<TextBox>(settings.Window, "CustomNameInput").Text = "常见误用";
                    Ui.Get<TextBox>(settings.Window, "CustomInstructionInput").Text = "解释容易混淆的用法。";
                    Ui.Get<Button>(settings.Window, "AddCustom").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(Ui.Get<WrapPanel>(settings.Window, "LearningChoices").Children.Count == 6, "Custom preference becomes a selectable option", checks);
                    scene.SelectedItem = "游戏";
                    Ui.Get<TextBox>(settings.Window, "PresetNameInput").Text = "游戏学习";
                    Ui.Get<Button>(settings.Window, "SavePreset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    scene.SelectedItem = "通用";
                    Ui.Get<Button>(settings.Window, "LoadPreset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(Convert.ToString(scene.SelectedItem) == "游戏", "Preference preset restores the scene", checks);
                    Ui.Render(settings.Window, Path.Combine(folder, "settings.png"));
                    settings.Window.Close();

                    store.Save(shell.Current);
                    var other = TranslationRecord.Create("Elle a pris son temps.", "选中文字", Settings.Defaults());
                    other.Translation = "她不紧不慢地完成了。"; other.Completed = true; store.Save(other);
                    var german = TranslationRecord.Create("Das kommt darauf an.", "剪贴板", Settings.Defaults());
                    german.Translation = "这要看情况。"; german.Completed = true; store.Save(german);
                    var history = new HistoryWindow(shell);
                    Check(Ui.Get<ListBox>(history.Window, "HistoryList").Items.Count == 3, "History displays saved translations", checks);
                    Ui.Get<TextBox>(history.Window, "HistorySearch").Text = "prowess";
                    Check(Ui.Get<ListBox>(history.Window, "HistoryList").Items.Count == 1, "History search filters original text", checks);
                    Ui.Get<TextBox>(history.Window, "HistorySearch").Text = "看情况";
                    Check(Ui.Get<ListBox>(history.Window, "HistoryList").Items.Count == 1, "History search filters translated text", checks);
                    Ui.Get<TextBox>(history.Window, "HistorySearch").Clear();
                    Ui.Render(history.Window, Path.Combine(folder, "history.png"));
                    history.Window.Close();
                    shell.Forget(null);
                    Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Visible, "Empty popup is ready for manual input", checks);
                    Ui.Render(shell.Popup, Path.Combine(folder, "popup-input.png"));
                }
                watch.Stop();
                File.WriteAllText(Path.Combine(folder, "result.json"), Json.Write(new {
                    success = true, checks = checks, elapsed_ms = watch.ElapsedMilliseconds,
                    renderer_working_set_mb = Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1048576.0, 1),
                    note = "UI rendered in-process with fixtures. This is not a live API, desktop selection, DPI or idle-memory benchmark."
                }));
                application.Shutdown(); return 0;
            } catch (Exception error) {
                File.WriteAllText(Path.Combine(folder, "result.json"), Json.Write(new { success = false, checks = checks, error = error.ToString() }));
                return 1;
            }
        }
    }
}
