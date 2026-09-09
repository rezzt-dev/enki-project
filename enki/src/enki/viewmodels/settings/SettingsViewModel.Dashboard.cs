using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Ajustes › Dashboard (propuesta 27): la lista ordenable/ocultable de secciones del Dashboard, su
/// resumen de visibilidad y la selección de plantilla de organización (Clásica, Pirámide invertida, …) —
/// split out of <c>SettingsViewModel.cs</c> to keep this whole page's build/reorder/toggle/reset logic
/// together. Still the very same <see cref="SettingsViewModel"/> class — partial types share every
/// field/property regardless of which file declares them — so no behavior change.</summary>
public sealed partial class SettingsViewModel
{
    /// <summary>Ajustes › Dashboard (propuesta 27): one row per configurable dashboard section, ordered and
    /// filtered by the per-vault settings.</summary>
    public ObservableCollection<DashboardSectionConfigViewModel> DashboardSections { get; } = [];

    /// <summary>Default order of dashboard sections, used when <see cref="Models.VaultSettings.DashboardSectionOrder"/>
    /// is empty or when a reset is requested.</summary>
    private static readonly string[] DefaultDashboardSectionOrder =
    [
        "QuickAccess", "Today", "Vault", "Activity", "Sessions", "BoardTasks", "RecentNotes", "Habits"
    ];

    /// <summary>Icon shown per dashboard section in the Ajustes › Dashboard list and its live preview —
    /// the exact same glyph <see cref="Views.DashboardView"/> renders in that section's own header, so
    /// the two surfaces read as one system rather than two independent icon choices.</summary>
    private static readonly Dictionary<string, MaterialIconKind> DashboardSectionIcons = new()
    {
        ["QuickAccess"] = MaterialIconKind.ViewGridOutline,
        ["Today"] = MaterialIconKind.CalendarCheckOutline,
        ["Vault"] = MaterialIconKind.Harddisk,
        ["Activity"] = MaterialIconKind.BellRingOutline,
        ["Sessions"] = MaterialIconKind.SitemapOutline,
        ["BoardTasks"] = MaterialIconKind.ViewColumnOutline,
        ["RecentNotes"] = MaterialIconKind.NoteTextOutline,
        ["Habits"] = MaterialIconKind.ClipboardCheckOutline
    };

    /// <summary>Total number of configurable dashboard sections — the denominator in
    /// <see cref="DashboardVisibilitySummary"/> ("N of 7 visible").</summary>
    private static int DashboardSectionCount => DefaultDashboardSectionOrder.Length;

    /// <summary>"N of 7 visible" summary shown above the section list — recomputed by
    /// <see cref="RefreshDashboardSummary"/> after every build/reorder/toggle instead of a computed
    /// property, since <see cref="DashboardSections"/> mutating in place doesn't raise property-changed
    /// for anything reading its aggregate count.</summary>
    [ObservableProperty]
    private string _dashboardVisibilitySummary = string.Empty;

    /// <summary>One of <see cref="Services.DashboardLayoutEngine.AllStyles"/> — which named arrangement
    /// (Clásica, Pirámide invertida, …) the Dashboard and this page's own preview pour the visible
    /// sections into. See <see cref="SelectDashboardLayoutStyle"/>.</summary>
    [ObservableProperty]
    private string _dashboardLayoutStyle = Services.DashboardLayoutEngine.Classic;

    /// <summary>True cuando la organización elegida es "Personalizada" — muestra el editor de rejilla y
    /// esconde la vista previa de plantilla (el editor ya es la vista previa, y además editable).</summary>
    public bool IsCustomLayout => DashboardLayoutStyle == Services.DashboardLayoutEngine.Custom;

    /// <summary>Columnas que ocupa la tarjeta de la lista de secciones dentro de la rejilla de esta
    /// página. Con una plantilla comparte fila con la vista previa (1 columna); en Personalizado esa
    /// vista previa no se muestra y, sin esto, la lista se quedaría en su columna estrecha con media
    /// página en blanco al lado. Un entero en el ViewModel en vez de un converter bool→int nuevo: es la
    /// única vista de la app que necesita la conversión.</summary>
    public int DashboardSectionListColumnSpan => IsCustomLayout ? 3 : 1;

    /// <summary>Columnas de la rejilla personalizada (1–<see cref="Services.DashboardLayoutEngine.MaxGridDimension"/>),
    /// enlazada al configurador del editor. Cambiarla no descoloca nada: el layout guardado se vuelve a
    /// normalizar contra la rejilla nueva (<see cref="NormalizeAndPersistCustomLayout"/>), así que las
    /// fichas que se salían se recolocan en vez de desaparecer.</summary>
    [ObservableProperty]
    private int _dashboardGridColumns = Services.DashboardLayoutEngine.DefaultColumns;

