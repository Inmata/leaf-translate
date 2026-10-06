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
using System.Windows.Media;
using System.Windows.Threading;

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

            // The read-only original is a plain text box: reachable from the keyboard, never
            // editable, and every word explains itself on demand.
            var reading = Ui.Get<TextBox>(popup, "SourceText");
            Check(reading.Focusable && reading.IsReadOnly && reading.Text.Length > 0 &&
                reading.SelectionLength == 0,
                "The original is a keyboard-reachable read-only box whose words explain on demand", checks);
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
        // The settings page keeps its sections in expanders that start collapsed, so reaching a
        // control the user can only see after opening one means opening its whole chain.
        private static void ExpandAncestors(DependencyObject element)
        {
            for (int depth = 0; element != null && depth < 32; depth++) {
                var expander = element as Expander;
                if (expander != null) expander.IsExpanded = true;
                element = LogicalTreeHelper.GetParent(element);
            }
        }
        // Dark pixels inside one rectangle of an already written 96 DPI PNG, so a state that is
        // supposed to show a control is inspected in the image rather than only produced.
        private static int CountInkInRect(string path, Rect box)
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit(); image.UriSource = new Uri(path);
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; image.EndInit();
            var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            int fromY = Math.Max(0, (int)Math.Floor(box.Top)), toY = Math.Min(converted.PixelHeight, (int)Math.Ceiling(box.Bottom));
            int fromX = Math.Max(0, (int)Math.Floor(box.Left)), toX = Math.Min(converted.PixelWidth, (int)Math.Ceiling(box.Right));
            int count = 0;
            for (int y = fromY; y < toY; y++)
                for (int x = fromX; x < toX; x++) {
                    int i = y * stride + x * 4;
                    if (pixels[i] < 200 && pixels[i + 1] < 200 && pixels[i + 2] < 200) count++;
                }
            return count;
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
                    Ui.Get<TextBox>(popup, "SourceText").Visibility == Visibility.Visible &&
                    Ui.Get<Border>(popup, "InputPanel").Visibility == Visibility.Collapsed,
                    "The " + name + " reading state shows the translation caption, the original and no editor", checks);
                Check(ask.Visibility == Visibility.Visible && ask.IsEnabled,
                    "The " + name + " reading state keeps a visible, enabled follow-up entry", checks);
                Check(Ui.Get<Border>(popup, "ChatPanel").Visibility == (record.Chat.Count > 0 ? Visibility.Visible : Visibility.Collapsed) &&
                    Ui.Get<Border>(popup, "WordPanel").Visibility == Visibility.Visible,
                    "The " + name + " reading state shows the selected word card and only the conversation that exists", checks);
                Capture(popup, folder, name + "-reading", checks);
                if (name == "default")
                    Check(shell.WordHighlighted, "The reading state keeps a highlight for the selected word", checks);

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
        // The settings page hosted in the popup shell and the previous-session back entry.
        // Both are entered through the application's own bridges, so the images and the
        // restored states come from production paths rather than renderer manipulation.
        private static async Task NavigationState(AppShell shell, PendingTransport transport, string folder, List<string> checks)
        {
            var popup = shell.Popup;
            popup.Width = 456; popup.Height = 620; shell.UpdatePopupTypography();

            // The reading page of the demo session, then the settings page on top of it.
            // The settings scenario saved a preset and restored the first-run profile, so the
            // empty-state check needs a fresh list and the internal-selection request below
            // needs a real model, exactly like the render states configured one.
            var noPresets = Json.Copy(shell.Store.Settings);
            noPresets.Presets.Clear();
            noPresets.Provider.Model = "fixture-model";
            shell.Store.SaveSettings(noPresets);
            shell.PopulateDemo();
            Layout(popup);
            Ui.Get<TextBox>(popup, "QuestionInput").Text = "离开前的追问草稿";
            string badgeBefore = Ui.Get<TextBlock>(popup, "SourceBadge").Text;
            shell.ShowSettingsPage();
            Layout(popup);
            Check(shell.IsSettingsPageOpen &&
                Ui.Get<Grid>(popup, "TranslatePage").Visibility == Visibility.Collapsed &&
                Ui.Get<Grid>(popup, "SettingsPage").Visibility == Visibility.Visible &&
                Ui.Get<TextBlock>(popup, "SourceBadge").Text.Length > 0 &&
                Ui.Get<TextBlock>(popup, "SourceBadge").Text == badgeBefore,
                "The settings page opens inside the popup shell without rebuilding the reading page", checks);
            var panel = (FrameworkElement)Ui.Get<ContentControl>(popup, "SettingsPageHost").Content;
            var placeholder = Ui.Get<TextBlock>(panel, "PresetPlaceholder");
            Check(placeholder.Text == "暂无偏好预设" && !Ui.Get<ComboBox>(panel, "PresetCombo").IsEnabled &&
                !Ui.Get<Button>(panel, "LoadPreset").IsEnabled && !Ui.Get<Button>(panel, "RemovePreset").IsEnabled,
                "An empty preset list disables its dropdown and actions and says so", checks);
            Capture(popup, folder, "settings-page", checks);

            // The empty-preset state must be visible in an image, not only asserted: the preset
            // list sits in a collapsed expander, so the chain is opened and the placeholder is
            // scrolled into view before it is captured. One targeted page per size, on top of
            // the images above; no other state is rendered again here.
            foreach (var size in new[] { new object[] { "default", 456.0, 620.0 }, new object[] { "compact", 360.0, 380.0 } }) {
                string presetSize = (string)size[0];
                popup.Width = (double)size[1]; popup.Height = (double)size[2]; shell.UpdatePopupTypography();
                ExpandAncestors(placeholder);
                Layout(popup);
                placeholder.BringIntoView();
                Layout(popup);
                var content = (FrameworkElement)popup.Content;
                var origin = placeholder.TranslatePoint(new Point(0, 0), content);
                Check(placeholder.Visibility == Visibility.Visible && placeholder.ActualWidth > 0 && placeholder.ActualHeight > 0 &&
                    Ui.Get<ComboBox>(panel, "PresetCombo").ActualWidth > 0,
                    "The " + presetSize + " empty preset row has a laid out dropdown and placeholder", checks);
                Check(origin.X >= 0 && origin.Y >= 0 &&
                    origin.X + placeholder.ActualWidth <= content.ActualWidth && origin.Y + placeholder.ActualHeight <= content.ActualHeight,
                    "The " + presetSize + " empty-preset placeholder is scrolled inside the visible settings body", checks);
                string presetPath = Capture(popup, folder, "settings-presets-empty-" + presetSize, checks);
                var placeholderBox = new Rect(origin, new Size(placeholder.ActualWidth, placeholder.ActualHeight));
                Check(CountInkInRect(presetPath, placeholderBox) > 0,
                    "The " + presetSize + " empty-preset image paints its placeholder text instead of a blank area", checks);
            }
            popup.Width = 456; popup.Height = 620; shell.UpdatePopupTypography();

            bool left = await shell.TryLeaveSettingsPageAsync();
            Layout(popup);
            Check(left && !shell.IsSettingsPageOpen &&
                Ui.Get<Grid>(popup, "TranslatePage").Visibility == Visibility.Visible &&
                Ui.Get<Grid>(popup, "SettingsPage").Visibility == Visibility.Collapsed,
                "Leaving settings restores the same reading page", checks);
            Check(Ui.Get<Border>(popup, "WordPanel").Visibility == Visibility.Visible &&
                Ui.Get<TextBlock>(popup, "TopicLabel").Text.Contains("prowess") &&
                Ui.Get<TextBox>(popup, "QuestionInput").Text == "离开前的追问草稿" &&
                Ui.Get<TextBlock>(popup, "SourceBadge").Text == "演示内容 · 未调用 API",
                "Leaving settings keeps the session: card, topic, draft and badge survive", checks);

            // A preset list with an entry but no selection says so in the same row. The
            // controller preselects the first preset itself, so the selection is cleared
            // through the combo's own change event — the state the placeholder exists for —
            // and the row must be visible in both sizes like the empty one above.
            var onePreset = Json.Copy(shell.Store.Settings);
            onePreset.Presets.Add(new LearningPreset {
                Name = "游戏学习", TargetLanguage = "中文", Scene = "游戏", SceneDetail = "示例游戏",
                GameTextType = "动作", Style = "简洁", Learning = new List<LearningOption>()
            });
            shell.Store.SaveSettings(onePreset);
            shell.ShowSettingsPage();
            Layout(popup);
            var presetPanel = (FrameworkElement)Ui.Get<ContentControl>(popup, "SettingsPageHost").Content;
            var presetCombo = Ui.Get<ComboBox>(presetPanel, "PresetCombo");
            Check(shell.IsSettingsPageOpen && presetCombo.IsEnabled && presetCombo.Items.Count == 1 &&
                presetCombo.SelectedItem is LearningPreset,
                "Saved presets enable the dropdown and preselect the saved entry", checks);
            presetCombo.SelectedIndex = -1;
            Layout(popup);
            var unselectedHint = Ui.Get<TextBlock>(presetPanel, "PresetPlaceholder");
            Check(unselectedHint.Text == "未选择预设" && !Ui.Get<Button>(presetPanel, "LoadPreset").IsEnabled &&
                !Ui.Get<Button>(presetPanel, "RemovePreset").IsEnabled,
                "A preset list with entries but no selection says 未选择预设 and disables the actions", checks);
            foreach (var size in new[] { new object[] { "default", 456.0, 620.0 }, new object[] { "compact", 360.0, 380.0 } }) {
                string presetSize = (string)size[0];
                popup.Width = (double)size[1]; popup.Height = (double)size[2]; shell.UpdatePopupTypography();
                ExpandAncestors(unselectedHint);
                Layout(popup);
                unselectedHint.BringIntoView();
                Layout(popup);
                var hintPage = (FrameworkElement)popup.Content;
                var hintOrigin = unselectedHint.TranslatePoint(new Point(0, 0), hintPage);
                Check(unselectedHint.Visibility == Visibility.Visible && unselectedHint.ActualWidth > 0 && unselectedHint.ActualHeight > 0,
                    "The " + presetSize + " unselected-preset placeholder has a laid out row", checks);
                Check(hintOrigin.X >= 0 && hintOrigin.Y >= 0 &&
                    hintOrigin.X + unselectedHint.ActualWidth <= hintPage.ActualWidth && hintOrigin.Y + unselectedHint.ActualHeight <= hintPage.ActualHeight,
                    "The " + presetSize + " unselected-preset placeholder is scrolled inside the visible settings body", checks);
                string hintPath = Capture(popup, folder, "settings-preset-unselected-" + presetSize, checks);
                Check(CountInkInRect(hintPath, new Rect(hintOrigin, new Size(unselectedHint.ActualWidth, unselectedHint.ActualHeight))) > 0,
                    "The " + presetSize + " unselected-preset image paints its placeholder text instead of a blank area", checks);
            }
            popup.Width = 456; popup.Height = 620; shell.UpdatePopupTypography();
            left = await shell.TryLeaveSettingsPageAsync();
            Layout(popup);
            Check(left && !shell.IsSettingsPageOpen &&
                Ui.Get<TextBlock>(popup, "SourceBadge").Text == "演示内容 · 未调用 API",
                "Leaving the preset visit restores the same reading page and badge", checks);

            // Paired markdown emphasis is presentation-only: document renders bold while the
            // raw translation keeps its markers, so history and the copied text stay verbatim.
            var markdownRecord = TranslationRecord.Create("The uploaded document is detailed.", "选中文字", shell.Store.Settings);
            markdownRecord.Translation = "这份 **document** 值得细读。"; markdownRecord.Completed = true;
            shell.OpenRecord(markdownRecord);
            Layout(popup);
            var paragraph = Ui.Get<RichTextBox>(popup, "TranslationText").Document.Blocks.OfType<System.Windows.Documents.Paragraph>().First();
            var visible = new System.Windows.Documents.TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
            var bold = paragraph.Inlines.OfType<System.Windows.Documents.Run>()
                .FirstOrDefault(run => run.FontWeight == FontWeights.Bold && run.Text == "document");
            Check(!visible.Contains("**") && bold != null &&
                new System.Windows.Documents.TextRange(bold.ContentStart, bold.ContentEnd).Text == "document",
                "Paired markdown emphasis renders document in bold without its markers", checks);
            Check(visible.Contains("document") && markdownRecord.Translation.Contains("**document**"),
                "The raw translation keeps its markdown markers so history and copy stay verbatim", checks);

            // A labeled internal-selection record, scrolled, with a draft: the session that
            // will be replaced and then restored by the back entry.
            var restored = TranslationRecord.Create("She kept her evening reading habit.", "选中文字", shell.Store.Settings);
            restored.Translation = "她保持了晚间阅读的习惯。"; restored.Completed = true;
            for (int i = 0; i < 6; i++) {
                restored.Chat.Add(new ChatTurn { Role = "user", Content = "问题 " + i, Topic = "原句" });
                restored.Chat.Add(new ChatTurn { Role = "assistant", Content = "回答 " + i + "：这一段足够长，可以让正文产生滚动。", Topic = "原句" });
            }
            shell.OpenRecord(restored);
            Layout(popup);
            Check(Ui.Get<TextBlock>(popup, "SourceBadge").Text == "选中文本", "Internal selections are labeled as selected text", checks);
            var body = Ui.Get<ScrollViewer>(popup, "BodyScroll");
            body.ScrollToBottom(); Layout(popup);
            double savedOffset = body.VerticalOffset;
            Check(savedOffset > 0, "The replaced session's body scrolls before the switch", checks);
            Ui.Get<TextBox>(popup, "QuestionInput").Text = "上一次会话的追问草稿";
            int calls = transport.Requests;
            var ignored = shell.TranslateInternalSelectionAsync("Words selected inside Leaf itself.");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Layout(popup);
            Check(shell.Current != null && shell.Current.Source == "Words selected inside Leaf itself." &&
                !ReferenceEquals(shell.Current, restored) && transport.Requests == calls + 1,
                "An internal selection starts a new session with a real request", checks);
            Check(shell.CanReturnToPreviousSession &&
                Ui.Get<Button>(popup, "BackToPreviousButton").Visibility == Visibility.Visible,
                "The replaced session offers its back entry", checks);
            // The new internal session with that entry on screen, in the default size, so the
            // state a reviewer has to look at exists as an image and not only as a visibility
            // check. One capture of this state; no other state is rendered again here.
            var backEntry = Ui.Get<Button>(popup, "BackToPreviousButton");
            var pageContent = (FrameworkElement)popup.Content;
            var backOrigin = backEntry.TranslatePoint(new Point(0, 0), pageContent);
            Check(backOrigin.X >= 0 && backOrigin.Y >= 0 &&
                backOrigin.X + backEntry.ActualWidth <= pageContent.ActualWidth &&
                backOrigin.Y + backEntry.ActualHeight <= pageContent.ActualHeight,
                "The back entry of the new internal session is laid out inside the popup body", checks);
            string backPath = Capture(popup, folder, "internal-selection-back", checks);
            Check(CountInkInRect(backPath, new Rect(backOrigin, new Size(backEntry.ActualWidth, backEntry.ActualHeight))) > 0,
                "The new internal session's back entry paints in its image", checks);
            shell.ReturnToPreviousSession();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Layout(popup);
            Check(ReferenceEquals(shell.Current, restored) && transport.Requests == calls + 1,
                "Returning to the previous session restores it without another request", checks);
            Check(Math.Abs(body.VerticalOffset - savedOffset) <= 2 &&
                Ui.Get<TextBox>(popup, "QuestionInput").Text == "上一次会话的追问草稿",
                "Returning restores the body scroll and the follow-up draft", checks);
            Check(!shell.CanReturnToPreviousSession &&
                Ui.Get<Button>(popup, "BackToPreviousButton").Visibility == Visibility.Collapsed,
                "The back entry disappears once the previous session is restored", checks);

            // An invalid internal selection reports the problem and leaves the session alone.
            await shell.TranslateInternalSelectionAsync("   ");
            Check(Ui.Get<Border>(popup, "ErrorPanel").Visibility == Visibility.Visible && ReferenceEquals(shell.Current, restored),
                "An invalid internal selection reports the problem without touching the session", checks);
            Ui.Get<Button>(popup, "RetryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Ui.Get<Border>(popup, "ErrorPanel").Visibility == Visibility.Collapsed,
                "Clearing the failed internal selection leaves the reading page usable", checks);
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
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Ui.InitializeTheme();
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
            Exception failure = null;
            try {
                var task = RunScenariosAsync(folder, checks);
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
                timer.Tick += (s, e) => frame.Continue = false;
                timer.Start();
                task.ContinueWith(t => application.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
                Dispatcher.PushFrame(frame); timer.Stop();
                if (!task.IsCompleted) throw new Exception("UI smoke checks timed out.");
                task.GetAwaiter().GetResult();
            } catch (Exception error) { failure = error; }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            if (failure != null) {
                File.WriteAllText(Path.Combine(folder, "result.json"), Json.Write(new { success = false, checks = checks, error = failure.ToString() }));
                application.Shutdown(); return 1;
            }
            watch.Stop();
            File.WriteAllText(Path.Combine(folder, "result.json"), Json.Write(new {
                success = true, checks = checks, elapsed_ms = watch.ElapsedMilliseconds,
                renderer_working_set_mb = Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1048576.0, 1),
                note = "UI rendered in-process with fixtures. This is not a live API, desktop selection, DPI or idle-memory benchmark."
            }));
            application.Shutdown(); return 0;
        }
        private static async Task RunScenariosAsync(string folder, List<string> checks)
        {
            var store = new LocalStore(Path.Combine(folder, "data"));
            await store.ClearAsync(); await store.SaveSettingsAsync(SettingsUpdate.Full(Settings.Defaults()));
            var transport = new PendingTransport();
            using (var shell = new AppShell(store, false, new LlmClient(transport))) {
                shell.PopulateDemo();
                Check(Ui.Get<Border>(shell.Popup, "InputPanel").Visibility == Visibility.Collapsed, "Input is hidden before Ask", checks);
                Check(Ui.Get<Border>(shell.Popup, "WordPanel").Visibility == Visibility.Visible, "Word card is visible for the selected occurrence", checks);
                // The whole card is one read-only document: the entry, the meaning and every
                // section heading and body are paragraphs of the same flow, so a drag that
                // starts on a heading runs on into the content under it, and the in-window
                // selection read finds it by the same mark as the other reading controls.
                var card = Ui.Get<RichTextBox>(shell.Popup, "WordCard");
                var cardParagraphs = card.Document.Blocks.OfType<System.Windows.Documents.Paragraph>()
                    .Select(p => new System.Windows.Documents.TextRange(p.ContentStart, p.ContentEnd).Text.Trim()).ToList();
                Check(card.IsReadOnly && (card.Tag as string) == "LeafSelectableText" &&
                    cardParagraphs.Contains("高超的本领；非凡的技艺"),
                    "The word card is one selectable read-only document", checks);
                Check(cardParagraphs.Contains("prowess") && cardParagraphs.Contains("与 power 有什么区别？") &&
                    cardParagraphs.Contains("放回这句话"),
                    "The entry and the learning headings are paragraphs of the same card", checks);
                Check(shell.Popup.FindName("WordTitle") == null && shell.Popup.FindName("WordMeaning") == null &&
                    shell.Popup.FindName("LearningSections") == null,
                    "The learning card is no longer split across separate controls", checks);
                Check(shell.Popup.FindName("SceneCombo") == null, "Popup contains no scene picker", checks);
                Check(!shell.Popup.ShowActivated, "Popup does not activate on Show", checks);
                Check(shell.Popup.ShowInTaskbar && shell.Popup.Icon != null, "Popup has a branded taskbar entry", checks);
                Ui.Render(shell.Popup, Path.Combine(folder, "popup.png"));
                if (Environment.GetEnvironmentVariable("LEAF_SMOKE_FAIL_AFTER_RENDER") == "1") throw new InvalidOperationException("Fixture-requested smoke failure after the first render.");
                Ui.Get<Button>(shell.Popup, "AskButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Ui.Get<Border>(shell.Popup, "InputPanel").Visibility == Visibility.Visible, "Ask reveals the input", checks);
                Check(!shell.Pinned, "Asking does not pin the popup", checks);
                Check(Ui.Get<TextBlock>(shell.Popup, "TopicLabel").Text.Contains("prowess"), "Follow-up identifies the selected word", checks);
                Ui.Render(shell.Popup, Path.Combine(folder, "popup-question.png"));
                shell.Popup.Width = 360; shell.Popup.Height = 380; shell.UpdatePopupTypography();
                var translation = Ui.Get<RichTextBox>(shell.Popup, "TranslationText");
                Check(translation.FontSize >= 14 && translation.FontSize <= 22, "Compact popup keeps the translation within its readable bounds", checks);
                Check(translation.IsReadOnly && shell.Popup.FindName("CopyButton") == null, "Translation supports selection with no redundant copy button", checks);
                Check(shell.Popup.FindName("SettingsButton") != null, "Settings is available beside pin in the popup", checks);
                // The original follows the same 84% ladder as the rest of the reading page:
                // the check reads the scale the window itself reports rather than a bound
                // that would pass whatever the ladder happened to be.
                Check(Math.Abs(Ui.Get<TextBox>(shell.Popup, "SourceText").FontSize - Math.Max(13, 19 * AppShell.TypographyScale(360, 380))) < 0.01,
                    "Compact popup scales the reading source to the window's own floor", checks);
                Check(Ui.Get<RichTextBox>(shell.Popup, "WordCard").Document.Blocks
                    .OfType<System.Windows.Documents.Paragraph>().All(p => p.FontSize >= 11),
                    "Learning text has a readable minimum", checks);
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
                Check(translation.FontSize == 22, "Large popup never excessively enlarges the text", checks);
                Ui.Render(shell.Popup, Path.Combine(folder, "popup-large.png"));
                shell.Popup.Width = 456; shell.Popup.Height = 620; shell.UpdatePopupTypography();
                Check(translation.FontSize == 22, "Resizing back restores the original typography without cumulative scaling", checks);

                EditorState(shell, checks);
                // The reading column's one origin check, and the face the page is really set
                // in, both reported as the values they came out as: the layout and a silent
                // fall back to an installed typeface are both reviewable from the run.
                var paper = (FontFamily)Application.Current.Resources["PaperFontFamily"];
                GlyphTypeface paperGlyph;
                new Typeface(paper, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out paperGlyph);
                Console.WriteLine("paper font: " + paper.Source + " -> " + (paperGlyph == null ? "unresolved" : paperGlyph.FontUri.ToString()));
                Console.WriteLine("reading column: " + AppShell.ReadingColumnReport);
                RenderStates(shell, transport, folder, checks);

                var settings = new SettingsWindow(shell);
                Check(Ui.Get<ComboBox>(settings.Panel, "ProviderCombo").SelectedIndex == -1, "New users choose an endpoint explicitly rather than guessing from a key", checks);
                Ui.Get<ComboBox>(settings.Panel, "ProviderCombo").SelectedIndex = 0;
                Check(settings.Panel.FindName("MonitorCombo") == null, "Popup-position selection is removed from settings", checks);
                var model = Ui.Get<ComboBox>(settings.Panel, "ModelInput");
                Check(model.IsEditable, "Model selection accepts a manual ID", checks);
                Check(Ui.Get<TextBox>(settings.Panel, "ShortcutInput").IsReadOnly, "Shortcut settings record keys instead of accepting typed names", checks);
                Check(Ui.Get<CheckBox>(settings.Panel, "ClipboardModeCheck").IsChecked == false, "The default clipboard checkbox is off", checks);
                model.Text = "glm-5.3-flash";
                Check(!Ui.Get<ComboBoxItem>(settings.Panel, "DisableThinkingOption").IsEnabled && Ui.Get<StackPanel>(settings.Panel, "ReasoningPanel").Visibility == Visibility.Visible,
                    "GLM-5.3 advanced options explain mandatory reasoning and expose effort", checks);
                model.Text = "example-model";
                var scene = Ui.Get<ComboBox>(settings.Panel, "SceneCombo");
                scene.SelectedItem = "游戏";
                Check(Ui.Get<StackPanel>(settings.Panel, "GameTypePanel").Visibility == Visibility.Visible, "Game fields appear only in the game scene", checks);
                Ui.Get<TextBox>(settings.Panel, "SceneDetailInput").Text = "Example game";
                scene.SelectedItem = "书籍";
                Check(Ui.Get<StackPanel>(settings.Panel, "GameTypePanel").Visibility == Visibility.Collapsed, "Game fields disappear in books", checks);
                Check(Ui.Get<TextBox>(settings.Panel, "SceneDetailInput").Text.Length == 0, "A new scene starts with its own empty detail", checks);
                scene.SelectedItem = "游戏";
                Check(Ui.Get<TextBox>(settings.Panel, "SceneDetailInput").Text == "Example game", "Changing back restores the scene's remembered name", checks);
                scene.SelectedItem = "通用";
                Check(Ui.Get<StackPanel>(settings.Panel, "SceneDetailPanel").Visibility == Visibility.Collapsed, "General scene has no irrelevant detail field", checks);
                Ui.Get<TextBox>(settings.Panel, "CustomNameInput").Text = "常见误用";
                Ui.Get<TextBox>(settings.Panel, "CustomInstructionInput").Text = "解释容易混淆的用法。";
                Ui.Get<Button>(settings.Panel, "AddCustom").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Ui.Get<WrapPanel>(settings.Panel, "LearningChoices").Children.Count == 6, "Custom preference becomes a selectable option", checks);
                scene.SelectedItem = "游戏";
                Ui.Get<TextBox>(settings.Panel, "PresetNameInput").Text = "游戏学习";
                Ui.Get<Button>(settings.Panel, "SavePreset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Ui.Get<ComboBox>(settings.Panel, "PresetCombo").IsEnabled && Ui.Get<Button>(settings.Panel, "LoadPreset").IsEnabled &&
                    Ui.Get<Button>(settings.Panel, "RemovePreset").IsEnabled,
                    "Saving the first preset enables the preset dropdown and its actions", checks);
                scene.SelectedItem = "通用";
                Ui.Get<Button>(settings.Panel, "LoadPreset").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Convert.ToString(scene.SelectedItem) == "游戏", "Preference preset restores the scene", checks);
                Ui.Render(settings.Window, Path.Combine(folder, "settings.png"));
                settings.Window.Close();
                await shell.WaitForPersistenceAsync();

                await store.SaveAsync(shell.Current, store.HistoryEpoch);
                var other = TranslationRecord.Create("Elle a pris son temps.", "选中文字", Settings.Defaults());
                other.Translation = "她不紧不慢地完成了。"; other.Completed = true; await store.SaveAsync(other, store.HistoryEpoch);
                var german = TranslationRecord.Create("Das kommt darauf an.", "剪贴板", Settings.Defaults());
                german.Translation = "这要看情况。"; german.Completed = true; await store.SaveAsync(german, store.HistoryEpoch);
                await shell.WaitForPersistenceAsync();
                var history = new HistoryWindow(shell);
                Check(Ui.Get<ListBox>(history.Window, "HistoryList").Items.Count == 3, "History displays saved translations", checks);
                Ui.Get<TextBox>(history.Window, "HistorySearch").Text = "prowess";
                Check(Ui.Get<ListBox>(history.Window, "HistoryList").Items.Count == 1, "History search filters original text", checks);
                Ui.Get<TextBox>(history.Window, "HistorySearch").Text = "看情况";
                Check(Ui.Get<ListBox>(history.Window, "HistoryList").Items.Count == 1, "History search filters translated text", checks);
                Ui.Get<TextBox>(history.Window, "HistorySearch").Clear();
                Ui.Render(history.Window, Path.Combine(folder, "history.png"));
                history.Window.Close();
                await NavigationState(shell, transport, folder, checks);
                StopState(shell, transport, folder, checks);
                shell.Forget(null);
                await shell.WaitForPersistenceAsync();
                Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Collapsed,
                    "Empty popup stays quiet without automatically opening the editor", checks);
                shell.BeginSourceEdit(false);
                Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Visible,
                    "Explicit editing opens manual input in the empty popup", checks);
                Ui.Render(shell.Popup, Path.Combine(folder, "popup-input.png"));
            }
        }
    }
}
