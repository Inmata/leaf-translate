using System;
using System.Collections.Generic;
using System.Text;

namespace Leaf
{
    public struct TranslationSpan
    {
        public string Text { get; private set; }
        public bool Bold { get; private set; }
        public TranslationSpan(string text, bool bold) : this() { Text = text; Bold = bold; }
    }

    // Pure presentation formatter for model answers: the only supported emphasis is the
    // writer's **paired** marker. Escaped markers and unmatched markers keep their original
    // characters. A code span is a paired `...` run copied verbatim — escapes and **
    // markers inside stay literal — and an unpaired backtick stays literal so it never
    // hides a later valid marker. Copy text is the concatenation of span texts, so raw
    // answers, history and LLM context stay untouched.
    public static class TranslationPresentation
    {
        public static IReadOnlyList<TranslationSpan> Parse(string raw)
        {
            var spans = new List<TranslationSpan>();
            if (string.IsNullOrEmpty(raw)) return spans;
            var plain = new StringBuilder();
            int i = 0;
            while (i < raw.Length)
            {
                char c = raw[i];
                if (c == '\\' && i + 1 < raw.Length && (raw[i + 1] == '*' || raw[i + 1] == '\\'))
                {
                    plain.Append(raw[i + 1]); i += 2; continue;
                }
                if (c == '`')
                {
                    // A paired backtick run is one code span consumed verbatim, so escape
                    // handling never sees its interior. Unpaired backticks stay literal.
                    int close = FindCodeClose(raw, i + 1);
                    if (close >= 0)
                    {
                        plain.Append(raw, i, close - i + 1); i = close + 1; continue;
                    }
                    plain.Append('`'); i++; continue;
                }
                if (c == '*' && i + 1 < raw.Length && raw[i + 1] == '*')
                {
                    int end = FindClosing(raw, i + 2);
                    if (end >= 0 && end > i + 2)
                    {
                        if (plain.Length > 0) { spans.Add(new TranslationSpan(plain.ToString(), false)); plain.Length = 0; }
                        var bold = new StringBuilder();
                        AppendUnescaped(raw, i + 2, end, bold);
                        spans.Add(new TranslationSpan(bold.ToString(), true));
                        i = end + 2; continue;
                    }
                }
                plain.Append(c); i++;
            }
            if (plain.Length > 0) spans.Add(new TranslationSpan(plain.ToString(), false));
            return spans;
        }

        private static int FindClosing(string raw, int start)
        {
            int j = start;
            while (j < raw.Length)
            {
                if (raw[j] == '\\' && j + 1 < raw.Length && (raw[j + 1] == '*' || raw[j + 1] == '\\')) { j += 2; continue; }
                if (raw[j] == '`')
                {
                    // Skip the whole paired code span so its ** never reads as a closing
                    // marker; an unpaired backtick just continues the search.
                    int close = FindCodeClose(raw, j + 1);
                    if (close >= 0) { j = close + 1; continue; }
                }
                if (raw[j] == '*' && j + 1 < raw.Length && raw[j + 1] == '*') return j;
                j++;
            }
            return -1;
        }

        // A code span closes at the next backtick; -1 means the backtick is unpaired.
        private static int FindCodeClose(string raw, int start)
        {
            for (int j = start; j < raw.Length; j++) if (raw[j] == '`') return j;
            return -1;
        }

        private static void AppendUnescaped(string raw, int start, int end, StringBuilder target)
        {
            int j = start;
            while (j < end)
            {
                if (raw[j] == '\\' && j + 1 < end && (raw[j + 1] == '*' || raw[j + 1] == '\\')) { target.Append(raw[j + 1]); j += 2; continue; }
                if (raw[j] == '`')
                {
                    // Keep embedded code spans verbatim, matching FindClosing's skips.
                    int close = FindCodeClose(raw, j + 1);
                    if (close >= 0 && close < end)
                    {
                        target.Append(raw, j, close - j + 1); j = close + 1; continue;
                    }
                }
                target.Append(raw[j]); j++;
            }
        }
    }
}
