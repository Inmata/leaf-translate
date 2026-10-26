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
    // The reading page's one typeface choice: an empty FontFamilyName keeps the embedded
    // Source Han Sans SC default, any other value names an installed text font. Common target
    // languages are offered by the settings picker; anything else is the user's own custom
    // value and stays a free string on Settings.TargetLanguage.
    public static class TargetLanguages
    {
        public const string Custom = "自定义";
        public static readonly string[] Common = {
            "中文", "繁體中文", "English", "日本語", "한국어", "Français", "Deutsch",
            "Español", "Português", "Русский", "Italiano", "العربية", "हिन्दी"
        };
        // The language detector answers with Leaf's own canonical Chinese name ("英语"), while the
        // picker offers each language under the name its speakers use ("English"). This is the one
        // bridge between the two, so an identified language selects a real entry in the picker
        // instead of becoming an unexplained custom value. A language the picker does not offer -
        // 泰语, 越南语, or a name the model wrote itself - is kept exactly as it was identified and
        // travels as the user's own custom value, which is what that field is for.
        private static readonly Dictionary<string, string> DetectedNames = new Dictionary<string, string>(StringComparer.Ordinal) {
            { "中文", "中文" }, { "英语", "English" }, { "日语", "日本語" }, { "韩语", "한국어" },
            { "法语", "Français" }, { "德语", "Deutsch" }, { "西班牙语", "Español" },
            { "葡萄牙语", "Português" }, { "俄语", "Русский" }, { "意大利语", "Italiano" },
            { "阿拉伯语", "العربية" }, { "印地语", "हिन्दी" }
        };
        public static string ForPicker(string detected)
        {
            string name = (detected ?? "").Trim();
            if (name.Length == 0) return "";
            string mapped;
            return DetectedNames.TryGetValue(name, out mapped) ? mapped : name;
        }
    }
}
