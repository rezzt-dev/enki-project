using System;
using System.Collections.Generic;
using System.Linq;
using enki.Models;
using enki.ViewModels;

namespace enki.Services;

/// <summary>Resuelve el color con el que el Calendario pinta cada origen — una columna de Kanban, una
/// lista de Polar o los recordatorios (docs/calendar-feature/07-filtros-busqueda-y-capas.md §1b).
///
/// Es una personalización *del calendario*, no de Kanban ni de "Gestión de tareas": el color elegido se
/// guarda en <see cref="AppSettings.CalendarLayerColors"/> y no toca ni <c>KanbanColumn</c> ni
/// <c>PolarTaskList.Color</c> (que además viaja a Supabase, así que reescribirlo cambiaría la lista para
/// todos los dispositivos del usuario por un ajuste puramente visual de esta pantalla).
///
/// No cachea: <see cref="BuildMap"/> se llama una vez por reconstrucción (una en
/// <see cref="CalendarAggregationService.BuildItems"/>, otra en <c>CalendarViewModel.RebuildLayers</c>),
/// que es el mismo recorrido O(columnas + listas) que esos métodos ya hacen. Una caché aquí solo añadiría
/// invalidaciones que olvidar cuando el usuario crea una columna o se vincula Polar.</summary>
public sealed class CalendarLayerColorService(
    KanbanService kanbanService,
    PolarTaskService polarTaskService,
    PolarAuthService polarAuthService,
    SettingsService settingsService)
{
    /// <summary>Clave de color de una columna de Kanban. Deliberadamente distinta de
    /// <see cref="CalendarAggregationService.KanbanLayerKey"/>: la *capa* (mostrar/ocultar) sigue siendo
    /// una sola para todo Kanban. En columnas base es un alias del color compartido por rol.</summary>
    public static string KanbanColumnColorKey(string columnId) => $"kanban:{columnId}";

    /// <summary>Los estados base comparten identidad entre todos los tableros.</summary>
    public static string KanbanRoleColorKey(CoreColumnRole role) => $"kanban:role:{role}";

    private string NormalizeColorKey(string key)
    {
        var column = kanbanService.AllColumns.FirstOrDefault(c => KanbanColumnColorKey(c.Id) == key);
        return column is { Role: not CoreColumnRole.None } ? KanbanRoleColorKey(column.Role) : key;
    }

    /// <summary>Recupera colores anteriores por columna; prevalece el primer tablero guardado.
    /// La elección explícita del estado compartido tiene prioridad sobre esos valores heredados.</summary>
    private string? CustomColor(string key)
    {
        var overrides = settingsService.Settings.CalendarLayerColors;
        if (overrides.TryGetValue(key, out var value) && HexColorUtility.NormalizeHex(value) is { } hex)
            return hex;
        if (!key.StartsWith("kanban:role:", StringComparison.Ordinal)) return null;
        return kanbanService.AllColumns
            .Where(c => c.Role != CoreColumnRole.None && KanbanRoleColorKey(c.Role) == key)
            .Select(c => overrides.GetValueOrDefault(KanbanColumnColorKey(c.Id)))
            .Select(HexColorUtility.NormalizeHex).FirstOrDefault(c => c is not null);
    }

    /// <summary>Se dispara cuando el usuario cambia o restablece un color, para que el calendario se
    /// repinte. Mismo patrón <c>Action?</c> que <c>KanbanService.BoardChanged</c>.</summary>
    public event Action? ColorsChanged;

    /// <summary>Mapa completo clave-de-color → hex para el estado actual: los tres/N colores de columnas
    /// de Kanban, el de recordatorios y uno por lista de Polar, con los colores elegidos a mano ya
    /// aplicados encima de los automáticos.</summary>
    public Dictionary<string, string> BuildMap()
    {
        var overrides = settingsService.Settings.CalendarLayerColors;
        var columns = kanbanService.AllColumns;
        // Same gate the calendar itself uses before showing any Polar data.
        var lists = polarAuthService.IsLinked
            ? polarTaskService.Store.Lists.Where(l => !l.IsDeleted).OrderBy(l => l.OrderIndex).ToList()
            : [];

        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CalendarAggregationService.KanbanLayerKey] = CalendarItemViewModel.KanbanLayerColorHex,
            [CalendarAggregationService.ReminderLayerKey] = CalendarItemViewModel.ReminderLayerColorHex
        };

        // 1. Colores fijos por rol: pendientes/en proceso/realizadas.
        foreach (var column in columns)
        {
            if (CalendarLayerPalette.DefaultForRole(column.Role) is { } roleColor)
                map[KanbanRoleColorKey(column.Role)] = roleColor;
        }

        // 2. Acentos automáticos para lo que no tiene color fijo. Columnas personalizadas y listas de
        //    Polar comparten un solo reparto para que una lista y una columna no acaben del mismo color
        //    (doc §1b: el objetivo es distinguir el origen).
        var accentKeys = columns
            .Where(c => CalendarLayerPalette.DefaultForRole(c.Role) is null)
            .Select(c => KanbanColumnColorKey(c.Id))
            .Concat(lists.Select(l => l.Id))
            .ToList();

        var pinnedColors = overrides.Values
            .Select(HexColorUtility.NormalizeHex)
            .Where(hex => hex is not null)
            .Select(hex => hex!)
            .ToList();

        foreach (var (key, color) in CalendarLayerPalette.AssignAccents(
                     accentKeys.Where(k => !overrides.ContainsKey(k)), pinnedColors))
            map[key] = color;

        // 3. El color elegido a mano gana sobre cualquier automático, incluidos los tres roles core: los
        //    rojo/ámbar/verde son el punto de partida, no una imposición ("el usuario podrá cambiar el
        //    color identificativo").
        foreach (var key in map.Keys.ToList())
        {
            if (CustomColor(key) is { } normalized)
                map[key] = normalized;
        }

        foreach (var key in accentKeys)
        {
            if (CustomColor(key) is { } normalized)
                map[key] = normalized;
        }

        // Alias por columna para los consumidores existentes de la agregación y creación rápida.
        foreach (var column in columns.Where(c => c.Role != CoreColumnRole.None))
            map[KanbanColumnColorKey(column.Id)] = map[KanbanRoleColorKey(column.Role)];

        return map;
    }

    /// <summary>Color efectivo de <paramref name="colorKey"/>, con el neutro de Kanban como último
    /// recurso — misma caída que ya usaba <c>CalendarAggregationService</c> para una lista de Polar
    /// borrada o desconocida.</summary>
    public static string Resolve(IReadOnlyDictionary<string, string> map, string colorKey) =>
        map.TryGetValue(colorKey, out var hex) ? hex : CalendarItemViewModel.KanbanLayerColorHex;

    /// <summary>Fija el color de una capa. <paramref name="hex"/> se normaliza con
    /// <see cref="HexColorUtility.NormalizeHex"/>; si no es un color válido no se guarda nada.</summary>
    public bool SetColor(string colorKey, string? hex)
    {
        if (string.IsNullOrEmpty(colorKey) || HexColorUtility.NormalizeHex(hex) is not { } normalized) return false;

        colorKey = NormalizeColorKey(colorKey);
        var overrides = settingsService.Settings.CalendarLayerColors;
        if (overrides.TryGetValue(colorKey, out var current) && current == normalized) return true;

        overrides[colorKey] = normalized;
        settingsService.Save();
        ColorsChanged?.Invoke();
        return true;
    }

    /// <summary>Vuelve al color automático de esa capa (quita el color elegido a mano).</summary>
    public void ResetColor(string colorKey)
    {
        colorKey = NormalizeColorKey(colorKey);
        var overrides = settingsService.Settings.CalendarLayerColors;
        var removed = overrides.Remove(colorKey);
        foreach (var column in kanbanService.AllColumns.Where(c =>
                     c.Role != CoreColumnRole.None && KanbanRoleColorKey(c.Role) == colorKey))
            removed |= overrides.Remove(KanbanColumnColorKey(column.Id));
        if (!removed) return;

        settingsService.Save();
        ColorsChanged?.Invoke();
    }

    public bool HasCustomColor(string colorKey) => CustomColor(NormalizeColorKey(colorKey)) is not null;
}
