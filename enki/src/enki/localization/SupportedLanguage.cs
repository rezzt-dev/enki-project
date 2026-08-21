using System;
using System.Collections.Generic;
using System.Linq;

namespace enki.Localization;

public enum SupportedLanguage
{
    EnUs,
    EnGb,
    DeDe,
    EsEs,
    Es419,
    FrFr,
}

/// <summary>BCP-47 culture code + native-language display name for one of Enki's 6 supported UI languages.
/// <see cref="NativeName"/> is meant for the first-run language picker
/// (docs/agent-docs/internacionalizacion-i18n.md §3.1) — it is deliberately static content, never itself run
/// through <see cref="LocalizationService"/>, so a German speaker can recognise "Deutsch" no matter what
/// language the picker currently defaults to.</summary>
public sealed record SupportedLanguageInfo(SupportedLanguage Language, string CultureCode, string NativeName);

public static class SupportedLanguages
{
    /// <summary>Culture of the neutral/source resource file, <c>localization/Strings.resx</c> — see §1.1.
    /// Used as <see cref="LocalizationService"/>'s initial <see cref="LocalizationService.CurrentCulture"/>
    /// before a language has been chosen or loaded from <c>Models.AppSettings.Language</c>, and as the
    /// Release-mode fallback for any key missing from the active language (§1.5).</summary>
    public const string SourceCultureCode = "en-US";

    public static readonly IReadOnlyList<SupportedLanguageInfo> All =
    [
        new(SupportedLanguage.EnUs, "en-US", "English (US)"),
        new(SupportedLanguage.EnGb, "en-GB", "English (UK)"),
        new(SupportedLanguage.DeDe, "de-DE", "Deutsch"),
        new(SupportedLanguage.EsEs, "es-ES", "Español (España)"),
        new(SupportedLanguage.Es419, "es-419", "Español (Latinoamérica)"),
        new(SupportedLanguage.FrFr, "fr-FR", "Français"),
    ];

    public static SupportedLanguageInfo? Find(string? cultureCode) =>
        All.FirstOrDefault(l => string.Equals(l.CultureCode, cultureCode, StringComparison.OrdinalIgnoreCase));
}
