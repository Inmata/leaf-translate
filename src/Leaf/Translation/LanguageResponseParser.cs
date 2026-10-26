using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Leaf
{
    public static class LanguageResponseParser
    {

        // An answer that cannot be read at all is a format failure like any other model output; an
        // answer that says "I cannot tell" is the structured non-success the result carries.
        public static SourceLanguageResult Parse(string payload, string targetLanguage)
        {
            var root = ReadObject(payload);
            if (root == null) throw new UserError("format", "模型返回的语言识别结果无法解析，请重试。");
            bool uncertain = Flag(root, "unknown") || !Flag(root, "certain", true) || LowConfidence(root);
            string language = uncertain ? "" : LanguageNames.Display(Text(root, "language"));
            return language.Length == 0
                ? new SourceLanguageResult { Language = "", Determined = false, SameAsTarget = false }
                : Result(language, targetLanguage);
        }


        internal static SourceLanguageResult Result(string language, string targetLanguage)
        {
            return new SourceLanguageResult {
                Language = language, Determined = true, SameAsTarget = LanguageNames.SameLanguage(language, targetLanguage)
            };
        }


        // Only a JSON string is a language name. A number, a boolean, an array or an object is a
        // malformed answer; Convert.ToString would have turned 42 / true / string[] into "42" /
        // "True" / "System.Object[]" and passed those off as a language.
        private static string Text(Dictionary<string, object> root, string field)
        {
            object raw;
            if (!root.TryGetValue(field, out raw)) return "";
            return raw as string ?? "";
        }

        // A missing flag is not a denial: models often omit a redundant certain=true. A value that
        // cannot be read as a boolean is not a confirmation either, so an unreadable answer stays
        // undetermined instead of becoming a confident guess.
        private static bool Flag(Dictionary<string, object> root, string field, bool whenMissing = false)
        {
            object raw;
            if (!root.TryGetValue(field, out raw) || raw == null) return whenMissing;
            if (raw is bool) return (bool)raw;
            string value = Convert.ToString(raw, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            if (value.Length == 0) return whenMissing;
            if (value == "true" || value == "yes" || value == "1" || value == "是" || value == "有把握") return true;
            // certain is a boolean: only 1 is a yes. A fraction or any other non-zero number
            // (0.2, 7, 100) is not a confirmation, so the answer stays undetermined.
            decimal number;
            if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out number)) return number == 1m;
            return false;
        }

        // The confidence field has no single shape: a model may write "low", 0.2, 20 or 0.9. A
        // word in the low set is a denial and a word in the high set is not; a number is read on an
        // explicit scale (0–1 as a probability, 1–100 as a percentage) with the same 50% line the
        // existing "low" answer draws. An unreadable type or a value outside both scales is not a
        // confidence that can be trusted, so it stays a denial instead of confirming a guess.
        private static bool LowConfidence(Dictionary<string, object> root)
        {
            object raw;
            if (!root.TryGetValue("confidence", out raw) || raw == null) return false;
            bool numeric = raw is byte || raw is sbyte || raw is short || raw is ushort || raw is int ||
                raw is uint || raw is long || raw is ulong || raw is float || raw is double || raw is decimal;
            if (!(raw is string) && !numeric) return true;
            string value = Convert.ToString(raw, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            if (value.Length == 0) return false;
            if (value == "low" || value == "none" || value == "低" || value == "不确定") return true;
            if (value == "high" || value == "certain" || value == "sure" || value == "confident" ||
                value == "高" || value == "确定" || value == "有把握") return false;
            decimal number;
            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out number)) return true;
            if (number < 0m || number > 100m) return true;
            return number <= 1m ? number < 0.5m : number < 50m;
        }


        private static Dictionary<string, object> ReadObject(string payload)
        {
            string text = (payload ?? "").Trim();
            string fence = new string((char)96, 3);
            if (text.StartsWith(fence, StringComparison.Ordinal)) {
                int line = text.IndexOf('\n'), end = text.LastIndexOf(fence, StringComparison.Ordinal);
                if (line >= 0 && end > line) text = text.Substring(line + 1, end - line - 1).Trim();
            }
            var root = TryObject(text);
            if (root == null) {
                // A sentence around the object is still readable; anything else is a format failure.
                int start = text.IndexOf('{'), finish = text.LastIndexOf('}');
                if (start >= 0 && finish > start) root = TryObject(text.Substring(start, finish - start + 1));
            }
            return root;
        }

        private static Dictionary<string, object> TryObject(string text)
        {
            try { return Json.Read(text) as Dictionary<string, object>; } catch { return null; }
        }
    }
}
