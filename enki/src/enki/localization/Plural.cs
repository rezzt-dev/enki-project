using System.Globalization;

namespace enki.Localization;

/// <summary>Minimal CLDR-style plural resolution for the 6 supported languages — see
/// docs/agent-docs/internacionalizacion-i18n.md §1.4. Every other supported language (en-US, en-GB, de-DE,
/// es-ES, es-419) is singular exactly at 1 and plural otherwise; French additionally treats 0 as singular.
/// Callers provide a resource key without suffix and two variants must exist:
/// <c>{key}_one</c>/<c>{key}_other</c>.</summary>
public static class Plural
{
    public static string Get(string key, int count, params object[] args)
    {
        var isSingular = LocalizationService.Instance.CurrentCulture.TwoLetterISOLanguageName == "fr"
            ? count is 0 or 1
            : count == 1;
        var resolved = LocalizationService.Instance[isSingular ? $"{key}_one" : $"{key}_other"];
        return string.Format(LocalizationService.Instance.CurrentCulture, resolved, args);
    }
}
