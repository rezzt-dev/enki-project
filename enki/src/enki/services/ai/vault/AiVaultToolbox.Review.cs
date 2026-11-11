using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>Revisión de cambios por lotes (AIA-07). Con <see cref="AiSettings.ReviewChangesBeforeApplying"/> encendido,
/// lo que el asistente cambia en el contenido de las notas —crear una nota, reescribirla, editarla, añadirle
/// texto, cambiar sus etiquetas o propiedades— no se escribe: se prepara en la cola del turno
/// (<see cref="AiChangeReview"/>) con lo que había y lo que habría. El usuario lo ve como diff bajo la respuesta y
/// aprueba o descarta los cambios juntos.
///
/// <para>Aplicar pasa por el mismo camino de escritura comprobada que el resto de herramientas
/// (<see cref="CommitNoteChange"/>: versión "Asistente" en el historial y <c>WriteContentChecked</c>) y deja en la
/// acción su forma de deshacerla, así que "Revertir esta respuesta" sigue funcionando. Un cambio solo se aplica si
/// la nota sigue exactamente como cuando se preparó; si no, se deja como está y se explica.</para>
///
/// <para>Las acciones estructurales (renombrar, mover, enviar a la papelera) y las de otros módulos se aplican al
/// momento como siempre: no tienen un diff que revisar y se pueden revertir desde la respuesta.</para></summary>
public sealed partial class AiVaultToolbox
{
    private const string QueuedForReviewNote =
        "Not applied yet: the user reviews changes before they are written. This change is queued and will be applied "
        + "only if they approve it. Do not repeat it; continue with the rest and tell the user it awaits their approval.";

    private void StageEdit(AiToolCall call, AiChangeReview review, string path, Func<string, string> transform,
        bool requireCompleteRead)
    {
        var fresh = _vault.ReadFromDisk(path) ?? throw NoteNotFound(Path.GetFileNameWithoutExtension(path));
        call.Target(AiToolTargetKind.Note, fresh.Note.Title, path);
        if (requireCompleteRead && call.Context.CompleteReads[path].ContentHash != fresh.Stamp.ContentHash)
            throw new AiToolException(AiToolErrorKind.Conflict, "The note changed after you read it. Read it again before replacing it.");

        // Un segundo cambio de la misma nota en el turno parte de lo que propuso el primero, que el modelo ya ve
        // al releerla.
        var previous = review.LatestFor(path);
        var current = previous?.ProposedContent ?? fresh.Content;
        var updated = transform(current);
        if (!requireCompleteRead) updated = KeepLineEndings(current, updated);
        // Igual que sin revisión, un cambio que deja la nota como está no es un cambio: no se encola nada.
        if (updated == current) return;

        var workspace = WorkspaceOf(call.Context.Scope, path);
        var change = new AiReviewChange
        {
            Kind = AiReviewChangeKind.Edit,
            Activity = call.Activity,
            FullPath = path,
            Title = fresh.Note.Title,
            WorkspaceName = workspace?.Name ?? string.Empty,
            RelativePath = workspace is null ? Path.GetFileName(path) : RelativePath(workspace, path),
            OriginalContent = current,
            ProposedContent = updated,
            BaselineHash = previous is null ? fresh.Stamp.ContentHash : null,
            DependsOn = previous
        };
        review.Stage(change);
        call.Queued();
    }

    /// <summary>Variante de <see cref="StageEdit"/> que devuelve la nota, para las herramientas que responden con su
    /// resumen: la nota sigue como estaba en disco.</summary>
    private NoteFile StageEditForSummary(AiToolCall call, AiChangeReview review, string path, Func<string, string> transform,
        bool requireCompleteRead)
    {
        StageEdit(call, review, path, transform, requireCompleteRead);
        return _vault.GetNote(path) ?? throw NoteNotFound(Path.GetFileNameWithoutExtension(path));
    }

    private static void StageCreation(AiToolCall call, AiChangeReview review, AiWorkspace workspace, string folder, string title, string content)
    {
        var relativeFolder = RelativePath(workspace, folder);
        review.Stage(new AiReviewChange
        {
            Kind = AiReviewChangeKind.Create,
            Activity = call.Activity,
            FullPath = folder,
            Title = title,
            WorkspaceName = workspace.Name,
            RelativePath = relativeFolder is "." or "" ? title + ".md" : relativeFolder + "/" + title + ".md",
            OriginalContent = string.Empty,
            ProposedContent = content
        });
        call.Queued();
    }

