using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>Encuentra qué notas del vault enlazan a una nota objetivo mediante wikilinks.</summary>
public static class EditorBacklinksService
{
    /// <summary>Busca en <paramref name="allNotesWithContent"/> referencias a <paramref name="targetNote"/>,
    /// resolviendo cada wikilink con el mismo criterio que <see cref="WikiLinkParser.ResolveTarget"/>
    /// (nombre de fichero primero, título como respaldo si es inequívoco) en vez del texto plano por título
    /// que se usaba antes — así este panel y el Ctrl+click del editor nunca discrepan sobre a qué nota
    /// apunta un enlace (docs/analisis-proyecto/roadmap.md, Fase 5). Devuelve contexto reducido: la línea
    /// que contiene el primer wikilink de cada nota que resuelve a <paramref name="targetNote"/>.
    ///
    /// <para>Takes each note's content already read (<see cref="VaultService.GetAllNotesRecursiveWithContent"/>)
    /// instead of re-reading every file itself — this used to mean every backlinks refresh scanned the
    /// vault twice (once for metadata via the caller, once more here for content), the exact "doble barrido"
    /// docs/analisis-proyecto/roadmap.md, Fase 6, análisis §3 calls out.</para></summary>
    public static IReadOnlyList<EditorBacklink> FindBacklinks(
        IReadOnlyList<(NoteFile Note, string Content)> allNotesWithContent, NoteFile targetNote)
    {
        var allNotes = allNotesWithContent.Select(t => t.Note).ToList();
        var result = new List<EditorBacklink>();

        foreach (var (note, content) in allNotesWithContent)
        {
            if (VaultService.PathsEqual(note.FullPath, targetNote.FullPath)) continue;

            var context = FindFirstLinkContext(content, allNotes, targetNote);
            if (context is null) continue;

            result.Add(new EditorBacklink
            {
                FullPath = note.FullPath,
                Title = note.Title,
                Context = context
            });
        }

        return result;
    }

    /// <summary>Pushes a warning notification naming how many other notes will be left with a broken
    /// wikilink if <paramref name="note"/> is deleted — called right before the delete actually happens
    /// (<c>ViewModels.NotesViewModel.DeleteNote</c>, <c>ViewModels.EditorViewModel.Delete</c>), never
    /// blocking it (análisis §3, "Integridad de la red de enlaces"). No-op when there's nothing to warn
    /// about, or when the caller has no <see cref="NotificationService"/> (tests, headless composition).</summary>
    public static void WarnIfDeletingLeavesBrokenLinks(VaultService vaultService, NotificationService? notificationService, NoteFile note)
    {
        if (notificationService is null) return;

        var allNotesWithContent = vaultService.GetAllNotesAcrossVaultWithContent();
        var backlinks = FindBacklinks(allNotesWithContent, note);
        if (backlinks.Count == 0) return;

        notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Notification_NoteDeletedWithBacklinks_Title"),
            LocalizedTextFormatter.Format("Notification_NoteDeletedWithBacklinks_Message", note.Title, LocalizedTextFormatter.PluralFormat("Notification_NoteDeletedWithBacklinks_Notes", backlinks.Count, backlinks.Count)),
            NotificationSeverity.Warning);
    }

    private static string? FindFirstLinkContext(string content, IReadOnlyList<NoteFile> allNotes, NoteFile targetNote)
    {
        foreach (var link in WikiLinkParser.FindOccurrences(content))
        {
            var resolved = WikiLinkParser.ResolveTarget(allNotes, link.Target);
            if (resolved is null || !VaultService.PathsEqual(resolved.FullPath, targetNote.FullPath)) continue;

            return ExtractContext(content, link);
        }

        return null;
    }

    private static string ExtractContext(string content, WikiLinkOccurrence link)
    {
        var lineStart = content.LastIndexOf('\n', link.Start);
        lineStart = lineStart == -1 ? 0 : lineStart + 1;

        var lineEnd = content.IndexOf('\n', link.End);
        if (lineEnd == -1) lineEnd = content.Length;

        var context = content[lineStart..lineEnd].Trim();
        return context.Length > 120 ? context[..120] + "…" : context;
    }
}