    /// <summary>Filas de la rejilla personalizada. Es un mínimo, no un tope: el Dashboard pinta las filas
    /// que hagan falta si alguna ficha desborda por abajo — subir este número lo que hace es reservar
    /// filas vacías donde poder soltar fichas.</summary>
    [ObservableProperty]
    private int _dashboardGridRows = Services.DashboardLayoutEngine.DefaultRows;

    partial void OnDashboardLayoutStyleChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomLayout));
        OnPropertyChanged(nameof(DashboardSectionListColumnSpan));
        if (value == Services.DashboardLayoutEngine.Custom)
            ReapplyGridDimensions();
    }

    partial void OnDashboardGridColumnsChanged(int value)
    {
        var clamped = Services.DashboardLayoutEngine.ClampDimension(value);
        if (clamped != value) { DashboardGridColumns = clamped; return; }
        _vaultSettingsService.Settings.DashboardGridColumns = value;
        _vaultSettingsService.Save();
        NormalizeAndPersistCustomLayout();
        RefreshDashboardSummary();
    }

    partial void OnDashboardGridRowsChanged(int value)
    {
        var clamped = Services.DashboardLayoutEngine.ClampDimension(value);
        if (clamped != value) { DashboardGridRows = clamped; return; }
        _vaultSettingsService.Settings.DashboardGridRows = value;
        _vaultSettingsService.Save();
        NormalizeAndPersistCustomLayout();
        RefreshDashboardSummary();
    }

    /// <summary>Copia las dimensiones guardadas en el vault (ya recortadas) a las propiedades del
    /// ViewModel. Se llama al pasar a Personalizado y al reconstruir la página.</summary>
    private void ReapplyGridDimensions()
    {
        DashboardGridColumns = Services.DashboardLayoutEngine.ClampDimension(_vaultSettingsService.Settings.DashboardGridColumns);
        DashboardGridRows = Services.DashboardLayoutEngine.ClampDimension(_vaultSettingsService.Settings.DashboardGridRows);
    }

    /// <summary>Ajustes › Dashboard › Organización: one selectable card per layout template, rebuilt by
    /// <see cref="BuildDashboardLayoutOptions"/> — same "just rebuild it, it's cheap" approach as
    /// <see cref="BuildDashboardSections"/> rather than a separate relabel-only path.</summary>
    public ObservableCollection<DashboardLayoutOptionViewModel> DashboardLayoutOptions { get; } = [];

    [ObservableProperty]
    private bool _dashboardShowScrollbar;

    partial void OnDashboardShowScrollbarChanged(bool value)
    {
        _settingsService.Settings.DashboardShowScrollbar = value;
        _settingsService.Save();
        _onDashboardShowScrollbarChanged(value);
    }

    /// <summary>Builds <see cref="DashboardSections"/> from the per-vault settings. Missing sections are
    /// appended in their default order and unknown/stale keys are ignored.</summary>
    private void BuildDashboardSections()
    {
        var settings = _vaultSettingsService.Settings;
        var order = settings.DashboardSectionOrder;
        var hidden = settings.DashboardHiddenSections;

        if (order is null || order.Count == 0)
            order = DefaultDashboardSectionOrder.ToList();

        var result = order
            .Concat(DefaultDashboardSectionOrder.Except(order))
            .Where(DefaultDashboardSectionOrder.Contains)
            .ToList();

        DashboardSections.Clear();
        foreach (var key in result)
        {
            DashboardSections.Add(new DashboardSectionConfigViewModel
            {
                Key = key,
                Title = LocalizationService.Instance[$"DashboardSection_{key}"],
                Description = LocalizationService.Instance[$"DashboardSection_{key}_Description"],
                Icon = DashboardSectionIcons.TryGetValue(key, out var icon) ? icon : MaterialIconKind.ViewDashboardOutline,
                IsVisible = hidden is null || !hidden.Contains(key)
            });
        }

        RefreshDashboardSummary();
    }

    /// <summary>Recomputes <see cref="DashboardVisibilitySummary"/>, every row's <c>Position</c>/
    /// <c>IsFirst</c>/<c>IsLast</c>, and the preview's grid geometry (<c>GridRow</c>/<c>GridColumn</c>/…,
    /// via <see cref="Services.DashboardLayoutEngine"/>) — called after every build, reorder, toggle,
    /// reset and layout-style change so the "N of 7 visible" header, the "01/02/…" index badges, the
    /// up/down button states and the live preview's arrangement never fall out of sync with
    /// <see cref="DashboardSections"/>.</summary>
    private void RefreshDashboardSummary()
    {
        for (var i = 0; i < DashboardSections.Count; i++)
        {
            DashboardSections[i].Position = i + 1;
            DashboardSections[i].IsFirst = i == 0;
            DashboardSections[i].IsLast = i == DashboardSections.Count - 1;
        }

        var visibleKeys = DashboardSections.Where(s => s.IsVisible).Select(s => s.Key).ToList();
        var customLayout = _vaultSettingsService.Settings.DashboardCustomLayout;
        var gridCols = Math.Max(1, Math.Min(_vaultSettingsService.Settings.DashboardGridColumns, Services.DashboardLayoutEngine.MaxGridDimension));
        var gridRows = Math.Max(1, Math.Min(_vaultSettingsService.Settings.DashboardGridRows, Services.DashboardLayoutEngine.MaxGridDimension));
        var slots = Services.DashboardLayoutEngine.Compute(visibleKeys, DashboardLayoutStyle, customLayout, gridRows, gridCols);
        foreach (var section in DashboardSections)
        {
            if (!slots.TryGetValue(section.Key, out var slot)) continue;
            section.GridRow = slot.Row;
            section.GridColumn = slot.Column;
            section.GridRowSpan = slot.RowSpan;
            section.GridColumnSpan = slot.ColumnSpan;
        }

        var visible = visibleKeys.Count;
        DashboardVisibilitySummary = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.Instance["Settings_Dashboard_VisibleCount"],
            visible, DashboardSectionCount);

        RefreshDashboardGridEditor();
    }

    /// <summary>Persists the current dashboard section order and visibility to the vault settings.</summary>
    private void SaveDashboardLayout()
    {
        _vaultSettingsService.Settings.DashboardSectionOrder = DashboardSections.Select(s => s.Key).ToList();
        _vaultSettingsService.Settings.DashboardHiddenSections = DashboardSections.Where(s => !s.IsVisible).Select(s => s.Key).ToList();
        _vaultSettingsService.Save();
        RefreshDashboardSummary();
    }

    /// <summary>Moves <paramref name="sourceKey"/> to the position of <paramref name="targetKey"/>,
    /// inserting it before the target. Used by the drag-and-drop reordering in Ajustes › Dashboard.</summary>
    public void MoveDashboardSection(string sourceKey, string targetKey)
    {
        var source = DashboardSections.FirstOrDefault(s => s.Key == sourceKey);
        var target = DashboardSections.FirstOrDefault(s => s.Key == targetKey);
        if (source is null || target is null || ReferenceEquals(source, target)) return;

        var oldIndex = DashboardSections.IndexOf(source);
        var newIndex = DashboardSections.IndexOf(target);
        DashboardSections.RemoveAt(oldIndex);
        if (newIndex > oldIndex) newIndex--;
        DashboardSections.Insert(newIndex, source);
        SaveDashboardLayout();
    }

    /// <summary>Swaps a section one slot earlier in the list — the keyboard/mouse-accessible counterpart
    /// to dragging the row's handle, exposed as its own button per row.</summary>
    [RelayCommand]
    private void MoveDashboardSectionUp(string key)
    {
        var item = DashboardSections.FirstOrDefault(s => s.Key == key);
        if (item is null) return;

        var index = DashboardSections.IndexOf(item);
        if (index <= 0) return;

        DashboardSections.Move(index, index - 1);
        SaveDashboardLayout();
    }

    /// <summary>Swaps a section one slot later in the list — see <see cref="MoveDashboardSectionUp"/>.</summary>
    [RelayCommand]
    private void MoveDashboardSectionDown(string key)
    {
        var item = DashboardSections.FirstOrDefault(s => s.Key == key);
        if (item is null) return;

        var index = DashboardSections.IndexOf(item);
        if (index < 0 || index >= DashboardSections.Count - 1) return;

        DashboardSections.Move(index, index + 1);
        SaveDashboardLayout();
    }

    /// <summary>Toggles visibility of a dashboard section, preventing the user from hiding the last
    /// visible one.</summary>
    [RelayCommand]
    private void ToggleDashboardSectionVisibility(string key)
    {
        var item = DashboardSections.FirstOrDefault(s => s.Key == key);
        if (item is null) return;

        if (item.IsVisible && DashboardSections.Count(s => s.IsVisible) <= 1)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Settings_Dashboard_CannotHideLast_Title"),
                LocalizedTextFormatter.Format("Settings_Dashboard_CannotHideLast_Message"),
                Models.NotificationSeverity.Warning);
            return;
        }

        item.IsVisible = !item.IsVisible;
        SaveDashboardLayout();
    }

    /// <summary>Restores the default section order with every section visible — the one-click escape
    /// hatch once dragging/hiding has left the layout in a state the user no longer wants.</summary>
    [RelayCommand]
    private void ResetDashboardLayout()
    {
        _vaultSettingsService.Settings.DashboardSectionOrder = DefaultDashboardSectionOrder.ToList();
        _vaultSettingsService.Settings.DashboardHiddenSections = [];
        _vaultSettingsService.Settings.DashboardLayoutStyle = Services.DashboardLayoutEngine.Classic;
        _vaultSettingsService.Settings.DashboardCustomLayout = [];
        _vaultSettingsService.Settings.DashboardGridRows = Services.DashboardLayoutEngine.DefaultRows;
        _vaultSettingsService.Settings.DashboardGridColumns = Services.DashboardLayoutEngine.DefaultColumns;
        _vaultSettingsService.Save();

        // El vault volvía a Classic pero la propiedad del ViewModel (y con ella IsCustomLayout, la
        // geometría de la vista previa y la tarjeta marcada) se quedaba en la organización anterior:
        // restablecer dejaba la página mintiendo hasta la siguiente reconstrucción.
        DashboardLayoutStyle = Services.DashboardLayoutEngine.Classic;
        BuildDashboardLayoutOptions();
        BuildDashboardSections();
    }

    /// <summary>Builds <see cref="DashboardLayoutOptions"/> — one card per
    /// <see cref="Services.DashboardLayoutEngine.AllStyles"/>, each with a thumbnail diagram computed
    /// against the full default section set so every option shows its true shape even if the vault
    /// currently has some sections hidden.</summary>
    private void BuildDashboardLayoutOptions()
    {
        var currentStyle = _vaultSettingsService.Settings.DashboardLayoutStyle;
        if (string.IsNullOrEmpty(currentStyle))
            currentStyle = Services.DashboardLayoutEngine.Classic;

        // La propiedad solo se movía al pulsar una tarjeta, así que al abrir Ajustes con una
        // organización ya guardada en el vault la tarjeta salía marcada pero la vista previa (y el
        // editor, que depende de IsCustomLayout) seguían dibujando Classic.
        DashboardLayoutStyle = currentStyle;
        ReapplyGridDimensions();

        DashboardLayoutOptions.Clear();
        foreach (var style in Services.DashboardLayoutEngine.AllStyles)
        {
            ObservableCollection<DashboardLayoutBlockViewModel> blocks;

            if (style == Services.DashboardLayoutEngine.Custom)
            {
                blocks = [];
            }
            else
            {
                var slots = Services.DashboardLayoutEngine.Compute(DefaultDashboardSectionOrder, style);
                blocks = new ObservableCollection<DashboardLayoutBlockViewModel>(
                    DefaultDashboardSectionOrder
                        .Where(slots.ContainsKey)
                        .Select(key =>
                        {
                            var slot = slots[key];
                            return new DashboardLayoutBlockViewModel
                            {
                                GridRow = slot.Row,
                                GridColumn = slot.Column,
                                GridRowSpan = slot.RowSpan,
                                GridColumnSpan = slot.ColumnSpan,
                                IsWide = Services.DashboardLayoutEngine.IsWideSection(key)
                            };
                        }));
            }

            DashboardLayoutOptions.Add(new DashboardLayoutOptionViewModel
            {
                Key = style,
                Title = LocalizationService.Instance[$"DashboardLayout_{style}_Title"],
                Description = LocalizationService.Instance[$"DashboardLayout_{style}_Description"],
                Blocks = blocks,
                IsSelected = style == currentStyle
            });
        }
    }

    /// <summary>Switches which named arrangement (Clásica, Pirámide invertida, …) the Dashboard and this
    /// page's preview pour the visible sections into. The section order/visibility list is untouched, so
    /// switching layouts never requires reconfiguring which sections are shown. When switching to Custom,
    /// grid dimensions are re-read from the vault (already normalized by VaultSettingsService.Load).</summary>
    [RelayCommand]
    private void SelectDashboardLayoutStyle(string style)
    {
        if (style == DashboardLayoutStyle) return;

        // Estrenar Personalizado arranca con la geometría de la plantilla que estuviera puesta, no con un
        // lienzo en blanco. Pero solo la primera vez: si ya hay una rejilla hecha a mano guardada, probar
        // una plantilla y volver a Personalizado la recupera tal cual — perder el trabajo por ir a mirar
        // otra organización sería la peor sorpresa posible. Para rehacerla a partir de la plantilla que se
        // está viendo está el botón "personalizar esta organización" (CustomizeDashboardLayout).
        var savedGrid = _vaultSettingsService.Settings.DashboardCustomLayout;
        if (style == Services.DashboardLayoutEngine.Custom && savedGrid is null or { Count: 0 })
            SeedCustomLayoutFrom(DashboardLayoutStyle);

        DashboardLayoutStyle = style;
        _vaultSettingsService.Settings.DashboardLayoutStyle = style;
        _vaultSettingsService.Save();

        foreach (var option in DashboardLayoutOptions)
            option.IsSelected = option.Key == style;

        RefreshDashboardSummary();
    }
}
