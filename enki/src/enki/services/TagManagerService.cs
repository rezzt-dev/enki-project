using System;
using System.Collections.Generic;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Resumen de cuántas entidades tocó una operación del Gestor de etiquetas, por dominio — lo que
/// <c>ViewModels.TagManagerViewModel</c> usa para construir la notificación final ("N notas, M tarjetas y
/// K tareas actualizadas"), mismo espíritu que el recuento por categoría de <c>TrashViewModel.ConfirmEmpty</c>.</summary>
public readonly record struct TagChangeSummary(int Notes, int Kanban, int Polar)
{
    public int Total => Notes + Kanban + Polar;
}

/// <summary>Gestor global de etiquetas (renombrar/fusionar/eliminar) que unifica Notas, Kanban y Polar en
/// una sola vista de mantenimiento del vault — mismo molde que <see cref="CalendarAggregationService"/>:
/// una función de solo lectura sobre el estado ya cargado por los tres servicios fuente para
/// <see cref="BuildEntries"/>, más las mutaciones que escriben directamente sobre las entidades reales
/// (<see cref="NoteFile"/>/<see cref="KanbanCard"/>/<see cref="PolarTask"/>) — nunca crea ni mantiene un
/// segundo almacén de etiquetas (AGENTS.md, "Unificar sin duplicar").
///
/// El alcance de las mutaciones son solo entidades activas: notas no borradas, tarjetas del tablero Kanban
/// (incluidas las archivadas — igual que el filtro de etiquetas principal de Kanban, que tampoco las
/// excluye — pero no las tarjetas de la Papelera) y tareas Polar con <c>IsDeleted == false</c>. La
/// Papelera queda fuera a propósito: sus elementos están a punto de restaurarse o purgarse, y corregir sus
/// etiquetas no aporta nada al índice vivo del vault.</summary>
public sealed class TagManagerService(VaultService vaultService, KanbanService kanbanService, PolarTaskService polarTaskService)
{
    /// <summary>Todas las etiquetas en uso ahora mismo, agregadas por nombre insensible a mayúsculas
    /// (mismo criterio que <c>StatisticsAggregationService.Report.BuildTopTags</c>) con el conteo por
    /// dominio, ordenadas alfabéticamente — no por frecuencia: para una herramienta de limpieza, ver
    /// variantes de ortografía una al lado de la otra ("reunion"/"reunión") es más útil que verlas
    /// dispersas por frecuencia de uso.</summary>
    public List<TagManagerEntry> BuildEntries()
    {
        var noteCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var kanbanCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var polarCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Count(Dictionary<string, int> counts, IEnumerable<string> tags)
        {
            foreach (var tag in tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                names.TryAdd(tag, tag);
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
            }
        }

        foreach (var note in vaultService.GetAllNotesAcrossVault()) Count(noteCounts, note.Tags);
        foreach (var card in kanbanService.AllColumns.SelectMany(c => c.Cards)) Count(kanbanCounts, card.Tags);
        foreach (var task in polarTaskService.Store.Tasks.Where(t => !t.IsDeleted)) Count(polarCounts, task.Tags);

        return names.Values
            .Select(name => new TagManagerEntry
            {
                Name = name,
                NoteCount = noteCounts.GetValueOrDefault(name),
                KanbanCount = kanbanCounts.GetValueOrDefault(name),
                PolarCount = polarCounts.GetValueOrDefault(name)
            })
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Sustituye <paramref name="sourceNames"/> por <paramref name="targetName"/> (o las elimina
    /// si es <see langword="null"/>) en las tres entidades reales a la vez: notas
    /// (<see cref="VaultService.ReplaceTagAcrossVault"/>), tarjetas Kanban (mutadas aquí mismo — a
    /// diferencia de Polar, <see cref="KanbanCard"/> no lleva marca de sincronización que preservar) y
    /// tareas Polar (<see cref="PolarTaskService.ReplaceTagAcrossTasks"/>). Renombrar es esta misma llamada
    /// con un único elemento en <paramref name="sourceNames"/>; fusionar, con varios.</summary>
    public TagChangeSummary ReplaceTag(IEnumerable<string> sourceNames, string? targetName)
    {
        ArgumentNullException.ThrowIfNull(sourceNames);
        var sourceTags = new HashSet<string>(sourceNames.Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.OrdinalIgnoreCase);
        if (sourceTags.Count == 0) return default;
        if (targetName is not null)
        {
            targetName = targetName.Trim();
            if (!IsValidTagName(targetName)) throw new ArgumentException("Nombre de etiqueta no válido.", nameof(targetName));
            // NoteTagHelper normaliza siempre a minúsculas; hacer lo mismo en Kanban y Polar evita
            // que una única etiqueta global termine con tres grafías distintas tras renombrarla.
            targetName = targetName.ToLowerInvariant();
        }

        var notesTouched = vaultService.ReplaceTagAcrossVault(sourceTags, targetName);

        var kanbanTouched = 0;
        foreach (var card in kanbanService.AllColumns.SelectMany(c => c.Cards))
        {
            if (!TagListRewriter.Matches(card.Tags, sourceTags)) continue;
            card.Tags = TagListRewriter.Apply(card.Tags, sourceTags, targetName);
            kanbanTouched++;
        }
        if (kanbanTouched > 0) kanbanService.Save();

        var polarTouched = polarTaskService.ReplaceTagAcrossTasks(sourceTags, targetName);

        return new TagChangeSummary(notesTouched, kanbanTouched, polarTouched);
    }

    /// <summary>La sintaxis inline del frontmatter de notas separa etiquetas con comas y corchetes.
    /// Rechazar esos caracteres evita guardar un nombre que se releería como varias etiquetas.</summary>
    public static bool IsValidTagName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Trim().IndexOfAny([',', '[', ']', '\r', '\n']) < 0;
}
