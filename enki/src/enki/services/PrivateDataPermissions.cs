using System;
using System.IO;

namespace enki.Services;

/// <summary>Permisos POSIX de los datos privados de Enki. Windows hereda las ACL del perfil; Linux y
/// macOS restringen carpetas a 0700 y archivos a 0600.</summary>
internal static class PrivateDataPermissions
{
    internal const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, DirectoryMode);
        if (new DirectoryInfo(path).LinkTarget is null)
            File.SetUnixFileMode(path, DirectoryMode);
    }

    internal static void EnsurePrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path) && new FileInfo(path).LinkTarget is null)
            File.SetUnixFileMode(path, FileMode);
    }

    /// <summary>Corrige también datos creados por versiones anteriores sin atravesar enlaces simbólicos.</summary>
    internal static void HardenExistingTree(string root)
    {
        EnsurePrivateDirectory(root);
        if (OperatingSystem.IsWindows()) return;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var directory in Directory.EnumerateDirectories(root, "*", options))
            File.SetUnixFileMode(directory, DirectoryMode);
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
            File.SetUnixFileMode(file, FileMode);
    }
}
