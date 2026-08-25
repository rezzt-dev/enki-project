using System;
using System.IO;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="NoteExportRenderer"/> — the Markdown→HTML conversion behind "Exportar como
/// HTML" / "Imprimir / PDF" (propuesta 6 de docs/propuesta-nuevas-funcionalidades.md), built on the same
/// <c>MarkdownLineParser</c> the editor's live preview uses.</summary>
public class NoteExportRendererTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private static NoteFile Note(string title = "Mi nota") => new()
    {
        FullPath = "/vault/" + title + ".md",
        Title = title
    };

    private static string Body(string markdown) => NoteExportRenderer.RenderBodyHtml(markdown, noteDirectory: null);

    [Fact]
    public void RenderBody_Headings_RenderAtTheirLevel()
    {
        var html = Body("# Uno\n\n### Tres");

        Assert.Contains("<h1>Uno</h1>", html);
        Assert.Contains("<h3>Tres</h3>", html);
    }

    [Fact]
    public void RenderBody_InlineSpans_RenderAsHtmlTags()
    {
        var html = Body("**negrita** _cursiva_ ~~tachado~~ `code` ==marca==");

        Assert.Contains("<strong>negrita</strong>", html);
        Assert.Contains("<em>cursiva</em>", html);
        Assert.Contains("<del>tachado</del>", html);
        Assert.Contains("<code>code</code>", html);
        Assert.Contains("<mark>marca</mark>", html);
    }

    [Fact]
    public void RenderBody_ConsecutiveParagraphLines_JoinWithBr()
    {
        var html = Body("primera línea\nsegunda línea");

        // HtmlEncode codifica los no-ASCII como entidades (línea → l&#237;nea) — el HTML resultante es
        // ASCII puro y seguro en cualquier contexto.
        Assert.Contains("primera l&#237;nea<br/>\nsegunda l&#237;nea", html);
    }

    [Fact]
    public void RenderBody_BlankLines_SplitParagraphs()
    {
        var html = Body("párrafo uno\n\npárrafo dos");

        Assert.Contains("<p>p&#225;rrafo uno</p>", html);
        Assert.Contains("<p>p&#225;rrafo dos</p>", html);
    }

    [Fact]
    public void RenderBody_EscapesRawHtml_InTextAndCode()
    {
        var html = Body("<script>alert(1)</script> y `<b>no es negrita</b>`");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("<code>&lt;b&gt;no es negrita&lt;/b&gt;</code>", html);
    }

    [Fact]
    public void RenderBody_ResolvesBackslashEscapes_ButNotInsideCode()
    {
        var html = Body("\\*no cursiva\\* y `\\*codigo\\*`");

        Assert.Contains("*no cursiva*", html);
        Assert.Contains("<code>\\*codigo\\*</code>", html);
    }

    [Fact]
    public void RenderBody_CodeFence_DumpsVerbatimContent()
    {
        var html = Body("```cs\nvar x = \"**no bold**\";\n```");

        Assert.Contains("<pre><code class=\"language-cs\">", html);
        Assert.Contains("var x = &quot;**no bold**&quot;;", html);
        Assert.DoesNotContain("<strong>", html);
    }

    [Fact]
    public void RenderBody_UnclosedFence_StillClosesPre()
    {
        var html = Body("```\nabierto");

        Assert.Contains("<pre><code>abierto\n</code></pre>", html);
    }

    [Fact]
    public void RenderBody_BulletAndOrderedLists_GroupConsecutiveItems()
    {
        var html = Body("- uno\n- dos\n\n1. primero\n2. segundo");

        Assert.Contains("<ul>\n<li>uno</li>\n<li>dos</li>\n</ul>", html);
        Assert.Contains("<ol>\n<li>primero</li>\n<li>segundo</li>\n</ol>", html);
    }

    [Fact]
    public void RenderBody_OrderedList_PreservesStartNumber()
    {
        var html = Body("3. tercero\n4. cuarto");

        Assert.Contains("<ol start=\"3\">", html);
    }

    [Fact]
    public void RenderBody_TaskList_RendersCheckboxes()
    {
        var html = Body("- [ ] pendiente\n- [x] hecha");

        Assert.Contains("<ul class=\"tasks\">", html);
        Assert.Contains("<input type=\"checkbox\" disabled/> pendiente", html);
        Assert.Contains("<input type=\"checkbox\" disabled checked/> hecha", html);
    }

    [Fact]
    public void RenderBody_Blockquote_GroupsConsecutiveLines()
    {
        var html = Body("> primera\n> segunda\n\nfuera");

        Assert.Contains("<blockquote><p>primera<br/>\nsegunda</p></blockquote>", html);
        Assert.Contains("<p>fuera</p>", html);
    }

    [Fact]
    public void RenderBody_HorizontalRule_RendersHr()
    {
        Assert.Contains("<hr/>", Body("arriba\n\n---\n\nabajo"));
    }

    [Fact]
    public void RenderBody_Link_RendersAnchor_WithSafeUrl()
    {
        var html = Body("[Enki](https://example.com)");

        Assert.Contains("<a href=\"https://example.com\" target=\"_blank\" rel=\"noopener noreferrer\">Enki</a>", html);
    }

    [Fact]
    public void RenderBody_Link_WithJavascriptUrl_DegradesToPlainText()
    {
        var html = Body("[pulsa](javascript:alert(1))");

        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("<a ", html);
        Assert.Contains("pulsa", html);
    }

    [Fact]
    public void RenderBody_WikiLink_RendersAliasWithoutBrackets()
    {
        var html = Body("ver [[Nota destino|el alias]] aquí");

        Assert.Contains("<span class=\"wikilink\">el alias</span>", html);
        Assert.DoesNotContain("[[", html);
    }

    [Fact]
    public void RenderBody_Image_WithRemoteUrl_RendersImg()
    {
        var html = Body("![foto](https://example.com/foto.png)");

        // El '!' de la sintaxis de imagen lo absorbe el span — no debe quedar como texto literal.
        Assert.Contains("<img src=\"https://example.com/foto.png\" alt=\"foto\"/>", html);
        Assert.DoesNotContain("!<img", html);
    }

    [Fact]
    public void RenderBody_Image_AfterOtherSpan_KeepsNoStrayBang()
    {
        var html = Body("**a**![b](https://example.com/b.png)");

        Assert.Contains("<strong>a</strong><img src=\"https://example.com/b.png\" alt=\"b\"/>", html);
    }

    [Fact]
    public void RenderBody_Image_WithLocalFile_EmbedsDataUri()
    {
        Directory.CreateDirectory(_storageDirectory);
        var imagePath = Path.Combine(_storageDirectory, "img.png");
        File.WriteAllBytes(imagePath, [1, 2, 3]);

        var html = NoteExportRenderer.RenderBodyHtml("![local](img.png)", _storageDirectory);

        Assert.Contains($"<img src=\"data:image/png;base64,{Convert.ToBase64String(new byte[] { 1, 2, 3 })}\" alt=\"local\"/>", html);
    }

    [Fact]
    public void RenderBody_Image_WithMissingLocalFile_KeepsOriginalPath()
    {
        var html = NoteExportRenderer.RenderBodyHtml("![local](no-existe.png)", _storageDirectory);

        Assert.Contains("<img src=\"no-existe.png\" alt=\"local\"/>", html);
    }

    [Fact]
    public void RenderBody_Image_WithPreexistingDataUri_KeepsItAsIs()
    {
        // Fase 8: a data: image (pasted straight into the note, or left over from a previous export) used
        // to be misclassified as an unsafe URL scheme and silently dropped to alt-text-only, even though
        // it's already a fully self-contained src that needs no further work.
        var dataUri = $"data:image/png;base64,{Convert.ToBase64String(new byte[] { 1, 2, 3 })}";

        var html = Body($"![local]({dataUri})");

        Assert.Contains($"<img src=\"{dataUri}\" alt=\"local\"/>", html);
    }

    [Fact]
    public void RenderBody_Image_WithWindowsAbsolutePath_KeepsOriginalPathInsteadOfDiscardingIt()
    {
        // Fase 8: "C:\..." matches the same scheme-detection regex a real URL scheme does (a letter run
        // followed by ':'), so it used to be misclassified as an unsafe scheme and dropped to alt-text-only
        // instead of being treated as the local path it is (the file won't exist on this machine either
        // way, but it must still render as an <img>, not disappear).
        var html = Body("![local](C:\\Users\\foo\\img.png)");

        Assert.Contains("<img src=\"C:\\Users\\foo\\img.png\" alt=\"local\"/>", html);
    }

    [Fact]
    public void RenderBody_Link_WithWindowsAbsolutePath_RendersAnchorInsteadOfDiscardingIt()
    {
        var html = Body("[archivo](C:/Users/foo/informe.pdf)");

        Assert.Contains("<a href=\"C:/Users/foo/informe.pdf\" target=\"_blank\" rel=\"noopener noreferrer\">archivo</a>", html);
    }

    [Fact]
    public void RenderBody_Link_WithDataUri_StillDegradesToPlainText()
    {
        // Unlike images, links keep the stricter allowlist: a data:text/html link opened in a new tab
        // could execute script, so AppendLink deliberately doesn't get the same data:image/... carve-out.
        var html = Body("[pulsa](data:text/html,hola)");

        Assert.DoesNotContain("<a ", html);
        Assert.Contains("pulsa", html);
    }

    [Fact]
    public void RenderBody_StripsFrontmatter_Always()
    {
        var html = Body("---\ntags: [a, b]\n---\n\nContenido");

        Assert.DoesNotContain("tags", html);
        Assert.Contains("<p>Contenido</p>", html);
    }

    [Fact]
    public void RenderDocument_IncludesTagsOnlyWhenRequested()
    {
        var note = Note();
        note.Tags.AddRange(new[] { "receta", "cocina" });
        const string content = "---\ntags: [receta, cocina]\n---\n\nCuerpo";

        var withTags = NoteExportRenderer.RenderDocument(note, content, includeTags: true);
        var withoutTags = NoteExportRenderer.RenderDocument(note, content, includeTags: false);

        Assert.Contains("<span class=\"doc-tag\">#receta</span>", withTags);
        // La clase CSS .doc-tag siempre está en la hoja de estilo incrustada — lo que no debe aparecer es
        // el span de etiqueta en sí.
        Assert.DoesNotContain("<span class=\"doc-tag\">", withoutTags);
    }

    [Fact]
    public void RenderDocument_EscapesTitle_AndWrapsBodyInStandaloneTemplate()
    {
        var note = Note("Nota <b>rara</b>");

        var html = NoteExportRenderer.RenderDocument(note, "Cuerpo", includeTags: true);

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<title>Nota &lt;b&gt;rara&lt;/b&gt;</title>", html);
        Assert.Contains("<h1 class=\"doc-title\">Nota &lt;b&gt;rara&lt;/b&gt;</h1>", html);
        Assert.Contains("@page", html);
        Assert.Contains("<p>Cuerpo</p>", html);
    }
}
