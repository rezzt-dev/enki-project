using System.ComponentModel;
using System.Globalization;
using System.Threading;

namespace enki.Localization;

/// <summary>Singleton that resolves resource keys against the active <see cref="CurrentCulture"/> and lets
/// the whole app re-render in a new language without restarting — see
/// docs/agent-docs/internacionalizacion-i18n.md §1.2. Bind XAML via <see cref="TrExtension"/>
/// (<c>{loc:Tr Some_Key}</c>) or the indexer directly (<c>{Binding [Some_Key], Source={x:Static
/// loc:LocalizationService.Instance}}</c>); from C#, call <c>LocalizationService.Instance["Some_Key"]</c>.</summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo CurrentCulture { get; private set; } = new(SupportedLanguages.SourceCultureCode);

    private LocalizationService()
    {
    }

    public string this[string key] => Resolve(key);

    /// <summary>In Debug, a key missing from the active culture (and from the source fallback) renders as
    /// <c>[[Key]]</c> so a hole in translation coverage is obvious at a glance while running the app. In
    /// Release it instead falls back silently to the source (en-US) string, then to the bare key, so an
    /// incomplete language degrades gracefully for real users instead of showing brackets — see §1.2/§1.5.</summary>
    private string Resolve(string key)
    {
        var value = Strings.ResourceManager.GetString(key, CurrentCulture);
        if (value is not null) return value;

#if DEBUG
        return $"[[{key}]]";
#else
        var sourceValue = Strings.ResourceManager.GetString(key, new CultureInfo(SupportedLanguages.SourceCultureCode));
        return sourceValue ?? key;
#endif
    }

    /// <summary>Switches the active language in place. Updates the ambient thread/app culture too, so any
    /// culture-sensitive formatting elsewhere (date/number <c>ToString</c> without an explicit culture) picks
    /// up the change automatically, then raises a "refresh every indexer binding" signal so every
    /// <c>{loc:Tr ...}</c>-bound control in the currently-open UI updates immediately — no restart, no
    /// per-view wiring. See §1.2/§3.3.</summary>
    public void SetLanguage(string cultureCode)
    {
        CurrentCulture = new CultureInfo(cultureCode);
        CultureInfo.CurrentCulture = CurrentCulture;
        CultureInfo.CurrentUICulture = CurrentCulture;
        Thread.CurrentThread.CurrentCulture = CurrentCulture;
        Thread.CurrentThread.CurrentUICulture = CurrentCulture;

        // Unlike WPF (which watches for the sentinel name "Item[]"), Avalonia's reflection indexer binding
        // (ReflectionIndexerNode.ShouldUpdate) refreshes when it sees a PropertyChanged whose PropertyName
        // matches the indexer's actual declared property name — "Item" for a plain `this[string key]` with
        // no [IndexerName] override, which is what this class declares.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
    }
}
