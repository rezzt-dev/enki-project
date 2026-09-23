using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace enki.Services;

/// <summary>Contraseña genérica en el Keychain del usuario mediante la API SecItem.</summary>
internal sealed class KeychainPolarCredentialStore(string installationId) : IPolarCredentialStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ItemNotFound = -25300;
    private const int DuplicateItem = -25299;
    private static readonly IntPtr SecurityLibrary = NativeLibrary.Load(Security);
    private static readonly IntPtr CoreFoundationLibrary = NativeLibrary.Load(CoreFoundation);
    private readonly string _installationId = installationId;

    public PolarCredentials? Read()
    {
        using var query = CreateQuery();
        query.SetConstant("kSecReturnData", CoreConstant(CoreFoundation, "kCFBooleanTrue"));
        query.SetConstant("kSecMatchLimit", SecurityConstant("kSecMatchLimitOne"));
        var status = SecItemCopyMatching(query.Handle, out var data);
        if (status == ItemNotFound) return null;
        Check(status);
        try
        {
            var length = checked((int)CFDataGetLength(data));
            var bytes = new byte[length];
            Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, length);
            return PolarCredentialStore.Deserialize(Encoding.UTF8.GetString(bytes));
        }
        finally { CFRelease(data); }
    }

    public void Write(PolarCredentials credentials)
    {
        using var query = CreateQuery();
        using var value = new Dictionary();
        value.SetData(PolarCredentialStore.Serialize(credentials));
        var status = SecItemUpdate(query.Handle, value.Handle);
        if (status == ItemNotFound)
        {
            query.SetData(PolarCredentialStore.Serialize(credentials));
            status = SecItemAdd(query.Handle, IntPtr.Zero);
            if (status == DuplicateItem) status = SecItemUpdate(query.Handle, value.Handle);
        }
        Check(status);
    }

    public void Delete()
    {
        using var query = CreateQuery();
        var status = SecItemDelete(query.Handle);
        if (status != ItemNotFound) Check(status);
    }

    private Dictionary CreateQuery()
    {
        var query = new Dictionary();
        query.SetConstant("kSecClass", SecurityConstant("kSecClassGenericPassword"));
        query.SetString("kSecAttrService", "Enki.Polar.Session");
        query.SetString("kSecAttrAccount", _installationId);
        return query;
    }

    private static void Check(int status)
    {
        if (status != 0) throw new IOException($"Keychain no pudo acceder a la sesión Polar (OSStatus {status}).");
    }

    private static IntPtr SecurityConstant(string symbol) => CoreConstant(Security, symbol);

    private static IntPtr CoreConstant(string library, string symbol)
    {
        var handle = library == Security ? SecurityLibrary : CoreFoundationLibrary;
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(handle, symbol));
    }

    private sealed class Dictionary : IDisposable
    {
        private readonly System.Collections.Generic.List<IntPtr> _owned = [];
        public IntPtr Handle { get; } = CFDictionaryCreateMutable(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);

        public void SetConstant(string key, IntPtr value) => CFDictionarySetValue(Handle, SecurityConstant(key), value);

        public void SetString(string key, string value)
        {
            var text = CFStringCreateWithCString(IntPtr.Zero, value, 0x08000100);
            if (text == IntPtr.Zero) throw new IOException("Keychain no pudo crear el nombre de la cuenta.");
            _owned.Add(text);
            SetConstant(key, text);
        }

        public void SetData(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var data = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
            if (data == IntPtr.Zero) throw new IOException("Keychain no pudo crear los datos de la sesión.");
            _owned.Add(data);
            SetConstant("kSecValueData", data);
        }

        public void Dispose()
        {
            CFRelease(Handle);
            foreach (var item in _owned) CFRelease(item);
        }
    }

    [DllImport(Security, EntryPoint = "SecItemCopyMatching")]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(Security, EntryPoint = "SecItemAdd")]
    private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
    [DllImport(Security, EntryPoint = "SecItemUpdate")]
    private static extern int SecItemUpdate(IntPtr query, IntPtr attributes);
    [DllImport(Security, EntryPoint = "SecItemDelete")]
    private static extern int SecItemDelete(IntPtr query);
    [DllImport(CoreFoundation, EntryPoint = "CFDictionaryCreateMutable")]
    private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [DllImport(CoreFoundation, EntryPoint = "CFDictionarySetValue")]
    private static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);
#pragma warning disable CA2101 // CoreFoundation usa UTF-8 y el parámetro declara LPUTF8Str.
    [DllImport(CoreFoundation, EntryPoint = "CFStringCreateWithCString")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
#pragma warning restore CA2101
    [DllImport(CoreFoundation, EntryPoint = "CFDataCreate")]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
    [DllImport(CoreFoundation, EntryPoint = "CFDataGetLength")]
    private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundation, EntryPoint = "CFDataGetBytePtr")]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CoreFoundation, EntryPoint = "CFRelease")]
    private static extern void CFRelease(IntPtr value);
}
