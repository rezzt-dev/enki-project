using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Input;
using enki.Models;

namespace enki.Services;

/// <summary>Perfil portátil BAJ-02: solo preferencias visuales, atajos, barra lateral y panel.
/// La lista permitida evita que nuevas propiedades de AppSettings filtren datos por accidente.</summary>
public sealed class PersonalizationProfileService(
    SettingsService settings, VaultSettingsService vaultSettings, CustomThemeService themes)
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    private const string Format = "enki-personalization";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    private static readonly string[] HeaderKeys = ["Format", "Version", "Preferences", "Dashboard", "Themes"];
    private static readonly string[] PreferenceKeys =
    [
        nameof(AppSettings.Theme), nameof(AppSettings.ActiveCustomThemeId),
        nameof(AppSettings.InterfaceFontFamily), nameof(AppSettings.EditorFontFamily), nameof(AppSettings.CodeFontFamily),
        nameof(AppSettings.EditorFontSize), nameof(AppSettings.EditorColumnWidth), nameof(AppSettings.EditorColumnPosition),
        nameof(AppSettings.UseSubtleAnimations), nameof(AppSettings.UseCustomTitleBar), nameof(AppSettings.ShowWindowControls),
        nameof(AppSettings.SidebarExpanded), nameof(AppSettings.VaultSwitcherEnabled),
        nameof(AppSettings.NotesPanelOnRight), nameof(AppSettings.FilesPanelOnRight),
        nameof(AppSettings.SkillsPanelOnRight), nameof(AppSettings.AssistantPanelOnRight),
        nameof(AppSettings.DashboardPanelEnabled), nameof(AppSettings.CalendarPanelEnabled),
        nameof(AppSettings.KanbanPanelEnabled), nameof(AppSettings.NotesPanelEnabled), nameof(AppSettings.FilesPanelEnabled),
        nameof(AppSettings.SessionsPanelEnabled), nameof(AppSettings.TrashPanelEnabled), nameof(AppSettings.HabitsPanelEnabled),
        nameof(AppSettings.SkillsPanelEnabled), nameof(AppSettings.TagManagerPanelEnabled), nameof(AppSettings.StatisticsPanelEnabled),
        nameof(AppSettings.RemindersSidebarEnabled), nameof(AppSettings.NotificationsSidebarEnabled),
        nameof(AppSettings.SidebarMainSectionOrder), nameof(AppSettings.SidebarBottomSectionOrder), nameof(AppSettings.SidebarSectionNames),
        nameof(AppSettings.ShortcutOverrides), nameof(AppSettings.DashboardShowScrollbar), nameof(AppSettings.KanbanBoardOrientation),
        nameof(AppSettings.NotesViewMode), nameof(AppSettings.NotesSimpleSidebarVisible), nameof(AppSettings.NotesSimpleSidebarWidth),
        nameof(AppSettings.FilesViewMode), nameof(AppSettings.FilesTreeSidebarVisible), nameof(AppSettings.FilesTreeSidebarWidth),
        nameof(AppSettings.SkillsViewMode), nameof(AppSettings.SkillsTreeSidebarVisible), nameof(AppSettings.SkillsTreeSidebarWidth)
    ];
    private static readonly string[] DashboardKeys =
    [
        nameof(VaultSettings.UiZoomPercent), nameof(VaultSettings.DashboardSectionOrder), nameof(VaultSettings.DashboardHiddenSections),
        nameof(VaultSettings.DashboardLayoutStyle), nameof(VaultSettings.DashboardCustomLayout),
        nameof(VaultSettings.DashboardGridRows), nameof(VaultSettings.DashboardGridColumns)
    ];
    public static IReadOnlyList<string> DashboardSections { get; } =
        Array.AsReadOnly(new[] { "QuickAccess", "Today", "Vault", "Activity", "Sessions", "BoardTasks", "RecentNotes", "Habits" });

    /// <summary>Contenido completo del fichero, sin rutas, sesiones, cuentas ni contenido del vault.</summary>
    public string Export()
    {
        var profile = new Profile
        {
            Preferences = Select(JsonSerializer.SerializeToNode(settings.Settings)!.AsObject(), PreferenceKeys),
            Dashboard = Select(JsonSerializer.SerializeToNode(vaultSettings.Settings)!.AsObject(), DashboardKeys)
        };
        foreach (var info in themes.Themes)
            profile.Themes.Add(themes.Load(info.Id) ?? throw new InvalidDataException("No se pudo leer un tema del perfil."));
        return Validate(JsonSerializer.Serialize(profile, JsonOptions));
    }

    /// <summary>Lee con límite de tamaño antes de reservar memoria para el fichero completo.</summary>
    public static string Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("El perfil es demasiado grande.");
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxFileBytes + 1];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count > MaxFileBytes) throw new InvalidDataException("El perfil es demasiado grande.");
        return Validate(new string(buffer, 0, count));
    }

    /// <summary>Valida sin escribir. Devuelve una instantánea canónica para mostrar y confirmar la misma entrada.</summary>
    public static string Validate(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
            throw new InvalidDataException("El perfil es demasiado grande.");
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null || root.Count != 5 || HeaderKeys.Any(k => !root.ContainsKey(k)))
                throw new InvalidDataException("Cabecera del perfil incompleta.");
            var profile = JsonSerializer.Deserialize<Profile>(json, JsonOptions)
                ?? throw new InvalidDataException("Perfil vacío.");
            if (profile.Format != Format || profile.Version != 1 || profile.Preferences is null || profile.Dashboard is null || profile.Themes is null)
                throw new InvalidDataException("Formato o versión de perfil no compatible.");
            RequireKeys(profile.Preferences, PreferenceKeys);
            RequireKeys(profile.Dashboard, DashboardKeys);
            var app = profile.Preferences.Deserialize<AppSettings>()!;
            var panel = profile.Dashboard.Deserialize<VaultSettings>()!;
            app.Normalize();
            panel.Normalize();
            if (app.ShortcutOverrides is null || app.SidebarSectionNames is null ||
                app.SidebarMainSectionOrder is null || app.SidebarBottomSectionOrder is null ||
                app.Theme is not ("Dark" or "Light") || !DashboardLayoutEngine.AllStyles.Contains(panel.DashboardLayoutStyle))
                throw new InvalidDataException("Preferencias inválidas.");
            foreach (var (id, gesture) in app.ShortcutOverrides)
            {
                if (!ShortcutCatalog.All.Any(d => d.Id == id) || string.IsNullOrWhiteSpace(gesture))
                    throw new InvalidDataException("Atajo desconocido.");
                _ = KeyGesture.Parse(gesture);
            }
            // La geometría se entrega al mismo motor que utiliza la pantalla. Rechaza posiciones dañadas
            // antes de que el usuario confirme; no cambia silenciosamente el diseño de un perfil válido.
            if (panel.DashboardSectionOrder.Any(k => !DashboardSections.Contains(k)) ||
                panel.DashboardHiddenSections.Any(k => !DashboardSections.Contains(k)) ||
                panel.DashboardCustomLayout.Any(kv => !DashboardSections.Contains(kv.Key) || kv.Value is not { Length: 4 } ||
                    kv.Value[0] < 0 || kv.Value[1] < 0 || kv.Value[2] < 1 || kv.Value[3] < 1 ||
                    kv.Value[0] > DashboardLayoutEngine.MaxGridDimension + DashboardSections.Count ||
                    kv.Value[1] > panel.DashboardGridColumns - kv.Value[3] || kv.Value[2] > DashboardLayoutEngine.MaxGridDimension))
                throw new InvalidDataException("Diseño del panel inválido.");
            var themeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var theme in profile.Themes)
            {
                if (theme is null || string.IsNullOrWhiteSpace(theme.Id) || !themeIds.Add(theme.Id) || theme.IsBuiltIn ||
                    BuiltInThemeCatalog.TryGet(theme.Id, out _) || string.IsNullOrWhiteSpace(theme.Name) || theme.Name.Length > 200 ||
                    theme.Colors is null || theme.Colors.Count != ThemeCatalog.All.Count ||
                    ThemeCatalog.All.Any(d => !theme.Colors.TryGetValue(d.ResourceKey, out var color) || HexColorUtility.NormalizeHex(color) is null))
                    throw new InvalidDataException("Tema inválido.");
            }
            if (app.ActiveCustomThemeId is { } active && !themeIds.Contains(active) && !BuiltInThemeCatalog.TryGet(active, out _))
                throw new InvalidDataException("El tema activo no está incluido.");
            app.NotesSimpleSidebarWidth = Math.Clamp(app.NotesSimpleSidebarWidth, ViewModels.NotesViewModel.SimpleSidebarMinWidth, ViewModels.NotesViewModel.SimpleSidebarMaxWidth);
            app.FilesTreeSidebarWidth = Math.Clamp(app.FilesTreeSidebarWidth, ViewModels.FilesViewModel.TreeSidebarMinWidth, ViewModels.FilesViewModel.TreeSidebarMaxWidth);
            app.SkillsTreeSidebarWidth = Math.Clamp(app.SkillsTreeSidebarWidth, ViewModels.SkillsViewModel.TreeSidebarMinWidth, ViewModels.SkillsViewModel.TreeSidebarMaxWidth);
            var sidebar = ViewModels.SidebarSectionCatalog.Resolve(app.SidebarMainSectionOrder, app.SidebarBottomSectionOrder);
            app.SidebarMainSectionOrder = sidebar.Main;
            app.SidebarBottomSectionOrder = sidebar.Bottom;
            if (app.SidebarSectionNames.Keys.Any(k => !ViewModels.SidebarSectionCatalog.AllKeys.Contains(k)))
                throw new InvalidDataException("Sección desconocida.");
            app.SidebarSectionNames = app.SidebarSectionNames.ToDictionary(kv => kv.Key, kv => ViewModels.SidebarSectionCatalog.Truncate(kv.Value));
            profile.Preferences = Select(JsonSerializer.SerializeToNode(app)!.AsObject(), PreferenceKeys);
            profile.Dashboard = Select(JsonSerializer.SerializeToNode(panel)!.AsObject(), DashboardKeys);
            var canonical = JsonSerializer.Serialize(profile, JsonOptions);
            if (System.Text.Encoding.UTF8.GetByteCount(canonical) > MaxFileBytes)
                throw new InvalidDataException("El perfil es demasiado grande.");
            return canonical;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or NotSupportedException)
        {
            throw new InvalidDataException("El fichero no es un perfil de personalización válido.", ex);
        }
    }

    /// <summary>Instala los temas con identidades nuevas y confirma las preferencias de una vez.
    /// Las notas y demás datos del vault no se leen ni modifican.</summary>
    public void Apply(string validatedJson)
    {
        var profile = JsonSerializer.Deserialize<Profile>(Validate(validatedJson), JsonOptions)!;
        var created = new List<string>();
        var preferences = profile.Preferences;
        var metadata = JsonSerializer.SerializeToNode(settings.Settings.CustomThemes)!.AsArray();
        try
        {
            foreach (var theme in profile.Themes)
            {
                var oldId = theme.Id;
                theme.Id = Guid.NewGuid().ToString("N");
                var directory = Path.Combine(settings.PreferenceDirectory, "themes");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, theme.Id + ".css");
                created.Add(path);
                CustomThemeService.Export(theme, path);
                metadata.Add(JsonSerializer.SerializeToNode(new CustomThemeInfo { Id = theme.Id, Name = theme.Name }));
                if (preferences[nameof(AppSettings.ActiveCustomThemeId)]?.GetValue<string>() == oldId)
                    preferences[nameof(AppSettings.ActiveCustomThemeId)] = theme.Id;
            }
            preferences[nameof(AppSettings.CustomThemes)] = metadata;
            settings.ImportPersonalization(preferences, profile.Dashboard);
        }
        catch
        {
            foreach (var path in created)
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { LogService.Warning("No se pudo retirar un tema de una importación fallida", ex); }
            }
            throw;
        }
        vaultSettings.ReloadPersonalization();
    }

    private static JsonObject Select(JsonObject source, IEnumerable<string> keys)
    {
        var result = new JsonObject();
        foreach (var key in keys) result[key] = source[key]?.DeepClone();
        return result;
    }

    private static void RequireKeys(JsonObject source, string[] allowed)
    {
        if (source.Count != allowed.Length || allowed.Any(k => !source.ContainsKey(k)) ||
            source.Any(kv => kv.Value is null && kv.Key is not (nameof(AppSettings.ActiveCustomThemeId) or
                nameof(AppSettings.InterfaceFontFamily) or nameof(AppSettings.EditorFontFamily) or nameof(AppSettings.CodeFontFamily))))
            throw new InvalidDataException("El perfil contiene claves no permitidas o incompletas.");
    }

    private sealed class Profile
    {
        public string Format { get; set; } = PersonalizationProfileService.Format;
        public int Version { get; set; } = 1;
        public JsonObject Preferences { get; set; } = new();
        public JsonObject Dashboard { get; set; } = new();
        public List<CustomTheme> Themes { get; set; } = [];
    }
}
