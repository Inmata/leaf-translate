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
    public sealed class UserError : Exception
    {
        public string Code { get; private set; }
        public UserError(string code, string message) : this(code, message, null) { }
        public UserError(string code, string message, Exception inner) : base(message, inner) { Code = code; }
    }
}
