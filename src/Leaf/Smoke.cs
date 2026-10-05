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
                    Ui.Render(shell.Popup, Path.Combine(folder, "popup.png"));
                    Ui.Get<Button>(shell.Popup, "AskButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(Ui.Get<Border>(shell.Popup, "InputPanel").Visibility == Visibility.Visible, "Ask reveals the input", checks);
                    Check(!shell.Pinned, "Asking does not pin the popup", checks);
                    Check(Ui.Get<TextBlock>(shell.Popup, "TopicLabel").Text.Contains("prowess"), "Follow-up identifies the selected word", checks);
                    Ui.Render(shell.Popup, Path.Combine(folder, "popup-question.png"));

                    var settings = new SettingsWindow(shell);
                    var scene = Ui.Get<ComboBox>(settings.Window, "SceneCombo");
                    scene.SelectedItem = "游戏";
                    Check(Ui.Get<StackPanel>(settings.Window, "GameTypePanel").Visibility == Visibility.Visible, "Game fields appear only in the game scene", checks);
                    Ui.Get<TextBox>(settings.Window, "SceneDetailInput").Text = "Example game";
                    scene.SelectedItem = "书籍";
                    Check(Ui.Get<StackPanel>(settings.Window, "GameTypePanel").Visibility == Visibility.Collapsed, "Game fields disappear in books", checks);
                    Check(Ui.Get<TextBox>(settings.Window, "SceneDetailInput").Text.Length == 0, "Scene-specific text is cleared when changing scene", checks);
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
