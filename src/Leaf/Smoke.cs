using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
        // The original editor must be a top-aligned multi-line field whose placeholder shares
        // the input's typography and starts exactly where the first character is drawn.
        private static void EditorState(AppShell shell, List<string> checks)
        {
            var popup = shell.Popup;
            var editor = Ui.Get<TextBox>(popup, "SourceInput");
            var placeholder = Ui.Get<TextBox>(popup, "SourcePlaceholder");
            var button = Ui.Get<Button>(popup, "TranslateButton");
            popup.Width = 360; popup.Height = 380; shell.UpdatePopupTypography();
            shell.BeginSourceEdit(false);
            editor.Clear();
            ((FrameworkElement)popup.Content).UpdateLayout();
            Check(editor.FontSize == placeholder.FontSize && editor.FontFamily.ToString() == placeholder.FontFamily.ToString(),
                "Popup input and placeholder share typography", checks);
            Check(editor.VerticalContentAlignment == VerticalAlignment.Top,
                "Popup editor aligns text at the top instead of single-line centring", checks);
            Check(popup.FindName("SourceEditHint") == null && popup.FindName("EmptyHint") == null && popup.FindName("SourceActions") == null,
                "The original editor carries no permanent keyboard tutorial", checks);
            Check(button.Visibility == Visibility.Collapsed, "An empty input hides its submit action", checks);
            Check(System.Windows.Automation.AutomationProperties.GetName(button) == "翻译原文" &&
                System.Windows.Automation.AutomationProperties.GetName(Ui.Get<Button>(popup, "SendButton")) == "发送追问" &&
                System.Windows.Automation.AutomationProperties.GetName(Ui.Get<Button>(popup, "AskButton")) == "追问",
                "Reading actions carry assistive names", checks);

            // Compare what is actually painted: the placeholder's first lit pixel must be where
            // the same text is painted once it is typed into the editor.
            string sample = placeholder.Text;
            editor.Clear();
            ((FrameworkElement)popup.Content).UpdateLayout();
            var placeholderPixel = PaintedOrigin(placeholder);
            placeholder.Visibility = Visibility.Collapsed;
            editor.Text = sample;
            ((FrameworkElement)popup.Content).UpdateLayout();
            var textPixel = PaintedOrigin(editor);
            editor.Clear();
            placeholder.Visibility = Visibility.Visible;
            ((FrameworkElement)popup.Content).UpdateLayout();
            Check(placeholderPixel.X >= 0 && textPixel.X >= 0 &&
                Math.Abs(placeholderPixel.X - textPixel.X) <= 1 && Math.Abs(placeholderPixel.Y - textPixel.Y) <= 1,
                "Placeholder paints where the same text lands within one DIP (placeholder " + placeholderPixel + " vs typed text " + textPixel + ")", checks);
            Check(editor.AcceptsReturn && editor.TextWrapping == TextWrapping.Wrap, "The original editor stays a multi-line field", checks);
            editor.Text = "M";
            ((FrameworkElement)popup.Content).UpdateLayout();
            Check(button.Visibility == Visibility.Visible, "A non-empty input reveals its submit action", checks);
            editor.Clear();
            ((FrameworkElement)popup.Content).UpdateLayout();

            // Words in the original are reachable and activatable from the keyboard.
            var links = new List<System.Windows.Documents.Hyperlink>();
            foreach (var block in Ui.Get<RichTextBox>(popup, "SourceText").Document.Blocks.OfType<System.Windows.Documents.Paragraph>())
                foreach (var inline in block.Inlines.OfType<System.Windows.Documents.Hyperlink>()) links.Add(inline);
            Check(links.Count > 0 && links.All(x => x.Focusable && x.ToolTip != null),
                "Original words are keyboard reachable and explain themselves on demand", checks);
            var linkStyle = (Style)Application.Current.Resources[typeof(System.Windows.Documents.Hyperlink)];
            Check(linkStyle.Triggers.OfType<Trigger>().Any(x => x.Property == UIElement.IsKeyboardFocusedProperty),
                "Focused words get a visible state without moving focus on open", checks);
            Check(!popup.ShowActivated, "Refining the editor never makes the popup activate itself", checks);
        }
        // The content is arranged exactly like Ui.Render does, so geometry reads are real.
        private static void Layout(Window window)
        {
            var element = (FrameworkElement)window.Content;
            element.Measure(new Size(window.Width, window.Height));
            element.Arrange(new Rect(0, 0, window.Width, window.Height));
            element.UpdateLayout();
        }
        // Top-left of the first painted pixel of one element on a white backdrop, at 96 DPI.
        private static Point PaintedOrigin(FrameworkElement element)
        {
            int width = (int)Math.Ceiling(element.ActualWidth), height = (int)Math.Ceiling(element.ActualHeight);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("Element has no layout to inspect.");
            var backdrop = new System.Windows.Media.DrawingVisual();
            using (var drawing = backdrop.RenderOpen())
                drawing.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, width, height));
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(backdrop); bitmap.Render(element);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            int left = -1, top = -1;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++) {
                    int i = (y * width + x) * 4;
                    if (pixels[i] < 240 || pixels[i + 1] < 240 || pixels[i + 2] < 240) {
                        if (top < 0) top = y;
                        if (left < 0 || x < left) left = x;
                    }
                }
            return new Point(left, top);
        }
        // Every rendered state is entered through the application's own path - the demo record
        // the showcase shows, the real follow-up entry, a real request in flight, a real stop
        // and same-source recovery - so the images are the application's states rather than
        // visibilities this renderer set for itself.
        private static void RenderStates(AppShell shell, PendingTransport transport, string folder, List<string> checks)
        {
            var popup = shell.Popup;
            // A real request needs a chosen model; the settings checks that follow describe a
            // first run, so the profile is put back exactly as it was afterwards.
            var firstRun = Json.Copy(shell.Store.Settings);
            var sizes = new[] {
                new object[] { "default", 456.0, 620.0 }, new object[] { "compact", 360.0, 380.0 }
            };
            foreach (var size in sizes) {
                string name = (string)size[0];
                popup.Width = (double)size[1]; popup.Height = (double)size[2]; shell.UpdatePopupTypography();
                var ask = Ui.Get<Button>(popup, "AskButton");

                // The reading page of a real record, entered through the demo path the
                // application itself uses.
                shell.PopulateDemo();
                Layout(popup);
                var record = shell.Current;
                Check(Ui.Get<TextBlock>(popup, "TranslationCaption").Text == "译文" &&
                    Ui.Get<Grid>(popup, "SourceEditor").Visibility == Visibility.Collapsed &&
                    Ui.Get<RichTextBox>(popup, "SourceText").Visibility == Visibility.Visible &&
                    Ui.Get<Border>(popup, "InputPanel").Visibility == Visibility.Collapsed,
                    "The " + name + " reading state shows the translation caption, the original and no editor", checks);
                Check(ask.Visibility == Visibility.Visible && ask.IsEnabled,
                    "The " + name + " reading state keeps a visible, enabled follow-up entry", checks);
                Check(Ui.Get<Border>(popup, "ChatPanel").Visibility == (record.Chat.Count > 0 ? Visibility.Visible : Visibility.Collapsed) &&
                    Ui.Get<Border>(popup, "WordPanel").Visibility == Visibility.Visible,
                    "The " + name + " reading state shows the selected word card and only the conversation that exists", checks);
                Capture(popup, folder, name + "-reading", checks);

                // Opening the follow-up is the production click the user makes.
                ask.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout(popup);
                Check(Ui.Get<Border>(popup, "InputPanel").Visibility == Visibility.Visible &&
                    ask.Visibility == Visibility.Collapsed &&
                    Ui.Get<Button>(popup, "SendButton").Visibility == Visibility.Visible &&
                    Ui.Get<Button>(popup, "SendButton").IsEnabled,
                    "The " + name + " follow-up state is opened by its own entry, which steps aside while it is expanded", checks);
                Check(!shell.Pinned, "Asking never pins the popup", checks);
                var question = Ui.Get<TextBox>(popup, "QuestionInput");
                var content = (FrameworkElement)popup.Content;
                double questionBottom = question.TranslatePoint(new Point(0, question.ActualHeight), content).Y;
                Check(question.ActualHeight > 0 && questionBottom <= content.ActualHeight,
                    "The follow-up input fits inside the " + name + " popup (" + questionBottom.ToString("0.#") + " of " + content.ActualHeight.ToString("0.#") + ")", checks);
                Check(Ui.Get<ScrollViewer>(popup, "BodyScroll").ActualHeight > 0,
                    "The body still scrolls to word cards and the conversation behind an expanded follow-up", checks);
                Capture(popup, folder, name + "-followup", checks);

                // Collapsing it is the production click too, and the entry has to return: a
                // reading page with no way to ask again is not a state the user can reach.
                Ui.Get<Button>(popup, "CloseQuestionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout(popup);
                Check(Ui.Get<Border>(popup, "InputPanel").Visibility == Visibility.Collapsed &&
                    ask.Visibility == Visibility.Visible && ask.IsEnabled,
                    "The " + name + " popup comes back to a reading page that still offers its follow-up entry", checks);

                // The manual editor is the page the user opens, cleared by the user.
                shell.BeginSourceEdit(false);
                var editor = Ui.Get<TextBox>(popup, "SourceInput");
                editor.Clear();
                Layout(popup);
                string emptyPath = Capture(popup, folder, name + "-input-empty", checks);
                // An empty input must not paint the submit arrow; a filled one must.
                Check(CountInk(emptyPath, 60, 140) == 0, "An empty input paints no submit arrow", checks);
                editor.Text = "编辑中的原文示例";
                Layout(popup);
                string filledPath = Capture(popup, folder, name + "-input-filled", checks);
                Check(CountInk(filledPath, 60, 140) > 10, "A filled input paints the vector submit arrow", checks);
                editor.Clear();

                // A real request in flight: the fixture transport never answers, so the busy
                // state in the image is the application waiting for its own request.
                var configured = Json.Copy(shell.Store.Settings);
                configured.Provider.Model = "fixture-model";
                shell.Store.SaveSettings(configured);
                int requests = transport.Requests;
                shell.TranslateAsync("A render-state fixture sentence.", "选中文字", false);
                Layout(popup);
                Check(transport.Requests == requests + 1 &&
                    Ui.Get<Grid>(popup, "BusyPanel").Visibility == Visibility.Visible &&
                    Ui.Get<TextBlock>(popup, "BusyLabel").Text == "正在翻译…" &&
                    Ui.Get<Button>(popup, "CancelButton").Visibility == Visibility.Visible,
                    "The " + name + " sending state is a real translation in flight", checks);
                Capture(popup, folder, name + "-sending", checks);

                // A real stop through the same entry the user clicks.
                shell.StopForRetry();
                Layout(popup);
                Check(Ui.Get<Grid>(popup, "BusyPanel").Visibility == Visibility.Collapsed &&
                    Ui.Get<Border>(popup, "ErrorPanel").Visibility == Visibility.Visible &&
                    Ui.Get<Button>(popup, "RetryButton").Visibility == Visibility.Visible &&
                    Ui.Get<TextBlock>(popup, "ErrorText").Text.Contains("已停止"),
                    "The " + name + " error state is a real stop that offers its retry entry", checks);
                Capture(popup, folder, name + "-error", checks);

                // Same-source recovery: the production path a repeated shortcut takes with a
                // cleared draft. It has to return to the reading page without an API call.
                var restored = TranslationRecord.Create("Her steady reading habit pays off.", "选中文字", shell.Store.Settings);
                restored.Translation = "她稳定的阅读习惯带来了回报。"; restored.Completed = true;
                restored.Chat.Add(new ChatTurn { Role = "user", Content = "夹具问题", Topic = "原句" });
                restored.Chat.Add(new ChatTurn { Role = "assistant", Content = "夹具回答", Topic = "原句" });
                shell.OpenRecord(restored);
                shell.BeginSourceEdit(false);
                editor.Clear();
                int calls = transport.Requests;
                shell.TranslateAsync(restored.Source, "选中文字", false).GetAwaiter().GetResult();
                Layout(popup);
                Check(ReferenceEquals(shell.Current, restored) && transport.Requests == calls,
                    "Recovering the same text restores the existing record without another API call", checks);
                Check(Ui.Get<TextBlock>(popup, "TranslationCaption").Text == "译文" &&
                    Ui.Get<Grid>(popup, "SourceEditor").Visibility == Visibility.Collapsed &&
                    ask.Visibility == Visibility.Visible && ask.IsEnabled,
                    "The recovered " + name + " reading page shows the translation caption and its follow-up entry", checks);
                Check(Ui.Get<Border>(popup, "ChatPanel").Visibility == (restored.Chat.Count > 0 ? Visibility.Visible : Visibility.Collapsed) &&
                    Ui.Get<Border>(popup, "WordPanel").Visibility == Visibility.Collapsed &&
                    Ui.Get<Border>(popup, "InputPanel").Visibility == Visibility.Collapsed,
                    "The recovered " + name + " page shows the conversation it has and no word card that is not selected", checks);
                Capture(popup, folder, name + "-restored", checks);
            }
            // Leave the first-run profile and the demo record in place: the settings and history
            // checks that follow expect them.
            shell.Store.SaveSettings(firstRun);
            shell.PopulateDemo();
            popup.Width = 456; popup.Height = 620; shell.UpdatePopupTypography();
            checks.Add("All popup states render at 456x620 and 360x380 across 96/120/144/192 DPI with exact output pixels");
        }
        // A real stop and its retry: the application's own StopForRetry drives the busy panel,
        // the retry entry and the restored state, so the rendered images are not painted by
        // the renderer. The fixture transport answers nothing, so no request ever leaves.
        private static void StopState(AppShell shell, PendingTransport transport, string folder, List<string> checks)
        {
            var popup = shell.Popup;
            popup.Width = 456; popup.Height = 620; shell.UpdatePopupTypography();
            // A request needs a configured model; the fixture transport answers it, so this
            // stays offline while giving the shell one real operation in flight.
            var configured = Json.Copy(shell.Store.Settings);
            configured.Provider.Model = "fixture-model";
            shell.Store.SaveSettings(configured);
            shell.TranslateAsync("A stopped state fixture sentence.", "选中文字", false);
            Layout(popup);
            Check(Ui.Get<Grid>(popup, "BusyPanel").Visibility == Visibility.Visible &&
                Ui.Get<Button>(popup, "RetryButton").Visibility == Visibility.Collapsed,
                "A running translation shows the busy panel and no retry entry", checks);
            Capture(popup, folder, "running", checks);

            shell.StopForRetry();
            Layout(popup);
            Check(Ui.Get<Grid>(popup, "BusyPanel").Visibility == Visibility.Collapsed &&
                Ui.Get<Border>(popup, "ErrorPanel").Visibility == Visibility.Visible &&
                Ui.Get<Button>(popup, "RetryButton").Visibility == Visibility.Visible,
                "A real stop clears the busy panel and offers its retry entry", checks);
            Check(Ui.Get<TextBlock>(popup, "ErrorText").Text.Contains("已停止"),
                "A real stop says it stopped instead of failing silently", checks);
            Check(Ui.Get<Border>(popup, "InputPanel").Visibility == Visibility.Collapsed,
                "A real stop neither pins nor expands the follow-up input", checks);
            Capture(popup, folder, "stopped", checks);

            int before = transport.Requests;
            Ui.Get<Button>(popup, "RetryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(transport.Requests == before + 1,
                "The rendered retry entry repeats the stopped translation exactly once", checks);
            Layout(popup);
            Check(Ui.Get<Grid>(popup, "BusyPanel").Visibility == Visibility.Visible &&
                Ui.Get<Border>(popup, "ErrorPanel").Visibility == Visibility.Collapsed,
                "A retried translation returns to the busy panel with no error left behind", checks);
            Capture(popup, folder, "restarted", checks);

            shell.StopForRetry();
            Check(Ui.Get<Grid>(popup, "BusyPanel").Visibility == Visibility.Collapsed,
                "Stopping the retried translation clears its busy state again", checks);
        }
        // Answers nothing and never reaches the network: it keeps one operation in flight so
        // the stop and retry states come from application state.
        private sealed class PendingTransport : HttpMessageHandler
        {
            public int Requests;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                Requests++;
                var pending = new TaskCompletionSource<HttpResponseMessage>();
                cancellation.Register(() => pending.TrySetCanceled());
                return pending.Task;
            }
        }
        private static string Capture(Window window, string folder, string name, List<string> checks)
        {
            string first = null;
            foreach (double dpi in new[] { 96.0, 120.0, 144.0, 192.0 }) {
                string path = Path.Combine(folder, "popup-" + name + "-" + ((int)dpi) + ".png");
                Ui.Render(window, path, dpi);
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit(); image.UriSource = new Uri(path);
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; image.EndInit();
                int width = (int)Math.Ceiling(window.Width * dpi / 96), height = (int)Math.Ceiling(window.Height * dpi / 96);
                if (image.PixelWidth != width || image.PixelHeight != height)
                    throw new InvalidOperationException("Rendered " + name + " at " + dpi + " DPI is " + image.PixelWidth + "x" + image.PixelHeight +
                        " instead of " + width + "x" + height + ".");
                if (first == null) first = path;
            }
            return first;
        }
        // Reads the written PNG back and counts dark (Ink) pixels in the right-hand action
        // band, so the rendered arrow is inspected rather than only produced.
        private static int CountInk(string path, int fromY, int toY)
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit(); image.UriSource = new Uri(path);
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; image.EndInit();
            var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            int count = 0;
            for (int y = fromY; y < Math.Min(toY, converted.PixelHeight); y++)
                for (int x = Math.Max(0, converted.PixelWidth - 130); x < converted.PixelWidth - 20; x++) {
                    int i = y * stride + x * 4;
                    if (pixels[i] < 120 && pixels[i + 1] < 120 && pixels[i + 2] < 120) count++;
                }
            return count;
        }
        public static int Run(string folder)
        {
            Directory.CreateDirectory(folder);
            var checks = new List<string>(); var watch = Stopwatch.StartNew();
            try {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; Ui.InitializeTheme();
                var store = new LocalStore(Path.Combine(folder, "data"));
                store.Clear(); store.SaveSettings(Settings.Defaults());
                var transport = new PendingTransport();
                using (var shell = new AppShell(store, false, new LlmClient(transport))) {
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
                    // The compact screenshot is the state the production collapse click leaves
                    // behind, so it keeps the follow-up entry a user can still reach.
                    Ui.Get<Button>(shell.Popup, "CloseQuestionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(Ui.Get<Border>(shell.Popup, "InputPanel").Visibility == Visibility.Collapsed &&
                        Ui.Get<Button>(shell.Popup, "AskButton").Visibility == Visibility.Visible &&
                        Ui.Get<Button>(shell.Popup, "AskButton").IsEnabled,
                        "Collapsing the follow-up restores the compact popup's follow-up entry", checks);
                    Ui.Render(shell.Popup, Path.Combine(folder, "popup-small.png"));
                    shell.Popup.Width = 700; shell.Popup.Height = 900; shell.UpdatePopupTypography();
                    Check(translation.FontSize == 17, "Large popup never excessively enlarges the text", checks);
                    Ui.Render(shell.Popup, Path.Combine(folder, "popup-large.png"));
                    shell.Popup.Width = 456; shell.Popup.Height = 620; shell.UpdatePopupTypography();
                    Check(translation.FontSize == 17, "Resizing back restores the original typography without cumulative scaling", checks);

                    EditorState(shell, checks);
                    RenderStates(shell, transport, folder, checks);

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
                    StopState(shell, transport, folder, checks);
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
