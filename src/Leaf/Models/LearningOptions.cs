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
    public sealed class LearningOption
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Instruction { get; set; }
        public bool Enabled { get; set; }
        public bool Custom { get; set; }
    }

    public sealed class LearningPreset
    {
        public string Name { get; set; }
        public string TargetLanguage { get; set; }
        public string Scene { get; set; }
        public string SceneDetail { get; set; }
        public string GameTextType { get; set; }
        public string Style { get; set; }
        public List<LearningOption> Learning { get; set; }
        public override string ToString() { return Name; }
    }
}
