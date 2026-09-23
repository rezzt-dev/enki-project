using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace enki.Services;

/// <summary>Secret Service de la sesión de usuario, a través de libsecret.</summary>
internal sealed class LibsecretPolarCredentialStore(string installationId) : IPolarCredentialStore
{
    private static readonly IntPtr Glib = NativeLibrary.Load("libglib-2.0.so.0");
    private readonly string _installationId = installationId;

    public PolarCredentials? Read()
    {
        using var attributes = new Attributes(_installationId);
        using var cancellation = new NativeCancellable();
        var password = SecretPasswordLookup(IntPtr.Zero, attributes.Table, cancellation.Handle, out var error);
        ThrowIfError(error);
        if (password == IntPtr.Zero) return null;
        try { return PolarCredentialStore.Deserialize(Marshal.PtrToStringUTF8(password)!); }
        finally { SecretPasswordFree(password); }
    }

    public void Write(PolarCredentials credentials)
    {
        using var attributes = new Attributes(_installationId);
        using var cancellation = new NativeCancellable();
        var stored = SecretPasswordStore(IntPtr.Zero, attributes.Table, IntPtr.Zero,
            "Enki Polar", PolarCredentialStore.Serialize(credentials), cancellation.Handle, out var error);
        ThrowIfError(error);
        if (stored == 0) throw new IOException("Secret Service no pudo guardar la sesión Polar.");
    }

    public void Delete()
    {
        using var attributes = new Attributes(_installationId);
        using var cancellation = new NativeCancellable();
        _ = SecretPasswordClear(IntPtr.Zero, attributes.Table, cancellation.Handle, out var error);
        ThrowIfError(error);
    }

    private static void ThrowIfError(IntPtr error)
    {
        if (error == IntPtr.Zero) return;
        try
        {
            var detail = Marshal.PtrToStructure<GError>(error);
            throw new IOException("Secret Service: " + Marshal.PtrToStringUTF8(detail.Message));
        }
        finally { GErrorFree(error); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GError
    {
        public uint Domain;
        public int Code;
        public IntPtr Message;
    }

    private sealed class Attributes : IDisposable
    {
        private readonly IntPtr[] _strings;
        public IntPtr Table { get; }

        public Attributes(string installationId)
        {
            Table = GHashTableNew(NativeLibrary.GetExport(Glib, "g_str_hash"),
                NativeLibrary.GetExport(Glib, "g_str_equal"));
            if (Table == IntPtr.Zero) throw new IOException("No se pudo crear la consulta de Secret Service.");
            _strings = [
                Marshal.StringToCoTaskMemUTF8("application"), Marshal.StringToCoTaskMemUTF8("enki.polar"),
                Marshal.StringToCoTaskMemUTF8("installation"), Marshal.StringToCoTaskMemUTF8(installationId)
            ];
            _ = GHashTableInsert(Table, _strings[0], _strings[1]);
            _ = GHashTableInsert(Table, _strings[2], _strings[3]);
        }

        public void Dispose()
        {
            GHashTableDestroy(Table);
            foreach (var item in _strings) Marshal.FreeCoTaskMem(item);
        }
    }

    /// <summary>Evita que un servicio de secretos bloqueado detenga el arranque o la interfaz.</summary>
    private sealed class NativeCancellable : IDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(5));
        private readonly CancellationTokenRegistration _registration;
        public IntPtr Handle { get; } = GCancellableNew();

        public NativeCancellable()
        {
            if (Handle == IntPtr.Zero) throw new IOException("No se pudo iniciar Secret Service.");
            _registration = _timeout.Token.Register(() => GCancellableCancel(Handle));
        }

        public void Dispose()
        {
            _registration.Dispose();
            _timeout.Dispose();
            GObjectUnref(Handle);
        }
    }

    [DllImport("libsecret-1.so.0", EntryPoint = "secret_password_lookupv_sync")]
    private static extern IntPtr SecretPasswordLookup(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
#pragma warning disable CA2101 // libsecret usa UTF-8; LPUTF8Str lo declara explícitamente.
    [DllImport("libsecret-1.so.0", EntryPoint = "secret_password_storev_sync")]
    private static extern int SecretPasswordStore(IntPtr schema, IntPtr attributes, IntPtr collection,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string label, [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        IntPtr cancellable, out IntPtr error);
#pragma warning restore CA2101
    [DllImport("libsecret-1.so.0", EntryPoint = "secret_password_clearv_sync")]
    private static extern int SecretPasswordClear(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [DllImport("libsecret-1.so.0", EntryPoint = "secret_password_free")]
    private static extern void SecretPasswordFree(IntPtr password);
    [DllImport("libglib-2.0.so.0", EntryPoint = "g_hash_table_new")]
    private static extern IntPtr GHashTableNew(IntPtr hash, IntPtr equal);
    [DllImport("libglib-2.0.so.0", EntryPoint = "g_hash_table_insert")]
    private static extern int GHashTableInsert(IntPtr table, IntPtr key, IntPtr value);
    [DllImport("libglib-2.0.so.0", EntryPoint = "g_hash_table_destroy")]
    private static extern void GHashTableDestroy(IntPtr table);
    [DllImport("libglib-2.0.so.0", EntryPoint = "g_error_free")]
    private static extern void GErrorFree(IntPtr error);
    [DllImport("libgio-2.0.so.0", EntryPoint = "g_cancellable_new")]
    private static extern IntPtr GCancellableNew();
    [DllImport("libgio-2.0.so.0", EntryPoint = "g_cancellable_cancel")]
    private static extern void GCancellableCancel(IntPtr cancellable);
    [DllImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")]
    private static extern void GObjectUnref(IntPtr value);
}
