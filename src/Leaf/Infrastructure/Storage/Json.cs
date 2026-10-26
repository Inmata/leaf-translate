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
    public static class Json
    {
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 33554432, RecursionLimit = 64 }; }
        public static string Write(object value) { return Serializer().Serialize(value); }
        public static T Read<T>(string value) { return Serializer().Deserialize<T>(value); }
        public static object Read(string value) { return Serializer().DeserializeObject(value); }
        public static T Copy<T>(T value) { return Read<T>(Write(value)); }
    }
}
