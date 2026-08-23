using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Ajustes › Atajos (propuesta 29, docs/propuesta-nuevas-funcionalidades.md): la lista de
/// categorías/filas de atajos, la captura/validación/persistencia de un nuevo gesto y la detección de
/// conflictos — la captura del gesto en sí (escuchar el próximo KeyDown) vive en
/// <c>SettingsView.axaml.cs</c>, este archivo solo valida, persiste y refresca el estado una vez que el
/// code-behind ya tiene un <see cref="KeyGesture"/> concreto. Split out of <c>SettingsViewModel.cs</c> to
/// keep this whole page's logic together. Still the very same <see cref="SettingsViewModel"/> class —
/// partial types share every field/property regardless of which file declares them — so no behavior
/// change.</summary>
public sealed partial class SettingsViewModel
{
    /// <summary>Ajustes › Atajos (propuesta 29, docs/propuesta-nuevas-funcionalidades.md), one card per
    /// <c>ShortcutDefinition.CategoryKey</c> — built once in the constructor from
    /// <see cref="Services.ShortcutCatalog.All"/> and only re-labeled (not rebuilt) afterwards, same
    /// pattern as <see cref="Sections"/>.</summary>
    public ObservableCollection<ShortcutCategoryViewModel> ShortcutCategories { get; }

    private void RebuildShortcutLabels()
    {
        foreach (var category in ShortcutCategories) category.RefreshLabel();
    }

    // ===================== Ajustes › Atajos (propuesta 29, docs/propuesta-nuevas-funcionalidades.md) =====================
    // La captura del gesto en sí (escuchar el próximo KeyDown) vive en SettingsView.axaml.cs, ya que un
    // RelayCommand no puede recibir un KeyEventArgs de forma natural — esta sección solo valida, persiste
    // y refresca el estado una vez que el code-behind ya tiene un KeyGesture concreto.

    /// <summary>Marca <paramref name="row"/> como la única fila en modo captura — llamado por
    /// <c>SettingsView.axaml.cs</c> al hacer clic en el campo de gesto de un atajo.</summary>
    public void BeginCapture(ShortcutRowViewModel row)
    {
        foreach (var category in ShortcutCategories)
            foreach (var item in category.Items)
                item.IsCapturing = ReferenceEquals(item, row);

        row.ConflictMessage = null;
        row.CaptureHintMessage = null;
        row.PendingGesture = null;
    }

    /// <summary>Sale del modo captura sin cambiar nada — Escape, perder el foco, o cancelar un conflicto
    /// pendiente. Instance method (not static) so <c>SettingsView.axaml.cs</c> can call it the same way as
    /// every other row-scoped method on this viewmodel, even though it only touches the row itself.</summary>
#pragma warning disable CA1822
    public void CancelCapture(ShortcutRowViewModel row)
    {
        row.IsCapturing = false;
        row.ConflictMessage = null;
        row.CaptureHintMessage = null;
        row.PendingGesture = null;
    }
#pragma warning restore CA1822

    /// <summary>A gesture without Ctrl/Alt/Meta (a bare letter, or Shift+letter) is rejected instead of
    /// applied — see <c>SettingsView.axaml.cs</c>'s capture handler for the one exception (function keys).
    /// Leaves the row in capture mode so the user can just try another combination.</summary>
#pragma warning disable CA1822
    public void RejectCapturedGesture(ShortcutRowViewModel row)
    {
        row.CaptureHintMessage = LocalizationService.Instance["Settings_Shortcuts_RequiresModifierWarning"];
    }
#pragma warning restore CA1822

    /// <summary>Aplica el gesto recién capturado — si colisiona con otro atajo, se detiene en un estado de
    /// aviso (propuesta 29 §"Detección de conflictos") en vez de guardar directamente; el usuario decide
    /// entre <see cref="ConfirmConflictingShortcut"/> y <see cref="CancelConflictingShortcut"/>.</summary>
    public void ApplyCapturedGesture(ShortcutRowViewModel row, KeyGesture gesture)
    {
        row.IsCapturing = false;
        row.CaptureHintMessage = null;

        var conflictId = ShortcutRegistry.FindConflict(row.Id, gesture);
        if (conflictId is null)
        {
            CommitGesture(row, gesture);
            return;
        }

        var conflictLabelKey = ShortcutCatalog.All.First(d => d.Id == conflictId).LabelKey;
        row.PendingGesture = gesture;
        row.ConflictMessage = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.Instance["Settings_Shortcuts_ConflictMessage"],
            LocalizationService.Instance[conflictLabelKey]);
    }

    [RelayCommand]
    private void ConfirmConflictingShortcut(ShortcutRowViewModel row)
    {
        if (row.PendingGesture is { } gesture) CommitGesture(row, gesture);
    }

#pragma warning disable CA1822
    [RelayCommand]
    private void CancelConflictingShortcut(ShortcutRowViewModel row)
    {
        row.PendingGesture = null;
        row.ConflictMessage = null;
    }
#pragma warning restore CA1822

    [RelayCommand]
    private void ResetShortcut(ShortcutRowViewModel row)
    {
        ShortcutRegistry.ResetToDefault(row.Id);
        ClearRowValidationState(row);
        PersistShortcutOverrides();
    }

    [RelayCommand]
    private void ResetAllShortcuts()
    {
        ShortcutRegistry.ResetAll();
        foreach (var category in ShortcutCategories)
            foreach (var row in category.Items)
                ClearRowValidationState(row);
        PersistShortcutOverrides();
    }

    private void CommitGesture(ShortcutRowViewModel row, KeyGesture gesture)
    {
        ShortcutRegistry.SetOverride(row.Id, gesture);
        ClearRowValidationState(row);
        PersistShortcutOverrides();
    }

    private static void ClearRowValidationState(ShortcutRowViewModel row)
    {
        row.PendingGesture = null;
        row.ConflictMessage = null;
        row.CaptureHintMessage = null;
    }

    /// <summary>Escribe el snapshot de overrides a <see cref="Models.AppSettings.ShortcutOverrides"/> y lo persiste
    /// de inmediato — a diferencia del resto de Ajustes, remapear un atajo es una acción discreta y poco
    /// frecuente (no un arrastre de slider), así que no hace falta el debounce de <see cref="DebouncedSave"/>.</summary>
    private void PersistShortcutOverrides()
    {
        _settingsService.Settings.ShortcutOverrides = new Dictionary<string, string>(ShortcutRegistry.ExportOverrides());
        _settingsService.Save();
    }

    /// <summary>Refresca <see cref="ShortcutRowViewModel.GestureDisplay"/>/<see cref="ShortcutRowViewModel.IsCustomized"/>
    /// en todas las filas — suscrito a <see cref="Services.ShortcutRegistry.Changed"/> en el constructor,
    /// así que también cubre los cambios que ya aplicó este mismo viewmodel, no solo los externos.</summary>
    private void RefreshAllShortcutRows()
    {
        foreach (var category in ShortcutCategories)
            foreach (var row in category.Items)
            {
                row.GestureDisplay = ShortcutRegistry.GetGestureDisplay(row.Id);
                row.IsCustomized = ShortcutRegistry.IsCustomized(row.Id);
            }
    }
}
