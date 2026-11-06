using System;
using System.Globalization;
using System.IO;
using System.Linq;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Corrector ortográfico local (MED-05) con diccionarios Hunspell mínimos creados en una carpeta
/// temporal: las pruebas no dependen de ningún diccionario real ni de la red.</summary>
public sealed class SpellCheckServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enki-spell-" + Guid.NewGuid().ToString("N"));

    public SpellCheckServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Dictionaries(string name = "es_ES")
    {
        var directory = Path.Combine(_root, "dictionaries");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".aff"), "SET UTF-8\nSFX S Y 1\nSFX S 0 s .\n");
        File.WriteAllText(Path.Combine(directory, name + ".dic"), "9\ncasa/S\nperro/S\ngato/S\nel\nla\nde\nuna\ny\nniño/S\n");
        return directory;
    }

    private SpellCheckService Ready(string name = "es_ES", string culture = "es-ES")
    {
        var service = new SpellCheckService([Dictionaries(name)], Path.Combine(_root, "personal.txt"));
        Assert.True(service.TrySelectLanguage(CultureInfo.GetCultureInfo(culture)));
        return service;
    }

    private static string[] Flagged(SpellCheckService service, string text) =>
        service.FindMisspellings(text).Select(r => text.Substring(r.Start, r.Length)).ToArray();

    [Fact]
    public void WithoutADictionary_NothingIsFlagged()
    {
        var service = new SpellCheckService([Path.Combine(_root, "no-existe")]);

        Assert.False(service.TrySelectLanguage(CultureInfo.GetCultureInfo("es-ES")));
        Assert.False(service.IsReady);
        Assert.Null(service.ActiveDictionary);
        Assert.Empty(service.FindMisspellings("palabrotas inventadas"));
        Assert.True(service.Check("palabrotas"));
    }

    [Fact]
    public void ADicWithoutItsAff_IsNotUsed()
    {
        var directory = Dictionaries();
        File.Delete(Path.Combine(directory, "es_ES.aff"));
        var service = new SpellCheckService([directory]);

        Assert.False(service.TrySelectLanguage(CultureInfo.GetCultureInfo("es-ES")));
    }

    [Fact]
    public void KnownWordsAndAffixForms_Pass_UnknownOnesAreFlaggedWithTheirPosition()
    {
        var service = Ready();
        const string text = "El perro y la casas, el gatoz.";

        var ranges = service.FindMisspellings(text);

        var range = Assert.Single(ranges);
        Assert.Equal("gatoz", text.Substring(range.Start, range.Length));
        Assert.Equal("es_ES", service.ActiveDictionary);
    }

    [Fact]
    public void Check_RespectsAccentsAndTheEnye()
    {
        var service = Ready();

        Assert.True(service.Check("niño"));
        Assert.True(service.Check("niños"));
        Assert.False(service.Check("nino"));
    }

    [Theory]
    [InlineData("es-419", "es_MX")]
    [InlineData("es-ES", "es")]
    public void Language_FallsBackToAnotherVariantOrTheBareLanguage(string culture, string file)
    {
        var service = Ready(file, culture);

        Assert.Equal(file, service.ActiveDictionary);
    }

    [Fact]
    public void ADifferentLanguage_DoesNotUseTheSpanishDictionary()
    {
        var service = new SpellCheckService([Dictionaries()]);

        Assert.False(service.TrySelectLanguage(CultureInfo.GetCultureInfo("en-GB")));
    }

    [Theory]
    [InlineData("Mira `palabrota` aquí")]
    [InlineData("```\npalabrota\nfoo()\n```")]
    [InlineData("Un [enlace raro](https://ejemplo.org/palabrota) más")]
    [InlineData("Un [[Nota palabrota]] más")]
    [InlineData("Dirección https://palabrota.org/zzz fin")]
    [InlineData("---\ntitulo: palabrota\n---\nel gato")]
    [InlineData("Fórmula $palabrota$ fin")]
    [InlineData("<span class=\"palabrota\">el gato</span>")]
    [InlineData("Una #palabrota y @palabrota")]
    [InlineData("Un archivo.txt, una variable_zzz y un camelCase")]
    [InlineData("La NASA y el HTML")]
    [InlineData("Ref abc123 y 3rd")]
    public void CodeLinksPropertiesAndIdentifiers_AreIgnored(string text)
    {
        var service = Ready();
        // El diccionario de prueba es mínimo: las palabras corrientes de la frase («Un», «más»…) salen como
        // desconocidas y no importan. Lo que se comprueba es que nada de lo ignorable llega a revisarse.
        string[] ignorable = ["palabrota", "zzz", "txt", "camel", "abc", "NASA", "HTML", "ejemplo", "titulo", "span", "class", "Nota", "enlace", "raro", "3rd"];

        Assert.DoesNotContain(Flagged(service, text), word => ignorable.Any(i => word.Contains(i, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ProseAroundIgnoredRegions_IsStillChecked()
    {
        var service = Ready();
        const string text = "---\na: b\n---\nel gatoz `perroz` y [x](y) la casaz";

        Assert.Equal(["gatoz", "casaz"], Flagged(service, text));
    }

    [Fact]
    public void PersonalDictionary_IsPersistedAndReloaded()
    {
        var service = Ready();
        Assert.Contains("Enkiz", Flagged(service, "el Enkiz"));
        var raised = 0;
        service.DictionaryChanged += () => raised++;

        service.AddWord("Enkiz");

        Assert.Empty(Flagged(service, "el Enkiz"));
        Assert.Empty(Flagged(service, "el enkiz"));
        Assert.Equal(1, raised);
        Assert.Contains("Enkiz", File.ReadAllLines(Path.Combine(_root, "personal.txt")));

        var other = new SpellCheckService([Path.Combine(_root, "dictionaries")], Path.Combine(_root, "personal.txt"));
        other.TrySelectLanguage(CultureInfo.GetCultureInfo("es-ES"));
        Assert.Empty(Flagged(other, "el Enkiz"));
    }

    [Fact]
    public void Suggestions_AreOfferedForAMisspelling()
    {
        var service = Ready();

        Assert.Contains("gato", service.Suggest("gatoz"));
    }

    [Fact]
    public void FindMisspellings_IsCappedOnHopelessText()
    {
        var service = Ready();
        var text = string.Join(' ', Enumerable.Repeat("xyzqw", SpellCheckService.MaxMisspellings + 500));

        Assert.Equal(SpellCheckService.MaxMisspellings, service.FindMisspellings(text).Count);
    }

    [Fact]
    public void LongNotes_AreCheckedQuickly()
    {
        var service = Ready();
        var paragraph = "El perro y la casa de una niña, con `código` y [[enlace]] y https://ejemplo.org/ruta zzzq.\n";
        var text = string.Concat(Enumerable.Repeat(paragraph, 20_000)); // ~1,9 MB

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ranges = service.FindMisspellings(text);
        watch.Stop();

        Assert.NotEmpty(ranges);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"tardó {watch.Elapsed}");
    }

    [Fact]
    public void Cancelling_StopsTheScan()
    {
        var service = Ready();
        using var cancel = new System.Threading.CancellationTokenSource();
        cancel.Cancel();

        Assert.Empty(service.FindMisspellings(string.Concat(Enumerable.Repeat("zzzq ", 1000)), cancel.Token));
    }
}
