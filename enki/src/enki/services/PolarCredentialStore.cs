using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace enki.Services;

internal sealed record PolarCredentials(string? AccessToken, string? RefreshToken);

/// <summary>Una sola entrada atómica para los dos tokens de la sesión Polar.</summary>
internal interface IPolarCredentialStore
{
    PolarCredentials? Read();
    void Write(PolarCredentials credentials);
    void Delete();
}

internal static class PolarCredentialStore
{
    public static IPolarCredentialStore Create(string storageDirectory, string purpose = "polar")
    {
        var identity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(storageDirectory))));
        if (OperatingSystem.IsWindows()) return new DpapiPolarCredentialStore(storageDirectory, purpose);
        if (OperatingSystem.IsMacOS()) return new KeychainPolarCredentialStore(identity, purpose);
        if (OperatingSystem.IsLinux()) return new LibsecretPolarCredentialStore(identity, purpose);
        throw new PlatformNotSupportedException("No hay almacén seguro de credenciales para esta plataforma.");
    }

    internal static string Serialize(PolarCredentials credentials) => JsonSerializer.Serialize(credentials);
    internal static PolarCredentials Deserialize(string json) =>
        JsonSerializer.Deserialize<PolarCredentials>(json)
        ?? throw new InvalidDataException("La sesión Polar del almacén seguro está dañada.");
}