    /// <summary>Resuelve una revisión: aplica, en el orden en que se prepararon, los cambios pendientes de
    /// <paramref name="approved"/> y descarta el resto. Lo pide el usuario desde el chat, no el modelo, así que no
    /// depende del ámbito; sí de <see cref="AiSettings.AllowVaultChanges"/>, que con el interruptor apagado solo
    /// deja descartar.</summary>
    public AiReviewReport ResolveReview(AiChangeReview review, IReadOnlyCollection<AiReviewChange> approved)
    {
        var (applied, notApplied, discarded) = (0, 0, 0);
        foreach (var change in review.Changes.Where(c => c.IsPending).ToList())
        {
            if (!approved.Contains(change) || !ChangesAllowed)
            {
                change.State = AiReviewChangeState.Discarded;
                change.Activity.State = AiToolActivityState.Discarded;
                discarded++;
                continue;
            }
            AiReviewProblem? problem;
            try
            {
                problem = _vault.IsVaultConfigured ? Apply(change) : AiReviewProblem.Failed;
            }
            catch (Exception ex) when (ReviewFailure(ex) is { } failure)
            {
                if (failure == AiReviewProblem.Failed) LogService.Warning($"[ia] no se pudo aplicar '{change.Activity.Tool}'", ex);
                problem = failure;
            }

            if (problem is null)
            {
                change.State = AiReviewChangeState.Applied;
                change.Activity.State = AiToolActivityState.Succeeded;
                change.Activity.Error = null;
                applied++;
            }
            else
            {
                change.State = AiReviewChangeState.NotApplied;
                change.Problem = problem;
                change.Activity.State = AiToolActivityState.Failed;
                change.Activity.Error = LocalizationService.Instance["Assistant_Review_Problem_" + problem];
                notApplied++;
            }
        }
        if (applied > 0) _host.VaultChanged();
        return new AiReviewReport(applied, notApplied, discarded);
    }

    private static AiReviewProblem? ReviewFailure(Exception ex) => ex switch
    {
        SessionLockedException => AiReviewProblem.Locked,
        ExternalModificationException => AiReviewProblem.ChangedSince,
        FileNotFoundException or DirectoryNotFoundException => AiReviewProblem.Missing,
        IOException or UnauthorizedAccessException or VaultUnavailableException or InvalidOperationException or ArgumentException
            or InvalidDataException or TimeoutException or JsonException => AiReviewProblem.Failed,
        _ => null
    };

    /// <returns><see langword="null"/> si se aplicó; si no, por qué se dejó como estaba.</returns>
    private AiReviewProblem? Apply(AiReviewChange change)
    {
        if (change.DependsOn is { State: not AiReviewChangeState.Applied }) return AiReviewProblem.Dependency;
        var path = change.FullPath;
        if (!VaultService.IsWithinFolder(path, _vault.VaultPath)) return AiReviewProblem.Missing;
        if (_vault.IsPathInLockedSession(path)) return AiReviewProblem.Locked;

        if (change.Kind == AiReviewChangeKind.Create)
        {
            if (!Directory.Exists(path)) return AiReviewProblem.Missing;
            var note = _vault.CreateNote(change.Title, path, change.ProposedContent);
            change.Activity.TargetKind = AiToolTargetKind.Note;
            change.Activity.Target = note.Title;
            change.Activity.TargetId = note.FullPath;
            change.Activity.Undo = UndoCreatedNote(note.FullPath);
            return null;
        }

        if (!File.Exists(path)) return AiReviewProblem.Missing;
        if (!_host.PrepareNoteChange(path)) return AiReviewProblem.Busy;
        if (_vault.ReadFromDisk(path) is not { } fresh) return AiReviewProblem.Missing;
        // El primero comprueba la huella de partida; los siguientes, que la nota quedó como la dejó el anterior.
        var unchanged = change.BaselineHash is { } baseline
            ? string.Equals(fresh.Stamp.ContentHash, baseline, StringComparison.Ordinal)
            : Normalize(fresh.Content) == Normalize(change.OriginalContent);
        if (!unchanged) return AiReviewProblem.ChangedSince;

        var (written, stamp, version) = CommitNoteChange(path, fresh.Note, fresh.Content, fresh.Stamp,
            KeepLineEndings(fresh.Content, change.ProposedContent));
        change.Activity.Target = written.Title;
        change.Activity.Undo = new AiToolUndo
        {
            Kind = AiUndoKind.NoteEdited,
            Target = path,
            VersionId = version.Id,
            ResultHash = (stamp ?? NoteDiskStamps.TryRead(path))?.ContentHash
        };
        return null;
    }

    private static string Normalize(string content) => content.Replace("\r\n", "\n", StringComparison.Ordinal);
}
