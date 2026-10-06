using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Leaf;

// The in-window selection read: only the control that really holds the current focus on this
// window's translate page may answer, and never a sensitive, follow-up or settings field. These
// checks run through the reader the shortcut uses, with real WPF controls, and never touch
// UI Automation, the clipboard or a simulated copy.
internal static class InternalSelectionTests
{
    private static int assertions;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAILED: " + label);
        assertions++;
        Console.WriteLine("PASS " + label);
    }

    // Registered from SelectionTests.Run, so the existing test entry stays the only one. The
    // checks run on their own STA thread: the entry point's thread has left the apartment the
    // async capture checks continued on, and WPF controls need one.
    public static int Run()
    {
        assertions = 0;
        Exception failure = null;
        var worker = new System.Threading.Thread(() => {
            try { Scenarios(); } catch (Exception error) { failure = error; }
        }) { IsBackground = true, Name = "Leaf internal selection checks" };
        worker.SetApartmentState(System.Threading.ApartmentState.STA);
        worker.Start(); worker.Join();
        if (failure != null) throw failure;
        return assertions;
    }

    private static void Scenarios()
    {
        var page = new Grid();
        var source = new RichTextBox { Name = "SourceText" };
        var translation = new RichTextBox { Name = "TranslationText" };
        var input = new TextBox { Name = "SourceInput" };
        var question = new TextBox { Name = "QuestionInput" };
        var setting = new TextBox { Name = "SceneDetailInput" };
        var secret = new PasswordBox { Name = "ApiKeyInput" };
        var reading = new RichTextBox { Tag = InternalSelectionReader.SelectableTag };
        foreach (var control in new Control[] { source, translation, input, question, setting, reading })
            page.Children.Add(control);
        page.Children.Add(secret);
        var reader = new InternalSelectionReader(page);

        // Each allowed source answers with its own current selection.
        source.Document.Blocks.Add(new Paragraph(new Run("selected source text")));
        source.Selection.Select(source.Document.ContentStart, source.Document.ContentEnd);
        var fromSource = reader.ReadFrom(source);
        Check(fromSource.Status == InternalSelectionStatus.Text && fromSource.Text == "selected source text" &&
            fromSource.Source == "SourceText" && fromSource.Reason == InternalSelectionReason.None,
            "A selection in the original text answers with its own text");

        var emptyTranslation = reader.ReadFrom(translation);
        Check(emptyTranslation.Status == InternalSelectionStatus.Empty &&
            emptyTranslation.Reason == InternalSelectionReason.EmptySelection && emptyTranslation.Text.Length == 0,
            "A control with no selection of its own answers empty instead of another control's selection");

        translation.Document.Blocks.Add(new Paragraph(new Run("translated selection")));
        translation.Selection.Select(translation.Document.ContentStart, translation.Document.ContentEnd);
        var fromTranslation = reader.ReadFrom(translation);
        Check(fromTranslation.Status == InternalSelectionStatus.Text && fromTranslation.Text == "translated selection" &&
            fromTranslation.Source == "TranslationText",
            "A selection in the translation answers with its own text");

        input.Text = "typed words";
        input.Select(0, 5);
        var fromInput = reader.ReadFrom(input);
        Check(fromInput.Status == InternalSelectionStatus.Text && fromInput.Text == "typed" && fromInput.Source == "SourceInput",
            "A selection in the original editor answers with its own text");

        reading.Document.Blocks.Add(new Paragraph(new Run("reading control selection")));
        reading.Selection.Select(reading.Document.ContentStart, reading.Document.ContentEnd);
        var fromReading = reader.ReadFrom(reading);
        Check(fromReading.Status == InternalSelectionStatus.Text && fromReading.Text == "reading control selection" &&
            fromReading.Source == InternalSelectionReader.SelectableTag,
            "A reading control that opts in with the shared tag answers for itself");

        // Whitespace is not a selection the shortcut should start a session for.
        input.Text = "   "; input.SelectAll();
        var whitespace = reader.ReadFrom(input);
        Check(whitespace.Status == InternalSelectionStatus.Empty && whitespace.Reason == InternalSelectionReason.EmptySelection,
            "A whitespace-only selection is empty rather than a session");
        input.Text = "typed words"; input.Select(0, 5);

        // Sensitive and settings fields never answer, however they are focused.
        question.Text = "a follow-up question"; question.SelectAll();
        var fromQuestion = reader.ReadFrom(question);
        Check(fromQuestion.Status == InternalSelectionStatus.Unavailable &&
            fromQuestion.Reason == InternalSelectionReason.SensitiveControl &&
            fromQuestion.Text.Length == 0 && fromQuestion.TextLength == 0,
            "The follow-up input is never read as a translation source");
        var fromSecret = reader.ReadFrom(secret);
        Check(fromSecret.Status == InternalSelectionStatus.Unavailable &&
            fromSecret.Reason == InternalSelectionReason.SensitiveControl && fromSecret.Text.Length == 0,
            "A password field is never read");
        setting.Text = "a settings value"; setting.SelectAll();
        var fromSetting = reader.ReadFrom(setting);
        Check(fromSetting.Status == InternalSelectionStatus.Unavailable && fromSetting.Text.Length == 0 &&
            (fromSetting.Reason == InternalSelectionReason.NotSelectable || fromSetting.Reason == InternalSelectionReason.SensitiveControl),
            "A settings field is never read, even while it holds a selection");

        // A focus inside the document content is not a selection the user made in the editor.
        var link = new Hyperlink(new Run("linked word"));
        source.Document.Blocks.Add(new Paragraph(link));
        var fromContent = reader.ReadFrom(link);
        Check(fromContent.Status == InternalSelectionStatus.Unavailable &&
            fromContent.Reason == InternalSelectionReason.ContentFocus && fromContent.Text.Length == 0,
            "A focused hyperlink never inherits the editor's older selection");

        // Another window's control is not this page's selection.
        var elsewhere = new TextBox { Name = "SourceInput", Text = "other window" };
        elsewhere.SelectAll();
        var fromElsewhere = reader.ReadFrom(elsewhere);
        Check(fromElsewhere.Status == InternalSelectionStatus.Unavailable &&
            fromElsewhere.Reason == InternalSelectionReason.FocusOutsidePage && fromElsewhere.Text.Length == 0,
            "A control of another window never answers for this page");

        // A collapsed page keeps its controls' older selections, which are not a current one.
        var hiddenPage = new Grid { Visibility = Visibility.Collapsed };
        var hiddenInput = new TextBox { Name = "SourceInput", Text = "stale page selection" };
        hiddenInput.SelectAll(); hiddenPage.Children.Add(hiddenInput); page.Children.Add(hiddenPage);
        var fromHidden = reader.ReadFrom(hiddenInput);
        Check(fromHidden.Status == InternalSelectionStatus.Unavailable &&
            fromHidden.Reason == InternalSelectionReason.HiddenPage && fromHidden.Text.Length == 0,
            "A collapsed page never answers with the selection it still holds");

        // No focus at all, and no page to read from, are their own answers.
        Check(reader.ReadFrom(null).Reason == InternalSelectionReason.NoFocus,
            "A missing focus is its own answer instead of an empty selection");
        Check(reader.ReadFrom("not a control").Reason == InternalSelectionReason.NotSelectable,
            "A focus that is not a control element is not a selectable source");
        var empty = new InternalSelectionReader(null);
        Check(empty.Read().Status == InternalSelectionStatus.Unavailable &&
            empty.Read().Reason == InternalSelectionReason.NoPage && empty.ReadFrom(input).Reason == InternalSelectionReason.NoPage,
            "A reader without a page answers with no page rather than reading anything");
        // The production entry point needs a shown, active window; a page that is not one says so.
        var notAWindow = new InternalSelectionReader(page);
        Check(notAWindow.Read().Reason == InternalSelectionReason.NoPage,
            "The production read needs a window, so a bare page is reported as no page");
    }
}
