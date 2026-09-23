using System;
using enki.Localization;

namespace enki.Models;

/// <summary>What caused a <see cref="NoteVersion"/> snapshot to be captured — shown next to its timestamp
/// in the history panel so the user can tell "the editor saved this automatically" apart from "I clicked
/// Restore and Enki protected what I had before doing it".</summary>
public enum NoteVersionTrigger
{
    /// <summary>Captured by the editor's own debounced autosave, throttled to at most one snapshot per
    /// <see cref="Services.NoteVersionHistoryService.MinimumAutosaveInterval"/> of active editing — see
    /// <see cref="Services.NoteVersionHistoryService"/>'s type doc for why every 600ms write can't produce
    /// one of these.</summary>
    Autosave,

    /// <summary>Captured on an explicit user action outside the autosave loop — today, editing the tags
    /// field (<c>EditorViewModel.ApplyTagsInput</c>) flushes a pending save first.</summary>
    Manual,

    /// <summary>Captured of the note's content exactly as it stood immediately before a
    /// <see cref="Services.NoteVersionHistoryService.RestoreVersion"/> overwrote it — the safety net that
    /// makes restoring a version itself undoable, so picking the wrong one from the list is never a
    /// dead end.</summary>
    PreRestore,

    /// <summary>Captured of a note's content immediately before a vault-wide mass edit touches it without
    /// the user reviewing each change individually — today, <c>VaultService.RenameNote</c>'s wikilink-rewrite
    /// cascade, which can silently rewrite dozens of other notes to keep a link pointing at a just-renamed
    /// one.</summary>
    PreBulkOperation,

    /// <summary>Capturada al resolver un conflicto con un cambio hecho fuera de Enki: guarda el contenido
    /// de la versión que el usuario <em>descartó</em> — la suya si eligió cargar la de disco, la de disco
    /// si eligió conservar la suya. Es lo que hace que ninguna de las dos opciones del diálogo de
    /// conflicto sea destructiva. Ver <c>.docs/project-info/cambios-externos-en-el-vault.md</c>.</summary>
    ExternalConflict
}

/// <summary>Metadata for one saved snapshot of a note's content — see
/// <see cref="Services.NoteVersionHistoryService"/> for where these live on disk and how they're captured.
/// The snapshot's actual Markdown content is not part of this record; it's read on demand via
/// <see cref="Services.NoteVersionHistoryService.ReadVersionContent"/> so listing a note's history never
/// has to load every version's full body.</summary>
public sealed class NoteVersion
{
    /// <summary>Sortable, collision-resistant, filesystem-safe identifier — also the snapshot file's name
    /// on disk (see <see cref="Services.NoteVersionHistoryService"/>). Never reused, even for a version
    /// later deleted, so a stale reference to one (a picker still open in another tab) fails a lookup
    /// cleanly instead of accidentally resolving to something else that took its place.</summary>
    public required string Id { get; set; }

    public DateTime CreatedUtc { get; set; }

    public NoteVersionTrigger Trigger { get; set; }

    public int WordCount { get; set; }

    public long SizeBytes { get; set; }

    public string CreatedLabel => RelativeTimeFormatter.Format(CreatedUtc);

    public string SizeLabel => FileSizeFormatter.Format(SizeBytes);

    public string TriggerLabel => Trigger switch
    {
        NoteVersionTrigger.Autosave => LocalizationService.Instance["Editor_History_Trigger_Autosave"],
        NoteVersionTrigger.Manual => LocalizationService.Instance["Editor_History_Trigger_Manual"],
        NoteVersionTrigger.PreRestore => LocalizationService.Instance["Editor_History_Trigger_PreRestore"],
        NoteVersionTrigger.PreBulkOperation => LocalizationService.Instance["Editor_History_Trigger_PreBulkOperation"],
        NoteVersionTrigger.ExternalConflict => LocalizationService.Instance["Editor_History_Trigger_ExternalConflict"],
        _ => string.Empty
    };
}
