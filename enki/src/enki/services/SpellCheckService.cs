using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using WeCantSpell.Hunspell;

namespace enki.Services;

/// <summary>Una palabra desconocida: desplazamiento y longitud dentro del texto revisado.</summary>
public readonly record struct SpellRange(int Start, int Length);

/// <summary>Corrector ortográfico local del editor (MED-05). Usa diccionarios Hunspell (<c>.dic</c> +
/// <c>.aff</c>) leídos del disco con WeCantSpell.Hunspell, que es código gestionado: no hay llamadas de red,
/// proceso externo ni biblioteca nativa, así que funciona sin conexión y en las tres plataformas.
///
/// <para><b>Idioma.</b> Se elige a partir del idioma de la aplicación (<c>es-ES</c> → <c>es_ES.dic</c>); si
/// no hay un diccionario exacto, el de otra variante del mismo idioma (<c>es-419</c> usa <c>es_ES</c> o
/// <c>es_MX</c>). Los diccionarios se buscan en la carpeta <c>dictionaries/</c> junto a la aplicación y en la
/// carpeta de datos del usuario (<see cref="UserDirectory"/>); sin diccionario para el idioma, el corrector
/// queda inactivo y no subraya nada, en vez de marcar como error cada palabra.</para>
///
/// <para><b>Qué se ignora.</b> Frontmatter, bloques y fragmentos de código, enlaces (wikilinks, Markdown y
/// direcciones), fórmulas y etiquetas HTML (<see cref="NoteRelationsService.MaskNonProse"/>), además de
/// <c>#etiquetas</c>, <c>@menciones</c>, rutas, palabras con cifras, siglas, <c>camelCase</c> y lo que el
/// usuario guarde en su diccionario personal (<c>personal-dictionary.txt</c>, una palabra por línea).</para>
///
/// <para>Es seguro llamarlo desde varios hilos una vez cargado el diccionario; la revisión de una nota larga
/// se hace fuera del hilo de interfaz (ver <c>SpellCheckController</c>).</para></summary>
public sealed class SpellCheckService
{
    public const string PersonalDictionaryFileName = "personal-dictionary.txt";

    /// <summary>Tope de palabras marcadas por revisión: una nota llena de jerga no debe producir miles de
    /// subrayados que ahoguen el repintado.</summary>
    public const int MaxMisspellings = 3000;

    private const int MaxCachedWords = 50_000;

    private static readonly Regex Word = new(@"[\p{L}\p{M}]+(?:['’][\p{L}\p{M}]+)*", RegexOptions.Compiled, RegexSafety.MatchTimeout);

    private static readonly Lazy<SpellCheckService> SharedInstance =
        new(() => new SpellCheckService([BundledDirectory, UserDirectory], Path.Combine(UserDirectory, PersonalDictionaryFileName)));

    private readonly IReadOnlyList<string> _directories;
    private readonly string? _personalPath;
    private readonly ConcurrentDictionary<string, bool> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _personal = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly object _gate = new();
    private WordList? _words;

    public SpellCheckService(IEnumerable<string> dictionaryDirectories, string? personalDictionaryPath = null)
    {
        _directories = dictionaryDirectories.ToList();
        _personalPath = personalDictionaryPath;
        LoadPersonal();
    }

    /// <summary>Instancia compartida por todos los editores, con las carpetas reales.</summary>
    public static SpellCheckService Shared => SharedInstance.Value;

