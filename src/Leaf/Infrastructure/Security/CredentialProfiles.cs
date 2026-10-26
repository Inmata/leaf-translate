using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Leaf
{
    // Credential access seam. AppShell creates the Windows-backed implementation only for
    // native runs; tests inject a fixture that never touches the real Credential Manager.
    public interface ICredentialProfiles
    {
        string Read(ProviderProfile profile);
        void Save(ProviderProfile profile, string key);
        void Delete(ProviderProfile profile);
    }

    public sealed class WindowsCredentialProfiles : ICredentialProfiles
    {
        public string Read(ProviderProfile profile) { return Credentials.Read(profile); }
        public void Save(ProviderProfile profile, string key) { Credentials.Save(profile, key); }
        public void Delete(ProviderProfile profile) { Credentials.Delete(profile); }
    }
}
