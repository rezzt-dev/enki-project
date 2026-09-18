using System;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;

namespace enki.Services;

/// <summary>DPAPI con ámbito CurrentUser; el archivo contiene sólo bytes cifrados.</summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiPolarCredentialStore(string directory) : IPolarCredentialStore
{
    private readonly string _path = Path.Combine(directory, "polar-session.dat");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Enki.Polar.Session.v1");

    public PolarCredentials? Read()
    {
        if (!File.Exists(_path)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
        try { return PolarCredentialStore.Deserialize(Encoding.UTF8.GetString(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Write(PolarCredentials credentials)
    {
        var plain = Encoding.UTF8.GetBytes(PolarCredentialStore.Serialize(credentials));
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, _path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
