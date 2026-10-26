using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Leaf
{
    // Thin file operations seam so storage regression checks can inject real I/O faults
    // without touching the user profile. Paths are always validated local absolute paths
    // owned by LocalStore; no session object or credential ever crosses this interface.
    public interface IStoreFiles
    {
        bool Exists(string path);
        long Length(string path);
        string Read(string path);
        void Write(string path, string payload);
        void Copy(string source, string destination, bool overwrite);
        void Move(string source, string destination);
        void Replace(string source, string destination);
        void Delete(string path);
    }

    public sealed class PhysicalStoreFiles : IStoreFiles
    {
        public bool Exists(string path) { return File.Exists(path); }
        public long Length(string path) { return new FileInfo(path).Length; }
        public string Read(string path) { return File.ReadAllText(path, Encoding.UTF8); }
        public void Write(string path, string payload)
        { File.WriteAllText(path, payload, new UTF8Encoding(false)); }
        public void Copy(string source, string destination, bool overwrite)
        { File.Copy(source, destination, overwrite); }
        public void Move(string source, string destination) { File.Move(source, destination); }
        public void Replace(string source, string destination) { File.Replace(source, destination, null); }
        public void Delete(string path) { File.Delete(path); }
    }
}
