using System;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Extracción de contenido del web clipper (T3). Función pura de una cadena HTML a metadatos +
/// Markdown, así que se prueba con documentos escritos a mano que reproducen lo que de verdad trae una
/// página real: el menú y el pie compitiendo con el artículo, el título repetido en tres sitios distintos,
/// y un <c>script</c> en medio del cuerpo.</summary>
public class HtmlReadabilityTests
{
    private const string Filler =
        "Este párrafo existe para que el bloque supere el mínimo de contenido que el extractor exige " +
        "antes de considerar que ha encontrado un artículo y no un aviso de cookies. ";

    [Fact]
    public void Extract_PrefersOpenGraphTitleOverTheDocumentTitle()
    {
        var article = HtmlReadability.Extract($"""
            <html><head>
              <title>Mi artículo — El Periódico</title>
              <meta property="og:title" content="Mi artículo">
              <meta name="description" content="Un resumen corto.">
              <meta name="author" content="Ada Lovelace">
              <meta property="article:published_time" content="2026-03-14T10:00:00Z">
            </head><body><article><p>{Filler}{Filler}</p></article></body></html>
            """);

        Assert.Equal("Mi artículo", article.Title);
        Assert.Equal("Un resumen corto.", article.Description);
        Assert.Equal("Ada Lovelace", article.Author);
        Assert.Equal("2026-03-14", article.Published);
    }

    [Fact]
    public void Extract_FallsBackToTheDocumentTitleAndThenToTheFirstHeading()
    {
        Assert.Equal("Solo title", HtmlReadability.Extract("<html><head><title>Solo title</title></head><body></body></html>").Title);
        Assert.Equal("Solo h1", HtmlReadability.Extract("<html><body><h1>Solo h1</h1></body></html>").Title);
    }

