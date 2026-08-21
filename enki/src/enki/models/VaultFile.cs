using System;
using System.IO;
using enki.Localization;

namespace enki.Models;

public sealed class VaultFile
{
    public required string FullPath { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public long SizeBytes { get; set; }
    public VaultFileKind Kind { get; set; }

    public string FileName => Path.GetFileName(FullPath);
    public string Extension => Path.GetExtension(FullPath).TrimStart('.').ToUpperInvariant();

    public string ModifiedLabel => RelativeTimeFormatter.Format(ModifiedUtc);

    public string SizeLabel => FileSizeFormatter.Format(SizeBytes);

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    /// <summary>Detección barata por extensión (case-insensitive), no por contenido — sin sniffing de
    /// magic bytes. <c>.svg</c> cae deliberadamente en <see cref="VaultFileKind.Other"/>: Avalonia's
    /// <c>Bitmap</c> no decodifica vectorial sin sumar una dependencia de renderizado SVG nueva.</summary>
    public static VaultFileKind KindFromExtension(string extension)
    {
        var normalized = extension.StartsWith('.') ? extension : $".{extension}";
        if (Array.Exists(ImageExtensions, e => e.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return VaultFileKind.Image;
        if (normalized.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return VaultFileKind.Pdf;
        return VaultFileKind.Other;
    }
}

public enum VaultFileKind { Image, Pdf, Other }
