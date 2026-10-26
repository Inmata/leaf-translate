using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Leaf
{
    public static class TextTools
    {
        private static readonly Regex Words = new Regex(@"[\p{L}\p{M}]+(?:['’\-][\p{L}\p{M}]+)*|\p{N}+(?:[.,]\p{N}+)*", RegexOptions.Compiled);
        private static readonly Regex ContinuousScript = new Regex(
            @"[\u3040-\u30ff\u3400-\u9fff\uf900-\ufaff\u0e00-\u0eff\u1000-\u109f\u1780-\u17ff]|\p{Cs}", RegexOptions.Compiled);
        private static readonly Regex SentenceMarker = new Regex(@"[.!?。！？；;]", RegexOptions.Compiled);
        public static List<TextPiece> Pieces(string source)
        {
            var pieces = new List<TextPiece>(); int cursor = 0;
            foreach (Match match in Words.Matches(source)) {
                if (match.Index > cursor) pieces.Add(new TextPiece { Start = cursor, Length = match.Index - cursor, Text = source.Substring(cursor, match.Index - cursor) });
                pieces.Add(new TextPiece { Start = match.Index, Length = match.Length, Text = match.Value, IsWord = true });
                cursor = match.Index + match.Length;
            }
            if (cursor < source.Length) pieces.Add(new TextPiece { Start = cursor, Length = source.Length - cursor, Text = source.Substring(cursor) });
            return pieces;
        }
        public static bool IsWordInput(string source)
        {
            source = (source ?? "").Trim();
            return source.Length > 0 && source.Length <= 80 &&
                !ContinuousScript.IsMatch(source) && !SentenceMarker.IsMatch(source) &&
                Pieces(source).Count(p => p.IsWord) == 1;
        }
        // Truncates on .NET Framework text-element boundaries so surrogate pairs and combining marks stay intact.
        public static string TruncateElements(string value, int maxUtf16)
        {
            value = value ?? "";
            if (maxUtf16 < 0) throw new ArgumentOutOfRangeException("maxUtf16");
            if (value.Length <= maxUtf16) return value;
            int end = 0;
            var elements = System.Globalization.StringInfo.GetTextElementEnumerator(value);
            while (elements.MoveNext()) {
                int candidate = elements.ElementIndex + elements.GetTextElement().Length;
                if (candidate > maxUtf16) break;
                end = candidate;
            }
            return value.Substring(0, end);
        }
        public static string ValidateInput(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) throw new UserError("empty", "没有取得文字。请先选中或复制，再按快捷键。");
            if (text.Length > 6000) throw new UserError("length", "这段文字超过 6000 字符。请缩小选取范围后再试。");
            return text;
        }
    }
}