    public static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "dictionaries");

    public static string UserDirectory => Path.Combine(AppDataDirectory.Resolve(), "dictionaries");

    /// <summary>Nombre del diccionario cargado (<c>es_ES</c>), o <see langword="null"/> si no hay ninguno.</summary>
    public string? ActiveDictionary { get; private set; }

    public bool IsReady => _words is not null;

    /// <summary>Avisa de que cambió el diccionario activo o el personal: los editores deben revisar de nuevo.</summary>
    public event Action? DictionaryChanged;

    /// <summary>Busca y carga el diccionario que corresponde a <paramref name="culture"/>. Devuelve
    /// <see langword="false"/> (y deja el corrector inactivo) si no hay ninguno utilizable. Lee el disco:
    /// llamarlo fuera del hilo de interfaz.</summary>
    public bool TrySelectLanguage(CultureInfo culture)
    {
        var found = FindDictionary(culture);
        WordList? loaded = null;
        if (found is { } path)
        {
            try { loaded = WordList.CreateFromFiles(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or FormatException)
            {
                LogService.Warning($"No se pudo cargar el diccionario ortográfico '{Path.GetFileName(path)}' ({ex.GetType().Name})");
            }
        }

        var name = loaded is null || found is null ? null : Path.GetFileNameWithoutExtension(found);
        lock (_gate)
        {
            if (ReferenceEquals(_words, loaded) && ActiveDictionary == name) return loaded is not null;
            _words = loaded;
            ActiveDictionary = name;
            _cache.Clear();
        }
        DictionaryChanged?.Invoke();
        return loaded is not null;
    }

    /// <summary>Ruta del <c>.dic</c> para <paramref name="culture"/>: el nombre exacto (<c>es_ES</c>), el
    /// idioma a secas (<c>es</c>) y, por último, cualquier variante del idioma. Un <c>.dic</c> sin su
    /// <c>.aff</c> no sirve y se descarta.</summary>
    public string? FindDictionary(CultureInfo culture)
    {
        var exact = culture.Name.Replace('-', '_');
        var language = culture.TwoLetterISOLanguageName;
        var candidates = new List<string> { exact, language };
        foreach (var directory in _directories)
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var name in candidates)
                if (HasPair(directory, name)) return Path.Combine(directory, name + ".dic");
        }
        foreach (var directory in _directories)
        {
            if (!Directory.Exists(directory)) continue;
            var variant = Directory.EnumerateFiles(directory, language + "_*.dic")
                .Where(f => HasPair(directory, Path.GetFileNameWithoutExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (variant is not null) return variant;
        }
        return null;
    }

    private static bool HasPair(string directory, string name) =>
        File.Exists(Path.Combine(directory, name + ".dic")) && File.Exists(Path.Combine(directory, name + ".aff"));

    /// <summary>¿Está bien escrita? Sin diccionario cargado todo se da por válido.</summary>
    public bool Check(string word)
    {
        var words = _words;
        if (words is null || word.Length == 0) return true;
        if (_cache.TryGetValue(word, out var known)) return known;

        var ok = _personal.Contains(word) || words.Check(word) || CheckParts(words, word);
        if (_cache.Count >= MaxCachedWords) _cache.Clear();
        _cache[word] = ok;
        return ok;
    }

    /// <summary>«l’histoire» y «don’t» con apóstrofo tipográfico: el diccionario guarda el recto.</summary>
    private static bool CheckParts(WordList words, string word)
    {
        if (!word.Contains('’', StringComparison.Ordinal)) return false;
        return words.Check(word.Replace('’', '\''));
    }

    /// <summary>Sugerencias para una palabra mal escrita, o ninguna si el corrector está inactivo.</summary>
    public IReadOnlyList<string> Suggest(string word, int max = 5) =>
        _words is { } words ? words.Suggest(word).Take(max).ToList() : [];

    /// <summary>Guarda <paramref name="word"/> en el diccionario personal y deja de marcarla.</summary>
    public void AddWord(string word)
    {
        word = word.Trim();
        if (word.Length == 0) return;
        lock (_gate)
        {
            if (!_personal.Add(word)) return;
            _cache.Clear();
            if (_personalPath is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_personalPath)!);
                    File.AppendAllText(_personalPath, word + Environment.NewLine);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogService.Warning("No se pudo guardar el diccionario personal", ex);
                }
            }
        }
        DictionaryChanged?.Invoke();
    }

    private void LoadPersonal()
    {
        if (_personalPath is null || !File.Exists(_personalPath)) return;
        try
        {
            foreach (var line in File.ReadLines(_personalPath).Take(50_000))
                if (line.Trim() is { Length: > 0 } word) _personal.Add(word);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning("No se pudo leer el diccionario personal", ex);
        }
    }

    /// <summary>Palabras desconocidas de <paramref name="text"/>, en orden y como máximo
    /// <see cref="MaxMisspellings"/>. No toca la interfaz: se puede ejecutar en un hilo de fondo.</summary>
    public IReadOnlyList<SpellRange> FindMisspellings(string text, CancellationToken cancel = default)
    {
        if (!IsReady || string.IsNullOrEmpty(text)) return [];

        var prose = NoteRelationsService.MaskNonProse(text);
        var result = new List<SpellRange>();
        try
        {
            foreach (Match match in Word.Matches(prose))
            {
                if (cancel.IsCancellationRequested || result.Count >= MaxMisspellings) break;
                if (!IsCandidate(prose, match)) continue;
                if (!Check(match.Value)) result.Add(new SpellRange(match.Index, match.Length));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Una nota patológica: se muestra lo encontrado hasta ahí.
        }
        return result;
    }

    /// <summary>¿Es una palabra de prosa que merece revisarse? Descarta lo que parece un identificador, una
    /// sigla, una etiqueta, una ruta o un nombre de archivo.</summary>
    internal static bool IsCandidate(string prose, Match match)
    {
        var word = match.Value;
        if (word.Length < 2) return false;

        var before = match.Index > 0 ? prose[match.Index - 1] : ' ';
        var after = match.Index + match.Length < prose.Length ? prose[match.Index + match.Length] : ' ';
        if (before is '#' or '@' or '_' or '/' or '\\' or '$' || char.IsDigit(before)) return false;
        if (after is '_' or '@' || char.IsDigit(after)) return false;
        // archivo.txt, ejemplo.com: el tramo tras el punto no es una palabra de la frase.
        if (before == '.' && match.Index > 1 && char.IsLetterOrDigit(prose[match.Index - 2])) return false;
        if (after == '.' && match.Index + match.Length + 1 < prose.Length && char.IsLower(prose[match.Index + match.Length + 1]) && word.All(char.IsLower)) return false;

        if (word.All(c => !char.IsLetter(c) || char.IsUpper(c)) && word.Length <= 8) return false;
        // camelCase / PascalCase / iPhone: mayúscula tras una minúscula.
        for (var i = 1; i < word.Length; i++)
            if (char.IsUpper(word[i]) && char.IsLower(word[i - 1])) return false;
        return true;
    }
}
