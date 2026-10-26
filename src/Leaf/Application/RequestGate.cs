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
    public sealed class RequestGate
    {
        private long version;
        public long Next() { return System.Threading.Interlocked.Increment(ref version); }
        public bool IsCurrent(long requestVersion) { return System.Threading.Interlocked.Read(ref version) == requestVersion; }
    }
}
