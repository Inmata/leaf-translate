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
    public sealed class LearningSection
    {
        public string title { get; set; }
        public string content { get; set; }
    }

    public sealed class WordCard
    {
        public string word { get; set; }
        public string lemma { get; set; }
        public string part_of_speech { get; set; }
        public string meaning { get; set; }
        public string target_phrase { get; set; }
        public List<LearningSection> sections { get; set; }
    }
}
