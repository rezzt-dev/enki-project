using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using enki.Models;
using enki.Localization;

namespace enki.Services;

/// <summary>Lista cerrada de preferencias visuales. Nunca serializa AppSettings ni expone credenciales,
/// proveedores, privacidad o rutas. Cada escritura conserva el valor anterior si falla el guardado.</summary>
public sealed class AiSafeSettingsService
{
    private sealed record Entry(Func<object> Read, Action<JsonElement> Write, string Values);
    private readonly SettingsService _settings;
    private readonly VaultSettingsService? _vaultSettings;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public AiSafeSettingsService(SettingsService settings, VaultSettingsService? vaultSettings = null)
    {
        _settings = settings;
        _vaultSettings = vaultSettings;
        _entries.Add("theme", new(() => settings.Settings.Theme, value =>
        {
            if (settings.Settings.ActiveCustomThemeId is not null)
                throw AiToolCall.Invalid("A custom theme is active. Select a built-in theme in Settings first.");
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not ("Dark" or "Light"))
                throw AiToolCall.Invalid("Theme must be Dark or Light.");
            settings.Settings.Theme = value.GetString()!;
        }, "Dark, Light"));
        AddFlag("calendar_visible", () => settings.Settings.CalendarPanelEnabled, v => settings.Settings.CalendarPanelEnabled = v);
        AddFlag("kanban_visible", () => settings.Settings.KanbanPanelEnabled, v => settings.Settings.KanbanPanelEnabled = v);
        AddFlag("habits_visible", () => settings.Settings.HabitsPanelEnabled, v => settings.Settings.HabitsPanelEnabled = v);
        AddFlag("statistics_visible", () => settings.Settings.StatisticsPanelEnabled, v => settings.Settings.StatisticsPanelEnabled = v);
        AddFlag("files_visible", () => settings.Settings.FilesPanelEnabled, v => settings.Settings.FilesPanelEnabled = v);
        AddFlag("tags_visible", () => settings.Settings.TagManagerPanelEnabled, v => settings.Settings.TagManagerPanelEnabled = v);
        if (vaultSettings is not null)
            _entries.Add("zoom_percent", new(() => vaultSettings.Settings.UiZoomPercent, value =>
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var percent)
                    || percent < VaultSettings.MinUiZoomPercent || percent > VaultSettings.MaxUiZoomPercent || percent % 10 != 0)
                    throw AiToolCall.Invalid("Zoom must be 50–200, in steps of 10.");
                vaultSettings.Settings.UiZoomPercent = percent;
            }, "50–200, step 10"));
    }

    private void AddFlag(string name, Func<object> read, Action<bool> write) => _entries.Add(name,
        new(read, value =>
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw AiToolCall.Invalid("The value must be a boolean.");
            write(value.GetBoolean());
        }, "true, false"));

    public IReadOnlyList<object> List() => _entries.Select(e => (object)new
        { Key = e.Key, Value = e.Value.Read(), Allowed = e.Value.Values }).ToList();

    public static string Label(string key) => LocalizationService.Instance[key switch
    {
        "theme" => "Settings_Appearance_Theme_Title",
        "zoom_percent" => "Settings_Appearance_Zoom_Title",
        "calendar_visible" => "Nav_Calendar",
        "kanban_visible" => "Nav_Kanban",
        "habits_visible" => "Nav_Habits",
        "statistics_visible" => "Nav_Statistics",
        "files_visible" => "Nav_Files",
        "tags_visible" => "Nav_TagManager",
        _ => throw AiToolCall.Invalid("Unknown safe setting.")
    }];

    public static string ValueLabel(string json) => json switch
    {
        "true" => LocalizationService.Instance["Common_Yes"],
        "false" => LocalizationService.Instance["Common_No"],
        _ => json.Trim('"')
    };

    public string Read(string key) => JsonSerializer.Serialize(Find(key).Read());

    private Entry Find(string key) => _entries.TryGetValue(key, out var entry) ? entry
        : throw AiToolCall.Invalid("This setting is not in the safe allowlist. Use list_safe_settings.");

    /// <summary>Valida sin guardar ni emitir eventos. Las llamadas se hacen en el hilo de interfaz.</summary>
    public void Validate(string key, JsonElement value)
    {
        var entry = Find(key);
        var before = JsonSerializer.SerializeToElement(entry.Read());
        try { entry.Write(value); }
        finally { entry.Write(before); }
    }

    public void Write(string key, JsonElement value)
    {
        var entry = Find(key);
        var before = JsonSerializer.SerializeToElement(entry.Read());
        try
        {
            entry.Write(value);
            if (key == "zoom_percent") _vaultSettings!.Save();
            else _settings.Save();
        }
        catch
        {
            entry.Write(before);
            throw;
        }
    }
}
