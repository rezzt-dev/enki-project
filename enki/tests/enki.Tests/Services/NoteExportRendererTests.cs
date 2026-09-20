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

        var html = NoteExportRenderer.RenderBodyHtml("![local](img.png)", _storageDirectory, _storageDirectory);

        Assert.Contains($"<img src=\"data:image/png;base64,{Convert.ToBase64String(new byte[] { 1, 2, 3 })}\" alt=\"local\"/>", html);
    }

    [Fact]
    public void RenderBody_Image_WithMissingLocalFile_KeepsOriginalPath()
    {
        var html = NoteExportRenderer.RenderBodyHtml("![local](no-existe.png)", _storageDirectory, _storageDirectory);

        Assert.Contains("<img src=\"no-existe.png\" alt=\"local\"/>", html);
    }

    [Fact]
    public void RenderBody_Image_OutsideVault_IsNeverEmbedded()
    {
        var vault = Path.Combine(_storageDirectory, "vault");
        var notes = Path.Combine(vault, "notas");
        Directory.CreateDirectory(notes);
        var privateImage = Path.Combine(_storageDirectory, "privada.png");
        File.WriteAllBytes(privateImage, [9, 8, 7]);

        var html = NoteExportRenderer.RenderBodyHtml("![privada](../../privada.png)", notes, vault);

        Assert.Contains("src=\"../../privada.png\"", html);
        Assert.DoesNotContain(Convert.ToBase64String(new byte[] { 9, 8, 7 }), html);
    }

    [Fact]
    public void RenderBody_Image_SymbolicLinkLeavingVault_IsNeverEmbedded()
    {
        if (OperatingSystem.IsWindows()) return;
        var vault = Path.Combine(_storageDirectory, "vault");
        Directory.CreateDirectory(vault);
        var privateImage = Path.Combine(_storageDirectory, "privada.png");
        File.WriteAllBytes(privateImage, [6, 6, 6]);
        File.CreateSymbolicLink(Path.Combine(vault, "enlace.png"), privateImage);

        var html = NoteExportRenderer.RenderBodyHtml("![privada](enlace.png)", vault, vault);

        Assert.Contains("src=\"enlace.png\"", html);
        Assert.DoesNotContain(Convert.ToBase64String(new byte[] { 6, 6, 6 }), html);
    }

    [Fact]
    public void RenderBody_Image_WithExplicitSize_EmitsWidthAndHeight()
    {
        var html = Body("![foto|300x200](https://example.com/foto.png)");

        Assert.Contains("<img src=\"https://example.com/foto.png\" alt=\"foto\" width=\"300\" height=\"200\"/>", html);
    }

    [Fact]
    public void RenderBody_Image_WithWidthOnly_LeavesHeightProportional()
    {
        var html = Body("![foto|300](https://example.com/foto.png)");

        Assert.Contains("<img src=\"https://example.com/foto.png\" alt=\"foto\" width=\"300\"/>", html);
    }

    [Fact]
    public void RenderBody_EmbeddedImageWikiLink_RendersImg()
    {
        Directory.CreateDirectory(_storageDirectory);
        var imagePath = Path.Combine(_storageDirectory, "captura.png");
        File.WriteAllBytes(imagePath, [4, 5, 6]);

        var html = NoteExportRenderer.RenderBodyHtml("![[captura.png]]", _storageDirectory, _storageDirectory);

        Assert.Contains($"<img src=\"data:image/png;base64,{Convert.ToBase64String(new byte[] { 4, 5, 6 })}\"", html);
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

        var withTags = NoteExportRenderer.RenderDocument(note, content, includeTags: true, "/vault");
        var withoutTags = NoteExportRenderer.RenderDocument(note, content, includeTags: false, "/vault");

        Assert.Contains("<span class=\"doc-tag\">#receta</span>", withTags);
        // La clase CSS .doc-tag siempre está en la hoja de estilo incrustada — lo que no debe aparecer es
        // el span de etiqueta en sí.
        Assert.DoesNotContain("<span class=\"doc-tag\">", withoutTags);
    }

    [Fact]
    public void RenderDocument_EscapesTitle_AndWrapsBodyInStandaloneTemplate()
    {
        var note = Note("Nota <b>rara</b>");

        var html = NoteExportRenderer.RenderDocument(note, "Cuerpo", includeTags: true, "/vault");

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<title>Nota &lt;b&gt;rara&lt;/b&gt;</title>", html);
        Assert.Contains("<h1 class=\"doc-title\">Nota &lt;b&gt;rara&lt;/b&gt;</h1>", html);
        Assert.Contains("@page", html);
        Assert.Contains("<p>Cuerpo</p>", html);
    }

    [Fact]
    public void RenderBody_HtmlBlock_ExportsAsAlignedContainer()
    {
        var html = Body("<div align=\"center\">\nHola\n</div>");

        // El align= de HTML4 se traduce a CSS: HTML5 ya no lo reconoce en <div>.
        Assert.Contains("<div style=\"text-align:center\">", html);
        Assert.Contains("<p>Hola</p>", html);
        Assert.Contains("</div>", html);
        // El contenedor es un bloque, no parte de un párrafo.
        Assert.DoesNotContain("<p><div", html);
    }

    [Fact]
    public void RenderBody_SingleLineHtmlBlock_KeepsItsText()
    {
        var html = Body("<p align=\"justify\">Un texto justificado</p>");

        Assert.Contains("<p style=\"text-align:justify\">Un texto justificado</p>", html);
    }

    [Fact]
    public void RenderBody_InlineHtmlTags_PassThrough()
    {
        var html = Body("hola <b>mundo</b> y <mark>fin</mark>");

        Assert.Contains("hola <b>mundo</b> y <mark>fin</mark>", html);
    }

    [Fact]
    public void RenderBody_InlineTagClosingOnALaterLine_StaysBalancedPerBlock()
    {
        var html = Body("<i>uno\ndos\ntres</i>\ncuatro");

        // Cada línea se cierra sola: un <i> sin cerrar no sobrevive al </p> del navegador, así que la
        // cursiva se reabre línea a línea hasta el </i> real y no se derrama sobre "cuatro".
        Assert.Contains("<i>uno</i>", html);
        Assert.Contains("<i>dos</i>", html);
        Assert.Contains("<i>tres</i>", html);
        Assert.Contains("cuatro</p>", html);
        Assert.DoesNotContain("<i>cuatro", html);
    }

    [Fact]
    public void RenderBody_HtmlAttributes_AreDropped()
    {
        var html = Body("<div align=\"right\" onclick=\"alert(1)\">texto</div>");

        Assert.DoesNotContain("onclick", html);
        Assert.Contains("<div style=\"text-align:right\">", html);
    }

    [Fact]
    public void RenderBody_HtmlInsideCodeFence_StaysEscaped()
    {
        var html = Body("```html\n<div align=\"center\">\n```");

        Assert.Contains("&lt;div align=&quot;center&quot;&gt;", html);
    }

    [Fact]
    public void RenderBody_UnknownTag_IsEscapedLikeAnyOtherText()
    {
        var html = Body("un <cualquiercosa> suelto");

        Assert.Contains("&lt;cualquiercosa&gt;", html);
    }

    [Fact]
    public void RenderBody_Table_RendersHeaderAndBodyRows()
    {
        var html = Body("| Fruta | Color |\n| --- | --- |\n| Manzana | Roja |\n| Uva | Verde |");

        Assert.Contains("<table>", html);
        Assert.Contains("<thead>\n<tr><th>Fruta</th><th>Color</th></tr>", html);
        Assert.Contains("<tbody>\n<tr><td>Manzana</td><td>Roja</td></tr>", html);
        Assert.Contains("<tr><td>Uva</td><td>Verde</td></tr>", html);
        Assert.Contains("</table>", html);
        // La fila separadora es sintaxis, no contenido: no debe aparecer como una fila más.
        Assert.DoesNotContain("---", html);
    }

    [Fact]
    public void RenderBody_TableColumnAlignment_TravelsAsACssClass()
    {
        var html = Body("| a | b | c |\n| :--- | :---: | ---: |\n| 1 | 2 | 3 |");

        Assert.Contains("<th class=\"ta-left\">a</th>", html);
        Assert.Contains("<th class=\"ta-center\">b</th>", html);
        Assert.Contains("<th class=\"ta-right\">c</th>", html);
        Assert.Contains("<td class=\"ta-center\">2</td>", html);
    }

    [Fact]
    public void RenderBody_TableWithoutExplicitAlignment_EmitsNoAlignmentClass()
    {
        var html = Body("| a |\n| --- |\n| 1 |");

        Assert.Contains("<th>a</th>", html);
        Assert.DoesNotContain("ta-", html);
    }

    [Fact]
    public void RenderBody_TableCells_RenderTheirInlineMarkdown()
    {
        var html = Body("| Estado | Enlace |\n| --- | --- |\n| **hecho** | [web](https://ejemplo.com) |");

        Assert.Contains("<td><strong>hecho</strong></td>", html);
        Assert.Contains("href=\"https://ejemplo.com\"", html);
    }

    [Fact]
    public void RenderBody_TableCellWithAnEscapedPipe_KeepsItAsContent()
    {
        var html = Body(@"| expresión |" + "\n| --- |\n" + @"| a \| b |");

        Assert.Contains("<td>a | b</td>", html);
    }

    [Fact]
    public void RenderBody_RowWithFewerCells_IsPaddedToTheTableWidth()
    {
        var html = Body("| a | b |\n| --- | --- |\n| solo |");

        Assert.Contains("<tr><td>solo</td><td></td></tr>", html);
    }

    [Fact]
    public void RenderBody_TableWithOnlyHeader_EmitsNoEmptyBody()
    {
        var html = Body("| a | b |\n| --- | --- |");

        Assert.Contains("<thead>", html);
        Assert.DoesNotContain("<tbody>", html);
    }

    [Fact]
    public void RenderBody_TextAfterATable_IsStillRenderedAsItsOwnBlock()
    {
        var html = Body("| a |\n| --- |\n| 1 |\n\nDespués de la tabla.");

        Assert.Contains("</table>", html);
        Assert.Contains("<p>Despu&#233;s de la tabla.</p>", html);
    }

    [Fact]
    public void RenderBody_TableInsideACodeFence_StaysLiteralCode()
    {
        var html = Body("```\n| a |\n| --- |\n```");

        Assert.DoesNotContain("<table>", html);
        Assert.Contains("| a |", html);
    }

    [Fact]
    public void RenderBody_PipesWithoutADelimiterRow_StayAParagraph()
    {
        var html = Body("uno | dos\ntres | cuatro");

        Assert.DoesNotContain("<table>", html);
        Assert.Contains("<p>uno | dos<br/>", html);
    }
}
