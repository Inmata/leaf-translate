using System;
using System.Collections.Generic;

namespace Leaf
{
    public sealed class ModelCatalog
    {
        public List<string> Models = new List<string>();
        public Dictionary<string, string> Owners = new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
