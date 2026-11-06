using System;
using System.IO;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="NoteImageAttachments"/>, las reglas de nombres y rutas de los adjuntos de
/// imagen de una nota. Todo aquí es aritmética de rutas, así que no hace falta ni vault ni disco: es
/// justo por eso que estas decisiones viven separadas de <see cref="NoteImageService"/>.</summary>
public class NoteImageAttachmentsTests
{
    [Fact]
    public void FolderFor_IsNamedAfterTheNoteAndSitsBesideIt()
    {
        var notePath = Path.Combine("C:", "vault", "proyectos", "Proyecto.md");

        var folder = NoteImageAttachments.FolderFor(notePath);

        Assert.Equal(Path.Combine("C:", "vault", "proyectos", "Proyecto.attachments"), folder);
    }

    [Fact]
    public void FolderFor_WithoutContainingDirectory_ReturnsNull() =>
        Assert.Null(NoteImageAttachments.FolderFor("Proyecto.md"));

    [Fact]
    public void TimestampedFileName_UsesInvariantTimestampAndLowercaseExtension()
    {
        var name = NoteImageAttachments.TimestampedFileName(".PNG", new DateTime(2026, 8, 28, 14, 30, 12));

        Assert.Equal("imagen-20260828-143012.png", name);
    }

    [Theory]
    [InlineData("Captura de pantalla.png", "Captura-de-pantalla.png")]
    [InlineData("foto  con   huecos.jpg", "foto-con-huecos.jpg")]
    [InlineData("   .png", "imagen.png")]
    [InlineData("sinextension", "sinextension.png")]
    public void SanitizeFileName_RemovesSpacesAndKeepsAUsableName(string input, string expected) =>
        Assert.Equal(expected, NoteImageAttachments.SanitizeFileName(input));

    [Fact]
    public void MarkdownReference_IsRelativeToTheNoteAndUsesForwardSlashes()
    {
        var noteDirectory = Path.Combine(Path.GetTempPath(), "vault");
        var notePath = Path.Combine(noteDirectory, "Proyecto.md");
        var imagePath = Path.Combine(noteDirectory, "Proyecto.attachments", "imagen-20260828-143012.png");

        var reference = NoteImageAttachments.MarkdownReference(notePath, imagePath);

        Assert.Equal("Proyecto.attachments/imagen-20260828-143012.png", reference);
    }

    [Fact]
    public void MarkdownReference_EscapesWhatWouldBreakTheMarkdownParentheses()
    {
        var noteDirectory = Path.Combine(Path.GetTempPath(), "vault");
        var notePath = Path.Combine(noteDirectory, "Proyecto.md");
        var imagePath = Path.Combine(noteDirectory, "mis fotos", "foto (1).png");

        var reference = NoteImageAttachments.MarkdownReference(notePath, imagePath);

        Assert.Equal("mis%20fotos/foto%20%281%29.png", reference);
    }

    [Theory]
    [InlineData("foto.png", true)]
    [InlineData("FOTO.JPEG", true)]
    [InlineData("dibujo.svg", true)]
    [InlineData("nota.md", false)]
    [InlineData("", false)]
    public void IsImageFile_JudgesByExtension(string path, bool expected) =>
        Assert.Equal(expected, NoteImageAttachments.IsImageFile(path));
}
