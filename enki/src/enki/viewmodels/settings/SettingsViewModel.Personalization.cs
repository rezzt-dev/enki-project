using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class SettingsViewModel
{
    private string? _pendingPersonalizationJson;
    private string? _personalizationVault;
    private readonly Action? _onPersonalizationImported;

    [ObservableProperty]
    private bool _isPersonalizationPreviewOpen;

    [ObservableProperty]
    private string _personalizationPreviewText = string.Empty;

    private PersonalizationProfileService PersonalizationProfiles =>
        new(_settingsService, _vaultSettingsService, _customThemeService);

    [RelayCommand]
    private async Task ExportPersonalization()
    {
        var storage = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef)?.StorageProvider : null;
        if (storage is null) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LocalizationService.Instance["Settings_Profile_Export"],
            SuggestedFileName = "enki-profile.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType(LocalizationService.Instance["Settings_Profile_Title"]) { Patterns = ["*.json"] }]
        });
        if (file?.TryGetLocalPath() is not { } destination) return;
        try
        {
            var json = PersonalizationProfiles.Export();
            File.WriteAllText(destination, json);
            ProfileNotification("Settings_Profile_Exported", NotificationSeverity.Success);
        }
        catch (Exception ex) when (IsProfileFailure(ex)) { ReportProfileFailure(ex); }
    }

    [RelayCommand]
    private async Task ImportPersonalization()
    {
        CancelPersonalizationImport();
        var storage = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef)?.StorageProvider : null;
        if (storage is null) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance["Settings_Profile_Import"], AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(LocalizationService.Instance["Settings_Profile_Title"]) { Patterns = ["*.json"] }]
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } source) return;
        try { PreparePersonalizationImport(PersonalizationProfileService.Read(source)); }
        catch (Exception ex) when (IsProfileFailure(ex)) { ReportProfileFailure(ex); }
    }

    /// <summary>Prepara una instantánea sin modificar ajustes, temas ni archivos del vault.</summary>
    internal void PreparePersonalizationImport(string json)
    {
        CancelPersonalizationImport();
        var canonical = PersonalizationProfileService.Validate(json);
        var root = JsonNode.Parse(canonical)!.AsObject();
        var app = root["Preferences"]!.Deserialize<AppSettings>()!;
        var panel = root["Dashboard"]!.Deserialize<VaultSettings>()!;
        var themes = root["Themes"]!.Deserialize<CustomTheme[]>()!;
        var loc = LocalizationService.Instance;
        var text = new StringBuilder();
        void Line(string key, params object[] args) => text.AppendLine(string.Format(CultureInfo.CurrentCulture, loc[key], args));
        var activeName = themes.FirstOrDefault(t => t.Id == app.ActiveCustomThemeId)?.Name;
        if (activeName is null && app.ActiveCustomThemeId is { } activeId && BuiltInThemeCatalog.TryGet(activeId, out var builtIn)) activeName = builtIn.Name;
        activeName ??= loc[app.Theme == "Light" ? "Settings_Profile_Light" : "Settings_Profile_Dark"];
        Line("Settings_Profile_ThemeSummary", activeName, themes.Length);
        foreach (var theme in themes) text.AppendLine("  • " + theme.Name);
        Line("Settings_Profile_FontSummary", app.InterfaceFontFamily ?? loc["Settings_Profile_Bundled"],
            app.EditorFontFamily ?? loc["Settings_Profile_Bundled"], app.CodeFontFamily ?? loc["Settings_Profile_Bundled"], panel.UiZoomPercent);
        text.AppendLine();
        Line("Settings_Profile_ShortcutSummary", app.ShortcutOverrides.Count);
        foreach (var (id, gesture) in app.ShortcutOverrides)
            text.AppendLine(CultureInfo.CurrentCulture, $"  • {loc[ShortcutCatalog.All.First(d => d.Id == id).LabelKey]}: {gesture}");
        text.AppendLine();
        string SidebarLabel(string key) => SidebarSectionCatalog.CustomName(app.SidebarSectionNames, key) ?? loc[SidebarSectionCatalog.Meta[key].LabelKey];
        Line("Settings_Profile_MainSummary", string.Join(" → ", app.SidebarMainSectionOrder.Select(SidebarLabel)));
        Line("Settings_Profile_BottomSummary", string.Join(" → ", app.SidebarBottomSectionOrder.Select(SidebarLabel)));
        var preferences = root["Preferences"]!.AsObject();
        var hidden = SidebarSectionCatalog.AllKeys.Where(k =>
        {
            var property = k switch
            {
                "Reminders" => nameof(AppSettings.RemindersSidebarEnabled),
                "Notifications" => nameof(AppSettings.NotificationsSidebarEnabled),
                _ => k + "PanelEnabled"
            };
            return preferences[property] is JsonValue value && !value.GetValue<bool>();
        });
        Line("Settings_Profile_HiddenSummary", string.Join(", ", hidden.Select(SidebarLabel)));
        text.AppendLine();
        Line("Settings_Profile_PanelSummary", loc[$"DashboardLayout_{panel.DashboardLayoutStyle}_Title"], panel.DashboardGridRows, panel.DashboardGridColumns);
        var order = panel.DashboardSectionOrder.Concat(PersonalizationProfileService.DashboardSections).Distinct().ToArray();
        var slots = DashboardLayoutEngine.Compute(order.Where(k => !panel.DashboardHiddenSections.Contains(k)).ToArray(), panel.DashboardLayoutStyle,
            panel.DashboardCustomLayout, panel.DashboardGridRows, panel.DashboardGridColumns);
        foreach (var key in order)
        {
            var title = loc["DashboardSection_" + key];
            if (slots.TryGetValue(key, out var slot))
                Line("Settings_Profile_TileSummary", title, slot.Row + 1, slot.Column + 1, slot.RowSpan, slot.ColumnSpan);
            else Line("Settings_Profile_HiddenTile", title);
        }
        PersonalizationPreviewText = text.ToString();
        _pendingPersonalizationJson = canonical;
        _personalizationVault = _vaultService.VaultPath;
        IsPersonalizationPreviewOpen = true;
    }

    [RelayCommand]
    private void CancelPersonalizationImport()
    {
        IsPersonalizationPreviewOpen = false;
        _pendingPersonalizationJson = null;
        _personalizationVault = null;
        PersonalizationPreviewText = string.Empty;
    }

    [RelayCommand]
    private void ConfirmPersonalizationImport()
    {
        if (_pendingPersonalizationJson is not { } json) return;
        try
        {
            if (!_vaultService.IsVaultConfigured || _personalizationVault != _vaultService.VaultPath)
                throw new InvalidOperationException("El vault ha cambiado durante la vista previa.");
            PersonalizationProfiles.Apply(json);
            CancelPersonalizationImport();
            ShortcutRegistry.Reload(_settingsService.Settings.ShortcutOverrides);
            if (_onPersonalizationImported is { } refresh) refresh();
            else { _customThemeService.ApplyActiveOnStartup(); ReloadForVault(); }
            RefreshAllShortcutRows();
            _onUiZoomChanged(_vaultSettingsService.Settings.UiZoomPercent);
            ProfileNotification("Settings_Profile_Imported", NotificationSeverity.Success);
        }
        catch (Exception ex) when (IsProfileFailure(ex)) { ReportProfileFailure(ex); }
    }

    private static bool IsProfileFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or TimeoutException;

    private void ReportProfileFailure(Exception ex)
    {
        LogService.Warning("No se pudo procesar el perfil de personalización", ex);
        ProfileNotification(ex is InvalidDataException or JsonException ? "Settings_Profile_Invalid" : "Settings_Profile_Failed", NotificationSeverity.Error);
    }

    private void ProfileNotification(string messageKey, NotificationSeverity severity) =>
        _notificationService.PushLocalized(LocalizedTextFormatter.Format("Settings_Profile_Title"), LocalizedTextFormatter.Format(messageKey), severity);
}
