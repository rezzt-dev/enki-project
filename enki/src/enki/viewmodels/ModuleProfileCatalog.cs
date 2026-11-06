using System;
using System.Collections.Generic;
using System.Linq;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Perfil de uso de partida (ALT-02): un conjunto de módulos activos que se aplica de una vez y
/// que el usuario puede seguir ajustando después con los interruptores de Ajustes › Navegación. No se
/// persiste el perfil elegido —solo los interruptores de cada módulo— y <see cref="ModuleProfileCatalog.Detect"/>
/// lo deduce comparando los módulos activos, de modo que no puede quedar desfasado de los interruptores.</summary>
/// <param name="Id">Identificador estable (también sufijo de las claves de recurso <c>ModuleProfile_*</c>).</param>
/// <param name="Modules">Claves de <see cref="SidebarSectionCatalog.AllKeys"/> que quedan activas.</param>
/// <param name="ReminderTray">Si los accesos de la barra de título a recordatorios y bandeja de entrada
/// quedan visibles; sin el módulo de recordatorios no tienen a qué llevar.</param>
public sealed record ModuleProfile(string Id, IReadOnlyList<string> Modules, bool ReminderTray);

/// <summary>Catálogo de perfiles de uso. Los datos de un módulo desactivado no se tocan nunca: aplicar un
/// perfil solo cambia indicadores de visibilidad en los ajustes del vault, así que reactivarlo devuelve
/// el contenido tal cual estaba.
///
/// <para>El módulo <c>Assistant</c> queda fuera de todos los perfiles: depende del interruptor de IA de
/// Ajustes › Inteligencia artificial (<see cref="AiSettings.Enabled"/>), que implica además ejecutar un
/// CLI externo, y un perfil no debe activarlo ni apagarlo por sorpresa.</para></summary>
public static class ModuleProfileCatalog
{
    public const string NotesOnly = "NotesOnly";
    public const string Planner = "Planner";
    public const string Everything = "Everything";

    /// <summary>Módulos que un perfil gobierna: todos los de <see cref="SidebarSectionCatalog.AllKeys"/>
    /// salvo el asistente de IA.</summary>
    public static readonly IReadOnlyList<string> ManagedModules =
        SidebarSectionCatalog.AllKeys.Where(k => k != "Assistant").ToArray();

    public static readonly IReadOnlyList<ModuleProfile> All =
    [
        new(NotesOnly, ["Notes", "Files", "Trash", "TagManager"], ReminderTray: false),
        new(Planner, ["Dashboard", "Calendar", "Kanban", "Notes", "Trash", "Habits", "Reminders", "Notifications", "Statistics"], ReminderTray: true),
        // Skills queda apagado igual que en una instalación nueva: es opt-in y escribe en el vault.
        new(Everything, ManagedModules.Where(k => k != "Skills").ToArray(), ReminderTray: true),
    ];

    public static ModuleProfile? Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>Id del perfil cuyos módulos coinciden exactamente con <paramref name="enabledModules"/>
    /// (ignorando el asistente), o <c>null</c> si el usuario ha personalizado la selección.</summary>
    public static string? Detect(IEnumerable<string> enabledModules)
    {
        var enabled = enabledModules.Where(ManagedModules.Contains).ToHashSet();
        return All.FirstOrDefault(p => enabled.SetEquals(p.Modules))?.Id;
    }

    /// <summary>Lee el indicador de visibilidad de <paramref name="key"/> en <paramref name="settings"/>.</summary>
    public static bool IsEnabled(AppSettings settings, string key) => key switch
    {
        "Dashboard" => settings.DashboardPanelEnabled,
        "Calendar" => settings.CalendarPanelEnabled,
        "Kanban" => settings.KanbanPanelEnabled,
        "Notes" => settings.NotesPanelEnabled,
        "Files" => settings.FilesPanelEnabled,
        "Sessions" => settings.SessionsPanelEnabled,
        "Trash" => settings.TrashPanelEnabled,
        "TagManager" => settings.TagManagerPanelEnabled,
        "Habits" => settings.HabitsPanelEnabled,
        "Skills" => settings.SkillsPanelEnabled,
        "Reminders" => settings.RemindersSidebarEnabled,
        "Notifications" => settings.NotificationsSidebarEnabled,
        "Statistics" => settings.StatisticsPanelEnabled,
        _ => true
    };

    /// <summary>Escribe el indicador de visibilidad de <paramref name="key"/>; las claves desconocidas y el
    /// asistente se ignoran.</summary>
    public static void SetEnabled(AppSettings settings, string key, bool value)
    {
        switch (key)
        {
            case "Dashboard": settings.DashboardPanelEnabled = value; break;
            case "Calendar": settings.CalendarPanelEnabled = value; break;
            case "Kanban": settings.KanbanPanelEnabled = value; break;
            case "Notes": settings.NotesPanelEnabled = value; break;
            case "Files": settings.FilesPanelEnabled = value; break;
            case "Sessions": settings.SessionsPanelEnabled = value; break;
            case "Trash": settings.TrashPanelEnabled = value; break;
            case "TagManager": settings.TagManagerPanelEnabled = value; break;
            case "Habits": settings.HabitsPanelEnabled = value; break;
            case "Skills": settings.SkillsPanelEnabled = value; break;
            case "Reminders": settings.RemindersSidebarEnabled = value; break;
            case "Notifications": settings.NotificationsSidebarEnabled = value; break;
            case "Statistics": settings.StatisticsPanelEnabled = value; break;
        }
    }

    /// <summary>Aplica <paramref name="profile"/> directamente sobre <paramref name="settings"/> sin pasar
    /// por la interfaz — la vía de la primera ejecución, cuando todavía no existe ningún ViewModel de
    /// Ajustes. Las demás preferencias (orden, nombres, tema) no se tocan.</summary>
    public static void ApplyTo(AppSettings settings, ModuleProfile profile)
    {
        foreach (var key in ManagedModules)
            SetEnabled(settings, key, profile.Modules.Contains(key));
        settings.RemindersPanelEnabled = profile.ReminderTray;
        settings.InboxPanelEnabled = profile.ReminderTray;
    }
}
