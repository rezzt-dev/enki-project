using System.Collections.Generic;
using System.Globalization;
using enki.Localization;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Registro de comandos de la paleta unificada (propuesta 25,
/// docs/propuesta-nuevas-funcionalidades.md) — separado en su propio archivo parcial, mismo criterio que
/// <c>MainWindowViewModel.Composition.cs</c>/<c>.Callbacks.cs</c>, porque esta lista crecerá con cada módulo
/// nuevo que quiera aportar una acción y no debería inflar <c>MainWindowViewModel.cs</c>. Deliberadamente
/// acotada a un conjunto curado de acciones de alto valor (navegación + las acciones "de un botón" más
/// usadas de cada módulo) en vez de reflejar automáticamente cada <c>[RelayCommand]</c> de la app — el
/// propio documento de propuesta señala la gobernanza de qué comandos merece la pena registrar como el
/// verdadero reto de esta funcionalidad, no la mecánica del popup (ya resuelta tres veces: Quick Switcher,
/// búsqueda global, este archivo).</summary>
public sealed partial class MainWindowViewModel
{
    private List<CommandPaletteItem> BuildCommandPaletteItems()
    {
        var items = new List<CommandPaletteItem>();

        // Navegación: un comando por cada sección de sidebar actualmente visible, en el mismo orden que
        // NavItems (que ya respeta los ajustes de "Secciones de la barra lateral").
        foreach (var navItem in NavItems)
        {
            var page = navItem.Page;
            items.Add(new CommandPaletteItem(
                string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["CommandPalette_GoToFormat"], navItem.Label),
                navItem.Icon,
                () => Navigate(page)));
        }

        items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_OpenSettings"], MaterialIconKind.CogOutline, OpenSettings));
        items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_OpenReminders"], MaterialIconKind.CalendarClockOutline, OpenReminders));
        items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_OpenNotifications"], MaterialIconKind.BellRingOutline, OpenNotifications));
        items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_ToggleSidebar"], MaterialIconKind.ChevronDoubleLeft, ToggleSidebar));
        items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_ZoomIn"], MaterialIconKind.MagnifyPlusOutline, ZoomIn));
        items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_ZoomOut"], MaterialIconKind.MagnifyMinusOutline, ZoomOut));
        items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_ZoomReset"], MaterialIconKind.BackupRestore, ZoomReset));
        items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_QuickNewNote"], MaterialIconKind.NoteTextOutline, QuickNewNote));
        items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_OpenGlobalSearch"], MaterialIconKind.Magnify, OpenGlobalSearch));
        items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_OpenQuickSwitcher"], MaterialIconKind.NoteSearchOutline, OpenQuickSwitcher));

        if (Notes is not null)
        {
            items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_OpenDailyNote"], MaterialIconKind.CalendarTodayOutline, OpenDailyNoteFromPalette));
            items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_NewNoteFromTemplate"], MaterialIconKind.FileDocumentMultipleOutline, NewNoteFromTemplateFromPalette));
            items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_ImportNotes"], MaterialIconKind.Import, ImportNotesFromPalette));

            // Solo tiene sentido si hay una pestaña de nota activa que enfocar — con ninguna abierta,
            // NotesViewModel.ToggleZenModeCommand ya es un no-op silencioso (reenvía a ActiveEditor?.),
            // así que este comando simplemente no se ofrece en vez de "funcionar" sin efecto visible.
            if (Notes.ActiveEditor is not null)
                items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_ToggleZenMode"], MaterialIconKind.Fullscreen, () => Notes.ToggleZenModeCommand.Execute(null)));
        }

        if (Kanban is not null)
            items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_AddKanbanColumn"], MaterialIconKind.ViewColumnOutline, AddKanbanColumnFromPalette));

        if (Settings is not null)
        {
            items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_ToggleTheme"], MaterialIconKind.ThemeLightDark, () => Settings.ToggleThemeCommand.Execute(null)));

            // Solo se ofrece con un tema personalizado activo — es la vía de escape "solo con teclado"
            // para un tema guardado ilegible (análisis alfa-3 §4.5, Alta): antes de esto, ni esta acción
            // existía ni "Alternar tema" tenía ningún efecto visible mientras un tema personalizado
            // mandaba sobre la paleta claro/oscuro.
            if (Settings.IsCustomThemeActive)
                items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_DisableCustomTheme"], MaterialIconKind.ThemeLightDark, () => Settings.UseSystemThemeCommand.Execute(null)));

            items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_OpenVaultFolder"], MaterialIconKind.FolderOpenOutline, () => Settings.OpenVaultFolderCommand.Execute(null)));
            items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_OpenCommunitySkills"], MaterialIconKind.OpenInNew, () => Settings.OpenCommunitySkillsCommand.Execute(null)));

            // Sin cuenta de Polar vinculada, SyncNowCommand sería otro no-op silencioso
            // (PolarSyncService.SyncNowAsync ya lo es), así que se omite igual que el modo enfoque de
            // arriba en vez de mostrarse siempre.
            if (_polarAuthService.IsLinked)
                items.Add(new CommandPaletteItem(LocalizationService.Instance["CommandPalette_Action_SyncPolarNow"], MaterialIconKind.Sync, () => Settings.SyncNowCommand.Execute(null)));
        }

        return items;
    }

    private void OpenDailyNoteFromPalette()
    {
        Navigate(AppPage.Notes);
        Notes?.OpenDailyNoteCommand.Execute(null);
    }

    /// <summary>Abre Documentos y, encima, el selector de plantillas — el equivalente por teclado del
    /// botón "Desde plantilla", igual que <see cref="OpenDailyNoteFromPalette"/> lo es del de la nota
    /// diaria.</summary>
    private void NewNoteFromTemplateFromPalette()
    {
        Navigate(AppPage.Notes);
        Notes?.ShowTemplatePickerCommand.Execute(null);
    }

    private void ImportNotesFromPalette()
    {
        Navigate(AppPage.Notes);
        Notes?.ImportCommand.Execute(null);
    }

    private void AddKanbanColumnFromPalette()
    {
        Navigate(AppPage.Kanban);
        Kanban?.AddColumnCommand.Execute(null);
    }
}