    [Fact]
    public void Extract_PicksTheArticleOverTheNavigationAndTheFooter()
    {
        var article = HtmlReadability.Extract($"""
            <html><body>
              <nav class="site-nav"><ul><li><a href="/a">Inicio</a></li><li><a href="/b">Blog</a></li></ul></nav>
              <div class="sidebar"><p>Suscríbete a la newsletter y no te pierdas nada de nada de nada.</p></div>
              <article><p>{Filler}</p><p>El cuerpo de verdad. {Filler}</p></article>
              <footer><p>Todos los derechos reservados para siempre jamás, hasta el fin de los tiempos.</p></footer>
            </body></html>
            """);

        Assert.Contains("El cuerpo de verdad.", article.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("newsletter", article.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Inicio", article.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("derechos reservados", article.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_NeverKeepsScriptOrStyleContent()
    {
        var article = HtmlReadability.Extract($$"""
            <html><body><article>
              <style>.x { color: red; }</style>
              <p>{{Filler}}</p>
              <script>var secreto = "no debe salir"; if (1 < 2) alert("ni esto");</script>
              <p>Texto tras el script. {{Filler}}</p>
              <noscript><p>Activa JavaScript por favor.</p></noscript>
            </article></body></html>
            """);

        Assert.DoesNotContain("secreto", article.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("color: red", article.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Activa JavaScript", article.Markdown, StringComparison.Ordinal);
        Assert.Contains("Texto tras el script.", article.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_ConvertsTheUsualBlocksToMarkdown()
    {
        var article = HtmlReadability.Extract($"""
            <html><body><article>
              <h2>Un encabezado</h2>
              <p>Con <strong>negrita</strong>, <em>cursiva</em>, <code>código</code> y un
                 <a href="https://ejemplo.org/x">enlace</a>. {Filler}</p>
              <ul><li>Primero</li><li>Segundo</li></ul>
              <ol><li>Uno</li><li>Dos</li></ol>
              <blockquote><p>Una cita.</p></blockquote>
              <pre><code>dotnet build</code></pre>
              <hr>
              <img src="https://ejemplo.org/i.png" alt="Una imagen">
            </article></body></html>
            """);

        Assert.Contains("## Un encabezado", article.Markdown, StringComparison.Ordinal);
        Assert.Contains("**negrita**", article.Markdown, StringComparison.Ordinal);
        Assert.Contains("*cursiva*", article.Markdown, StringComparison.Ordinal);
        Assert.Contains("`código`", article.Markdown, StringComparison.Ordinal);
        Assert.Contains("[enlace](https://ejemplo.org/x)", article.Markdown, StringComparison.Ordinal);
        Assert.Contains("- Primero", article.Markdown, StringComparison.Ordinal);
        Assert.Contains("1. Uno", article.Markdown, StringComparison.Ordinal);
        Assert.Contains("> Una cita.", article.Markdown, StringComparison.Ordinal);
        Assert.Contains("```\ndotnet build\n```", article.Markdown, StringComparison.Ordinal);
        Assert.Contains("---", article.Markdown, StringComparison.Ordinal);
        Assert.Contains("![Una imagen](https://ejemplo.org/i.png)", article.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_ConvertsTablesWithASeparatorRow()
    {
        var article = HtmlReadability.Extract($"""
            <html><body><article><p>{Filler}</p>
              <table>
                <tr><th>Clave</th><th>Valor</th></tr>
                <tr><td>uno</td><td>1</td></tr>
              </table>
            </article></body></html>
            """);

        Assert.Contains("| Clave | Valor |", article.Markdown, StringComparison.Ordinal);
        Assert.Contains("| --- | --- |", article.Markdown, StringComparison.Ordinal);
        Assert.Contains("| uno | 1 |", article.Markdown, StringComparison.Ordinal);
    }

    /// <summary>Un enlace que no se puede abrir (un <c>#</c> de JavaScript, un <c>javascript:</c>) aporta
    /// su texto pero no su destino: la nota no debe quedarse con destinos que <see cref="ExternalLinkPolicy"/>
    /// bloquearía después al pulsarlos.</summary>
    [Fact]
    public void Extract_KeepsTheLabelButDropsUnusableLinkTargets()
    {
        var article = HtmlReadability.Extract($"""
            <html><body><article><p>{Filler}
              <a href="javascript:alert(1)">pulsa</a> y <a href="#seccion">salta</a> y
              <a href="https://ok.example/p">vale</a>.</p></article></body></html>
            """);

        Assert.DoesNotContain("javascript:", article.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("](#seccion)", article.Markdown, StringComparison.Ordinal);
        Assert.Contains("pulsa", article.Markdown, StringComparison.Ordinal);
        Assert.Contains("[vale](https://ok.example/p)", article.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_DecodesEntities()
    {
        var article = HtmlReadability.Extract($"""
            <html><head><title>Caf&eacute; &amp; t&eacute;</title></head>
            <body><article><p>1 &lt; 2 &amp;&amp; 3 &gt; 2. {Filler}{Filler}</p></article></body></html>
            """);

        Assert.Equal("Café & té", article.Title);
        Assert.Contains("1 < 2 && 3 > 2.", article.Markdown, StringComparison.Ordinal);
    }

    /// <summary>El caso que la alternativa del clipper existe para cubrir: la página se descarga pero no
    /// trae texto extraíble (se pinta entera con JavaScript, o es un muro de pago). No es un error — se
    /// devuelve el título y ningún cuerpo, y quien llama crea la nota solo con título y enlace.</summary>
    [Theory]
    [InlineData("<html><head><title>Vacía</title></head><body><div id=\"app\"></div></body></html>")]
    [InlineData("<html><head><title>Corta</title></head><body><p>Dos palabras.</p></body></html>")]
    [InlineData("")]
    public void Extract_ReturnsNoBodyWhenThereIsNothingToRead(string html)
    {
        Assert.Empty(HtmlReadability.Extract(html).Markdown);
    }

    /// <summary>HTML mal formado —etiquetas sin cerrar, cierres huérfanos, atributos sin comillas— no debe
    /// lanzar ni perder el texto: es lo que devuelve media web real.</summary>
    [Fact]
    public void Extract_SurvivesMalformedMarkup()
    {
        var article = HtmlReadability.Extract($"""
            <html><body><div class=post>
              <p>Primero sin cerrar. {Filler}
              <p>Segundo sin cerrar. {Filler}
              </span></div></em>
            """);

        Assert.Contains("Primero sin cerrar.", article.Markdown, StringComparison.Ordinal);
        Assert.Contains("Segundo sin cerrar.", article.Markdown, StringComparison.Ordinal);
    }
}
