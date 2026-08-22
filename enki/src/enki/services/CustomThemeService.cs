using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Media;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>Owns the complete custom-theme lifecycle: create, edit, preview, activate, delete and
/// import/export as CSS. Every theme's colors are stored in a validated <c>.css</c> file under
/// <c>%AppData%/Enki/themes/{id}.css</c>, and a lightweight <see cref="CustomThemeInfo"/> record is
/// kept in <see cref="AppSettings.CustomThemes"/> for listing. Live application of colors is delegated
/// to <see cref="ThemeRegistry"/>.</summary>
public sealed class CustomThemeService
{
    private readonly SettingsService _settingsService;
    private readonly string _themesDirectory;

    public CustomThemeService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _themesDirectory = Path.Combine(Path.GetDirectoryName(settingsService.SettingsFilePath)!, "themes");
    }

    public IReadOnlyList<CustomThemeInfo> Themes => _settingsService.Settings.CustomThemes;

    public string? ActiveThemeId => _settingsService.Settings.ActiveCustomThemeId;

    /// <summary>Writes the default CSS template to a user-chosen destination — the starting point
    /// advanced users edit before importing.</summary>
    public static void ExportDefaultTemplate(string destinationPath) =>
        File.WriteAllText(destinationPath, ThemeCssParser.BuildTemplate());

    /// <summary>Builds a new theme with the default dark palette and persists it. Returns the in-memory
    /// representation so the editor can open it immediately.</summary>
    public CustomTheme Create(string name)
    {
        var theme = new CustomTheme
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? "Untitled" : name.Trim(),
            Colors = ThemeCatalog.All.ToDictionary(d => d.ResourceKey, d => d.DefaultHex, StringComparer.Ordinal)
        };
        Save(theme);
        _settingsService.Settings.CustomThemes.Add(new CustomThemeInfo { Id = theme.Id, Name = theme.Name });
        _settingsService.Save();
        return theme;
    }

    /// <summary>Loads the full color dictionary for a persisted theme. Returns <c>null</c> if the theme
    /// is unknown or its CSS file cannot be read/parsed.</summary>
    public CustomTheme? Load(string themeId)
    {
        var info = _settingsService.Settings.CustomThemes.FirstOrDefault(t => t.Id == themeId);
        if (info is null) return null;

        var path = Path.Combine(_themesDirectory, $"{themeId}.css");
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var result = ThemeCssParser.Parse(text);
        if (!result.Success) return null;

        return new CustomTheme
        {
            Id = info.Id,
            Name = info.Name,
            Colors = result.Colors.ToDictionary(kvp => kvp.Key, kvp => HexColorUtility.ColorToHex(kvp.Value), StringComparer.Ordinal)
        };
    }

    /// <summary>Persists a theme's metadata and its CSS file. Renames are reflected in
    /// <see cref="AppSettings.CustomThemes"/>. Does not activate the theme automatically.</summary>
    public void Save(CustomTheme theme)
    {
        Directory.CreateDirectory(_themesDirectory);
        var path = Path.Combine(_themesDirectory, $"{theme.Id}.css");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("/* ============================================================");
        sb.AppendLine("   Enki — tema personalizado");
        sb.AppendLine("   ============================================================ */");
        sb.AppendLine();
        sb.AppendLine(":root {");
        var groups = ThemeCatalog.All.GroupBy(d => d.CategoryKey).ToList();
        for (var i = 0; i < groups.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            sb.Append("  /* ").Append(groups[i].Key).AppendLine(" */");
            foreach (var definition in groups[i])
            {
                var hex = theme.Colors.TryGetValue(definition.ResourceKey, out var value)
                    ? (HexColorUtility.NormalizeHex(value) ?? definition.DefaultHex)
                    : definition.DefaultHex;
                sb.Append("  --").Append(definition.CssVariable).Append(": ").Append(hex).AppendLine(";");
            }
        }
        sb.AppendLine("}");

        var tempPath = $"{path}.tmp-{Guid.NewGuid():N}";
        File.WriteAllText(tempPath, sb.ToString());
        File.Move(tempPath, path, overwrite: true);

        var info = _settingsService.Settings.CustomThemes.FirstOrDefault(t => t.Id == theme.Id);
        if (info is not null) info.Name = theme.Name;
        _settingsService.Save();
    }

    /// <summary>Removes a theme's file and registration, deactivating it first when necessary.</summary>
    public void Delete(string themeId)
    {
        if (_settingsService.Settings.ActiveCustomThemeId == themeId) Deactivate();

        var info = _settingsService.Settings.CustomThemes.FirstOrDefault(t => t.Id == themeId);
        if (info is null) return;

        try
        {
            var path = Path.Combine(_themesDirectory, $"{themeId}.css");
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — still drop the registration below so a locked/already-gone file never leaves a
            // theme permanently stuck in the list with no way to remove it.
        }

        _settingsService.Settings.CustomThemes.Remove(info);
        _settingsService.Save();
    }

    /// <summary>Renames an existing theme.</summary>
    public void Rename(string themeId, string newName)
    {
        var info = _settingsService.Settings.CustomThemes.FirstOrDefault(t => t.Id == themeId);
        if (info is null) return;

        info.Name = string.IsNullOrWhiteSpace(newName) ? info.Name : newName.Trim();
        _settingsService.Save();
    }

    /// <summary>Applies a theme's colors live and persists it as the active one. Returns
    /// <c>false</c> without changing any state if the theme id is unknown or its stored file can no
    /// longer be read/parsed.</summary>
    public bool Activate(string themeId)
    {
        var theme = Load(themeId);
        if (theme is null) return false;

        if (!TryParseColors(theme.Colors, out var parsed)) return false;
        ApplyToResources(parsed);
        _settingsService.Settings.ActiveCustomThemeId = themeId;
        _settingsService.Save();
        return true;
    }

    /// <summary>Applies a raw color dictionary to the application resources for live preview, without
    /// persisting the active theme. Used by the theme editor while the user is dragging sliders.</summary>
    public static void Preview(IReadOnlyDictionary<string, string> colors)
    {
        if (!TryParseColors(colors, out var parsed)) return;
        ApplyToResources(parsed);
    }

    private static bool TryParseColors(IReadOnlyDictionary<string, string> colors, out Dictionary<string, Color> parsed)
    {
        parsed = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var kvp in colors)
        {
            var normalized = HexColorUtility.NormalizeHex(kvp.Value);
            if (normalized is null) return false;
            parsed[kvp.Key] = Color.Parse(normalized);
        }
        return true;
    }

    /// <summary>Applies a raw color dictionary to the application resources for live preview, without
    /// persisting the active theme.</summary>
    public static void Preview(IReadOnlyDictionary<string, Color> colors)
    {
        ApplyToResources(colors);
    }

    /// <summary>Reverts to the built-in Dark/Light theme (<see cref="AppSettings.Theme"/>).</summary>
    public void Deactivate()
    {
        ThemeRegistry.Clear();
        _settingsService.Settings.ActiveCustomThemeId = null;
        _settingsService.Save();
    }

    /// <summary>Called once at composition root startup (after <see cref="SettingsService"/> loads, before
    /// any window/view reads a color resource) to re-apply whichever theme was active last session. Clears
    /// a stale/corrupt <see cref="AppSettings.ActiveCustomThemeId"/> instead of failing — a custom theme
    /// must never be able to block Enki from starting.</summary>
    public void ApplyActiveOnStartup()
    {
        var themeId = _settingsService.Settings.ActiveCustomThemeId;
        if (themeId is null) return;

        if (!Activate(themeId))
        {
            _settingsService.Settings.ActiveCustomThemeId = null;
            _settingsService.Save();
        }
    }

    /// <summary>Writes a theme's current colors to a user-chosen <c>.css</c> file.</summary>
    public void Export(string themeId, string destinationPath)
    {
        var theme = Load(themeId);
        if (theme is null) throw new InvalidOperationException("Theme not found.");
        Save(theme); // ensure the CSS file on disk is up to date
        var sourcePath = Path.Combine(_themesDirectory, $"{themeId}.css");
        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    public sealed record ImportResult(bool Success, string? ThemeId, IReadOnlyList<string> Errors)
    {
        public static ImportResult Failed(params string[] errors) => new(false, null, errors);
    }

    /// <summary>Validates a user-picked CSS file and, on success, imports it as a new theme.
    /// Unlike the original flow, importing does not activate the theme automatically.</summary>
    public ImportResult Import(string sourceCssPath)
    {
        var loc = LocalizationService.Instance;
        string text;
        try
        {
            text = File.ReadAllText(sourceCssPath);
        }
        catch (UnauthorizedAccessException)
        {
            return ImportResult.Failed(loc["Common_Error_AccessDenied"]);
        }
        catch (IOException)
        {
            return ImportResult.Failed(loc["Common_Error_IOFailure"]);
        }

        var parseResult = ThemeCssParser.Parse(text);
        if (!parseResult.Success)
            return new ImportResult(false, null, parseResult.Errors.Select(e => DescribeParseError(e, loc)).ToList());

        var id = Guid.NewGuid().ToString("N");
        var name = Path.GetFileNameWithoutExtension(sourceCssPath);

        Directory.CreateDirectory(_themesDirectory);
        var destinationPath = Path.Combine(_themesDirectory, $"{id}.css");
        var tempPath = $"{destinationPath}.tmp-{Guid.NewGuid():N}";
        File.WriteAllText(tempPath, text);
        File.Move(tempPath, destinationPath, overwrite: true);

        _settingsService.Settings.CustomThemes.Add(new CustomThemeInfo { Id = id, Name = name });
        _settingsService.Save();

        return new ImportResult(true, id, []);
    }

    private static void ApplyToResources(IReadOnlyDictionary<string, Color> colors)
    {
        ThemeRegistry.Apply(colors);
        ThemeRegistry.ApplySystemAccent(colors);
    }

    private static string DescribeParseError(ThemeParseError error, LocalizationService loc) => error.Kind switch
    {
        ThemeParseErrorKind.MissingRootBlock => loc["Settings_Appearance_CustomTheme_Error_NoRootBlock"],
        ThemeParseErrorKind.MissingVariables =>
            string.Format(CultureInfo.CurrentCulture, loc["Settings_Appearance_CustomTheme_Error_MissingVariables"], error.Detail),
        ThemeParseErrorKind.UnexpectedVariables =>
            string.Format(CultureInfo.CurrentCulture, loc["Settings_Appearance_CustomTheme_Error_UnexpectedVariables"], error.Detail),
        ThemeParseErrorKind.InvalidColorValue =>
            string.Format(CultureInfo.CurrentCulture, loc["Settings_Appearance_CustomTheme_Error_InvalidColorValue"], error.Detail),
        _ => error.Detail,
    };
}
