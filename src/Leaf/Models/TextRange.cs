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
    public sealed class TextPiece
    {
        public int Start { get; set; }
        public int Length { get; set; }
        public string Text { get; set; }
        public bool IsWord { get; set; }
        public string Key { get { return Start + ":" + Length; } }
    }
}
