using System;
using System.Collections.Generic;
using System.Text;
using Leaf;

// Presentation formatter coverage: only paired **emphasis** becomes bold; escapes, code
// spans and unmatched markers keep their original characters, and Unicode survives intact.
// Copy text is the concatenation of span texts: the visible characters without markers.
public static class TranslationPresentationTests
{
    private static int assertions;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAILED: " + label);
        assertions++;
        Console.WriteLine("PASS " + label);
    }
    private static string Plain(IReadOnlyList<TranslationSpan> spans)
    {
        var text = new StringBuilder();
        foreach (var span in spans) text.Append(span.Text);
        return text.ToString();
    }
    private static IReadOnlyList<TranslationSpan> Parsed(string raw, string expectedPlain)
    {
        var spans = TranslationPresentation.Parse(raw);
        Check(Plain(spans) == expectedPlain, "Copy text of " + raw + " is the visible characters");
        return spans;
    }
    public static int Run()
    {
        assertions = 0;
        Check(TranslationPresentation.Parse(null).Count == 0, "Null raw yields an empty span list");
        Check(TranslationPresentation.Parse("").Count == 0, "Empty raw yields an empty span list");
        Check(TranslationPresentation.Parse("  ").Count == 1 && Plain(TranslationPresentation.Parse("  ")) == "  ", "Whitespace-only raw stays plain text");

        var simple = Parsed("**document**", "document");
        Check(simple.Count == 1 && simple[0].Bold && simple[0].Text == "document", "A paired marker renders one bold span");

        var mixed = Parsed("plain **document** tail", "plain document tail");
        Check(mixed.Count == 3 && !mixed[0].Bold && mixed[1].Bold && mixed[1].Text == "document" && !mixed[2].Bold, "Order is text, bold, text");

        var consecutive = Parsed("**first****second**", "firstsecond");
        Check(consecutive.Count == 2 && consecutive[0].Bold && consecutive[0].Text == "first" && consecutive[1].Bold && consecutive[1].Text == "second", "Consecutive bold pairs stay adjacent bold spans");

        var unicode = Parsed("他展现了**非凡的勇气**，越过『边界』。", "他展现了非凡的勇气，越过『边界』。");
        Check(unicode.Count == 3 && !unicode[0].Bold && unicode[1].Bold && unicode[1].Text == "非凡的勇气" && !unicode[2].Bold, "Unicode bold spans keep every character");

        var escaped = Parsed("a \\* not bold \\* b", "a * not bold * b");
        Check(escaped.Count == 1 && !escaped[0].Bold, "Escaped markers keep the original meaning");

        var escapedPair = Parsed("\\*not bold\\*", "*not bold*");
        Check(escapedPair.Count == 1 && !escapedPair[0].Bold, "Escaped markers never open a bold span");

        var code = Parsed("use `**x**` here", "use `**x**` here");
        Check(code.Count == 1 && !code[0].Bold, "Code spans keep their markers literal");

        var unmatched = Parsed("**unmatched stays", "**unmatched stays");
        Check(unmatched.Count == 1 && !unmatched[0].Bold, "An unmatched marker stays literal");

        var balanced = Parsed("****", "****");
        Check(balanced.Count == 1 && !balanced[0].Bold, "An empty pair stays literal");

        Check(TranslationPresentation.Parse("no markers").Count == 1 && !TranslationPresentation.Parse("no markers")[0].Bold, "Plain raw maps to one identical plain span");

        // The backslash inside the code span is literal content, not an escape.
        var spans = Parsed("中**粗**体 `c\\*d` 尾\\*", "中粗体 `c\\*d` 尾*");
        Check(spans.Count == 3 && spans[0].Text == "中" && spans[1].Bold && spans[1].Text == "粗" && spans[2].Text == "体 `c\\*d` 尾*", "Mixed bold, code and escapes segment correctly");

        var codeInBold = Parsed("**before `**literal**` after**", "before `**literal**` after");
        Check(codeInBold.Count == 1 && codeInBold[0].Bold && codeInBold[0].Text == "before `**literal**` after", "The closing search skips code spans so inner markers stay literal");

        var codeEscapes = Parsed("`c\\*d`", "`c\\*d`");
        Check(codeEscapes.Count == 1 && !codeEscapes[0].Bold && codeEscapes[0].Text == "`c\\*d`", "Code spans keep backslashes and stars verbatim");

        var boldWithCode = Parsed("**a `b\\*c` d**", "a `b\\*c` d");
        Check(boldWithCode.Count == 1 && boldWithCode[0].Bold && boldWithCode[0].Text == "a `b\\*c` d", "Bold text keeps embedded code spans verbatim");

        var strayBacktick = Parsed("tmp `x **bold**", "tmp `x bold");
        Check(strayBacktick.Count == 2 && !strayBacktick[0].Bold && strayBacktick[0].Text == "tmp `x " && strayBacktick[1].Bold && strayBacktick[1].Text == "bold", "An unpaired backtick never hides a later valid marker");

        var pairedCodeWithBold = Parsed("a `x **y** z` b", "a `x **y** z` b");
        Check(pairedCodeWithBold.Count == 1 && !pairedCodeWithBold[0].Bold, "A paired code span keeps inner bold markers literal");

        var emoji = Parsed("👍 **要点** 🫶🏼", "👍 要点 🫶🏼");
        Check(emoji.Count == 3 && !emoji[0].Bold && emoji[1].Bold && emoji[1].Text == "要点" && !emoji[2].Bold, "Emoji surrogate pairs survive around bold spans");

        var combining = Parsed("cafe\u0301 **粗** ba\u0300r", "cafe\u0301 粗 ba\u0300r");
        Check(combining.Count == 3 && combining[1].Bold && combining[1].Text == "粗" && combining[2].Text == " ba\u0300r", "Combining marks stay attached to their base letters");

        return assertions;
    }
}
