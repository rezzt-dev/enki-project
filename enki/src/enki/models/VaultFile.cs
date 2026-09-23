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

    /// <summary>Extensiones de imagen rasterizada que Avalonia <c>Bitmap</c> decodifica nativamente
    /// via SkiaSharp — <c>.ico</c> ahora incluido (SkiaSharp lo soporta).</summary>
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tiff", ".tif"];

    /// <summary>Extensiones vectoriales SVG — decodificadas via Svg.Skia (SvgRenderHelper) en vez de
    /// <c>new Bitmap(stream)</c> porque SkiaSharp no rasteriza SVG nativamente.</summary>
    private static readonly string[] SvgExtensions = [".svg"];

    private static readonly string[] AudioExtensions = [".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".wma", ".opus", ".weba"];

    private static readonly string[] VideoExtensions = [".mp4", ".webm", ".mkv", ".mov", ".avi", ".wmv", ".m4v"];

    /// <summary>Extensiones de archivos de código/texto plano que merecen un visor con resaltado de
    /// sintaxis en vez de un icono genérico — incluye <c>.md</c> y <c>.txt</c> para que también se puedan
    /// previsualizar como código (Markdown raw, texto plano).</summary>
    private static readonly string[] CodeExtensions =
    [
        ".cs", ".js", ".ts", ".jsx", ".tsx", ".py", ".java", ".rs", ".go", ".rb", ".php",
        ".c", ".cpp", ".h", ".hpp", ".swift", ".kt", ".kts", ".dart", ".r", ".sql", ".sh",
        ".bat", ".ps1", ".json", ".xml", ".html", ".htm", ".css", ".scss", ".less",
        ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".env", ".gitignore",
        ".dockerfile", ".md", ".txt", ".log", ".csv", ".tsv", ".Makefile",
        ".gradle", ".cmake", ".vue", ".svelte", ".astro", ".tf", ".proto"
    ];

    /// <summary>True si este archivo es un SVG — se rasteriza via <c>SvgRenderHelper</c> en vez de
    /// decodificarse directamente con <c>new Bitmap(stream)</c>.</summary>
    public bool IsSvg => Array.Exists(SvgExtensions, e => e.Equals(Path.GetExtension(FullPath), StringComparison.OrdinalIgnoreCase));

    /// <summary>Detección barata por extensión (case-insensitive), no por contenido — sin sniffing de
    /// magic bytes. <c>.svg</c> ahora se clasifica como <see cref="VaultFileKind.Image"/> (rasterización
    /// via SvgRenderHelper). Los tipos audio/video/código estrenan sus propios valores del enum.</summary>
    public static VaultFileKind KindFromExtension(string extension)
    {
        var normalized = extension.StartsWith('.') ? extension : $".{extension}";
        if (normalized.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return VaultFileKind.Pdf;
        if (Array.Exists(ImageExtensions, e => e.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return VaultFileKind.Image;
        if (Array.Exists(SvgExtensions, e => e.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return VaultFileKind.Image;
        if (Array.Exists(AudioExtensions, e => e.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return VaultFileKind.Audio;
        if (Array.Exists(VideoExtensions, e => e.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return VaultFileKind.Video;
        if (Array.Exists(CodeExtensions, e => e.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return VaultFileKind.Code;
        return VaultFileKind.Other;
    }
}

public enum VaultFileKind { Image, Pdf, Audio, Video, Code, Other }
