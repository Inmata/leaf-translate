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
    public static class WordCardParser
    {
        public static WordCard Parse(string text, string word, string translation)
        {
            text = text.Trim();
            string fence = new string((char)96, 3);
            if (text.StartsWith(fence)) {
                int line = text.IndexOf('\n'), end = text.LastIndexOf(fence, StringComparison.Ordinal);
                if (line >= 0 && end > line) text = text.Substring(line + 1, end - line - 1);
            }
            WordCard result;
            try { result = Json.Read<WordCard>(text); }
            catch { throw new UserError("format", "模型返回的词卡无法解析，请重试。"); }
            if (result == null || string.IsNullOrWhiteSpace(result.meaning) || result.meaning.Length > 6000)
                throw new UserError("format", "模型没有返回有效的词义，请重试。");
            result.word = word;
            result.lemma = Safe(result.lemma, 250);
            result.part_of_speech = Safe(result.part_of_speech, 100);
            result.target_phrase = Safe(result.target_phrase, 500);
            if (!string.IsNullOrEmpty(result.target_phrase) && translation.IndexOf(result.target_phrase, StringComparison.Ordinal) < 0)
                result.target_phrase = "";
            result.sections = (result.sections ?? new List<LearningSection>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.content)).Take(8)
                .Select(s => new LearningSection { title = Safe(s.title, 100), content = Safe(s.content, 6000) }).ToList();
            return result;
        }
        private static string Safe(string value, int limit) {
            value = (value ?? "").Trim(); return TextTools.TruncateElements(value, limit);
        }
    }
}
