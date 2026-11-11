using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using enki.Models;

namespace enki.Services;

/// <summary>Resultado de "Revertir esta respuesta".</summary>
/// <param name="Reverted">Acciones deshechas.</param>
/// <param name="Skipped">Acciones que no se tocaron; cada una dice por qué en <see cref="AiToolUndo.Reason"/>.</param>
/// <param name="NotRevertible">Acciones que cambiaron algo y no tienen vuelta atrás desde aquí (carpetas,
/// archivos, sesiones, Polar, papelera…): se avisa de que siguen como están.</param>
public sealed record AiRevertReport(int Reverted, int Skipped, int NotRevertible);

/// <summary>"Revertir esta respuesta": deshace lo que el asistente cambió en un turno, en orden inverso y sin
/// pisar nada de lo que el usuario haya hecho después.
///
/// <para>Cada herramienta que cambia algo recuperable deja apuntado en su actividad (<see cref="AiToolUndo"/>)
/// cómo era antes y la huella de cómo lo dejó. Al revertir, una acción solo se deshace si el elemento sigue
/// exactamente como lo dejó el asistente; si alguien lo tocó después, o ya no existe, se deja como está y se
/// explica. Las notas vuelven a la versión de su historial (guardando antes la actual, así que revertir tampoco
/// pierde nada) por el mismo camino de escritura comprobada que el resto de herramientas; lo que se creó va a la
/// papelera; lo que se movió o se borró vuelve a su sitio; tarjetas, recordatorios y hábitos, que no tienen
/// historial, recuperan el estado que se guardó con la acción.</para>
///
/// <para>Lo pide el usuario desde el chat, no el modelo: no es una herramienta ni depende del ámbito o de
/// <see cref="AiSettings.AllowVaultChanges"/>.</para></summary>
public sealed partial class AiVaultToolbox
{
    private static StringComparer PathComparer => VaultService.PathComparison == StringComparison.OrdinalIgnoreCase
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Deshace las acciones de una respuesta que aún se pueden deshacer y deja en cada una el resultado.</summary>
    public AiRevertReport RevertActivities(IReadOnlyList<AiToolActivity> activities)
    {
        var (reverted, skipped) = (0, 0);
        foreach (var activity in activities.Reverse())
        {
            if (activity.State != AiToolActivityState.Succeeded || activity.Undo is not { CanAttempt: true } undo) continue;
            AiUndoSkipReason? reason;
            try
            {
                reason = _vault.IsVaultConfigured ? Revert(undo) : AiUndoSkipReason.Failed;
            }
            catch (Exception ex) when (RevertFailure(ex) is { } failure)
            {
                if (failure == AiUndoSkipReason.Failed) LogService.Warning($"[ia] no se pudo revertir '{activity.Tool}'", ex);
                reason = failure;
            }
            undo.State = reason is null ? AiUndoState.Reverted : AiUndoState.Skipped;
            undo.Reason = reason;
            // Lo que el modelo sabía de esta acción ha cambiado: se le vuelve a contar en el turno siguiente.
            undo.ModelInformed = false;
            if (reason is null) reverted++;
            else skipped++;
        }
        if (reverted > 0) _host.VaultChanged();

        var notRevertible = activities.Count(a => a.State == AiToolActivityState.Succeeded && a.Undo is null
            && _tools.TryGetValue(a.Tool, out var tool) && tool.Effect != AiToolEffect.Read);
        return new AiRevertReport(reverted, skipped, notRevertible);
    }

    private static AiUndoSkipReason? RevertFailure(Exception ex) => ex switch
    {
        SessionLockedException => AiUndoSkipReason.Locked,
        AiToolException => AiUndoSkipReason.ChangedSince,
        ExternalModificationException => AiUndoSkipReason.ChangedSince,
        FileNotFoundException or DirectoryNotFoundException => AiUndoSkipReason.Missing,
        IOException or UnauthorizedAccessException or VaultUnavailableException or InvalidOperationException or ArgumentException
            or InvalidDataException or TimeoutException or JsonException => AiUndoSkipReason.Failed,
        _ => null
    };

    /// <returns><see langword="null"/> si se deshizo; si no, por qué se dejó como estaba.</returns>
    private AiUndoSkipReason? Revert(AiToolUndo undo) => undo.Kind switch
    {
        AiUndoKind.NoteEdited => RevertNoteEdit(undo),
        AiUndoKind.NoteCreated => RevertNoteCreation(undo),
        AiUndoKind.NoteMoved => RevertNoteMove(undo),
        AiUndoKind.NoteTrashed => RevertNoteTrash(undo),
        AiUndoKind.KanbanCardCreated => RevertCardCreation(undo),
        AiUndoKind.KanbanCardChanged => RevertCardChange(undo),
        AiUndoKind.KanbanCardTrashed => RevertCardTrash(undo),
        AiUndoKind.ReminderCreated => RevertReminderCreation(undo),
        AiUndoKind.ReminderChanged => RevertReminderChange(undo),
        AiUndoKind.ReminderTrashed => RevertReminderTrash(undo),
        AiUndoKind.HabitMarked => RevertHabitMark(undo),
        AiUndoKind.SafeSettingChanged => RevertSetting(undo),
        AiUndoKind.CalendarLayerChanged => RevertCalendarLayer(undo),
        _ => AiUndoSkipReason.Failed
    };

    // ===================== Notas =====================

    private static AiToolUndo UndoCreatedNote(string path) => new()
    {
        Kind = AiUndoKind.NoteCreated,
        Target = path,
        ResultHash = NoteDiskStamps.TryRead(path)?.ContentHash
    };

    /// <summary>Una ruta guardada en el historial solo se toca si sigue dentro del vault abierto y su sesión,
    /// si la tiene, no está bloqueada.</summary>
    private AiUndoSkipReason? NotePathProblem(string? path)
    {
        if (string.IsNullOrEmpty(path) || !VaultService.IsWithinFolder(path, _vault.VaultPath)) return AiUndoSkipReason.Missing;
        return _vault.IsPathInLockedSession(path) ? AiUndoSkipReason.Locked : null;
    }

    /// <summary>La nota tal como está en disco, con lo que su pestaña tuviera sin guardar ya volcado, o el motivo
    /// por el que no se puede tocar. Si el contenido no es el que dejó el asistente, alguien la editó después.</summary>
    private AiUndoSkipReason? LoadUnchangedNote(AiToolUndo undo, out (NoteFile Note, string Content, NoteDiskStamp Stamp) fresh)
    {
        fresh = default;
        if (NotePathProblem(undo.Target) is { } problem) return problem;
        if (!File.Exists(undo.Target)) return AiUndoSkipReason.Missing;
        if (!_host.PrepareNoteChange(undo.Target)) return AiUndoSkipReason.Busy;
        if (_vault.ReadFromDisk(undo.Target) is not { } disk) return AiUndoSkipReason.Missing;
        if (undo.ResultHash is null || !string.Equals(disk.Stamp.ContentHash, undo.ResultHash, StringComparison.Ordinal))
            return AiUndoSkipReason.ChangedSince;
        fresh = disk;
        return null;
    }

    private AiUndoSkipReason? RevertNoteEdit(AiToolUndo undo)
    {
        if (LoadUnchangedNote(undo, out var fresh) is { } problem) return problem;
        if (undo.VersionId is null || _vault.ReadVersionContent(fresh.Note, undo.VersionId) is not { } previous)
            return AiUndoSkipReason.HistoryMissing;
        CommitNoteChange(undo.Target, fresh.Note, fresh.Content, fresh.Stamp, previous);
        return null;
    }

    private AiUndoSkipReason? RevertNoteCreation(AiToolUndo undo)
    {
        if (LoadUnchangedNote(undo, out var fresh) is { } problem) return problem;
        _vault.DeleteNote(fresh.Note);
        _host.NoteChanged(undo.Target, null);
        return null;
    }

    private AiUndoSkipReason? RevertNoteMove(AiToolUndo undo)
    {
        if ((NotePathProblem(undo.Target) ?? NotePathProblem(undo.PreviousPath)) is { } problem) return problem;
        var previousPath = undo.PreviousPath!;
        if (_vault.GetNote(undo.Target) is not { } note || !File.Exists(undo.Target)) return AiUndoSkipReason.Missing;
        var previousFolder = Path.GetDirectoryName(previousPath)!;
        if (!Directory.Exists(previousFolder)) return AiUndoSkipReason.Missing;
        if (File.Exists(previousPath)) return AiUndoSkipReason.Occupied;
        if (!_host.PrepareNoteChange(undo.Target)) return AiUndoSkipReason.Busy;

        // Mover o renombrar de vuelta no pierde nada aunque el usuario haya editado la nota: el contenido viaja con ella.
        var result = note;
        if (!VaultService.PathsEqual(Path.GetDirectoryName(result.FullPath)!, previousFolder))
            result = _vault.TransferNote(result, previousFolder, cut: true);
        if (!VaultService.PathsEqual(result.FullPath, previousPath))
            result = _vault.RenameNote(result, Path.GetFileNameWithoutExtension(previousPath));
        _kanban.RelinkNotePath(undo.Target, result.FullPath);
        _host.NoteChanged(undo.Target, result);
        return null;
    }

    private AiUndoSkipReason? RevertNoteTrash(AiToolUndo undo)
    {
        if (NotePathProblem(undo.PreviousPath) is { } problem) return problem;
        var previousPath = undo.PreviousPath!;
        if (_vault.GetTrashedNotes().FirstOrDefault(n => VaultService.PathsEqual(n.FullPath, undo.Target)) is not { } trashed)
            return AiUndoSkipReason.Missing;
        var previousFolder = Path.GetDirectoryName(previousPath)!;
        if (!Directory.Exists(previousFolder)) return AiUndoSkipReason.Missing;
        if (File.Exists(previousPath)) return AiUndoSkipReason.Occupied;

        // La papelera devuelve las notas sin sesión a la raíz y con el nombre que tuvieran allí: se llevan después
        // a su carpeta y a su nombre de antes.
        _vault.RestoreNote(trashed);
        var result = trashed;
        if (!VaultService.PathsEqual(Path.GetDirectoryName(result.FullPath)!, previousFolder))
            result = _vault.MoveNote(result, previousFolder);
        if (!VaultService.PathsEqual(result.FullPath, previousPath))
            result = _vault.RenameNote(result, Path.GetFileNameWithoutExtension(previousPath));
        _host.NoteChanged(result.FullPath, _vault.GetNote(result.FullPath) ?? result);
        return null;
    }

    // ===================== Kanban =====================

    /// <summary>Una tarjeta y dónde está: lo que hace falta para devolverla a su estado y a su sitio.</summary>
    private sealed record CardSnapshot(string BoardId, string ColumnId, string? NextCardId, KanbanCard Card);

    private CardSnapshot? SnapshotCard(string cardId)
    {
        if (_kanban.FindCardLocation(cardId) is not { } location) return null;
        var next = location.Column.Cards.SkipWhile(c => c != location.Card).Skip(1).FirstOrDefault()?.Id;
        return new CardSnapshot(location.Board.Id, location.Column.Id, next, location.Card);
    }

    /// <summary>La tarjeta tal como está ahora, ya serializada: lo que se guarda antes de cambiarla.</summary>
    private string? CardState(string cardId) => SnapshotCard(cardId) is { } snapshot ? JsonSerializer.Serialize(snapshot) : null;

    /// <summary>Huella de la tarjeta y de su columna. La posición dentro de la columna no cuenta: reordenar
    /// otras tarjetas no es haber tocado esta.</summary>
    private string? CardHash(string cardId) => SnapshotCard(cardId) is { } snapshot
        ? Hash(snapshot.BoardId + "\n" + snapshot.ColumnId + "\n" + JsonSerializer.Serialize(snapshot.Card))
        : null;

    private AiToolUndo UndoCard(AiUndoKind kind, string cardId, string? previous = null) => new()
    {
        Kind = kind,
        Target = cardId,
        Previous = previous,
        ResultHash = CardHash(cardId)
    };

    private AiUndoSkipReason? RevertCardCreation(AiToolUndo undo)
    {
        if (CardHash(undo.Target) is not { } current) return AiUndoSkipReason.Missing;
        if (current != undo.ResultHash) return AiUndoSkipReason.ChangedSince;
        _kanban.TrashCard(undo.Target);
        return null;
    }

    private AiUndoSkipReason? RevertCardChange(AiToolUndo undo)
    {
        if (SnapshotCard(undo.Target) is not { } current) return AiUndoSkipReason.Missing;
        if (CardHash(undo.Target) != undo.ResultHash) return AiUndoSkipReason.ChangedSince;
        if (undo.Previous is null || JsonSerializer.Deserialize<CardSnapshot>(undo.Previous) is not { } previous)
            return AiUndoSkipReason.Failed;
        if (_kanban.Boards.FirstOrDefault(b => b.Id == previous.BoardId)?.Columns.FirstOrDefault(c => c.Id == previous.ColumnId) is not { } column)
            return AiUndoSkipReason.Missing;

        // Primero el sitio y después el estado: mover aplica las transiciones de la columna (completar, marcar
        // subtareas) y lo guardado tiene que quedar por encima de ellas.
        if (current.BoardId != previous.BoardId || current.ColumnId != previous.ColumnId || current.NextCardId != previous.NextCardId)
        {
            var before = previous.NextCardId is { } next && next != undo.Target && column.Cards.Any(c => c.Id == next) ? next : null;
            _kanban.MoveCardToBoard(undo.Target, previous.BoardId, previous.ColumnId, before);
        }
        RestoreCardFields(current.Card, previous.Card);
        _kanban.Save();
        return null;
    }

    private AiUndoSkipReason? RevertCardTrash(AiToolUndo undo)
    {
        if (_kanban.AllTrashedCards.FirstOrDefault(t => t.Card.Id == undo.Target) is not { } trashed) return AiUndoSkipReason.Missing;
        _kanban.RestoreTrashedCard(trashed);
        // Restaurar desde la papelera quita la marca de archivada; si la tenía al borrarla, vuelve con ella.
        if (undo.Previous is not null && JsonSerializer.Deserialize<CardSnapshot>(undo.Previous) is { Card.ArchivedAtUtc: { } archived }
            && _kanban.FindCardLocation(undo.Target) is { Column.Role: CoreColumnRole.Done } location)
        {
            location.Card.ArchivedAtUtc = archived;
            _kanban.Save();
        }
        return null;
    }

    private static void RestoreCardFields(KanbanCard card, KanbanCard previous)
    {
        card.Title = previous.Title;
        card.Description = previous.Description;
        card.Priority = previous.Priority;
        card.StartDateUtc = previous.StartDateUtc;
        card.DueDateUtc = previous.DueDateUtc;
        card.DurationMinutes = previous.DurationMinutes;
        card.CompletedAtUtc = previous.CompletedAtUtc;
        card.ArchivedAtUtc = previous.ArchivedAtUtc;
        card.LinkedNotePath = previous.LinkedNotePath;
        card.Tags = previous.Tags;
        card.Subtasks = previous.Subtasks;
        card.Links = previous.Links;
        card.Blocks = previous.Blocks;
    }

    // ===================== Recordatorios =====================

    private sealed record ReminderSnapshot(DateTime NotifyAtUtc, int? DurationMinutes, bool IsCompleted, bool IsTriggered,
        bool IsRead, DateTime? AdvanceNoticeSentForUtc);

    private static string ReminderState(Reminder reminder) => JsonSerializer.Serialize(new ReminderSnapshot(reminder.NotifyAtUtc,
        reminder.DurationMinutes, reminder.IsCompleted, reminder.IsTriggered, reminder.IsRead, reminder.AdvanceNoticeSentForUtc));

    /// <summary>Huella de lo que el usuario o el asistente deciden de un recordatorio. Que ya haya avisado o se
    /// haya visto no cuenta: eso cambia solo con el paso del tiempo.</summary>
    private static string ReminderHash(Reminder reminder) => Hash(string.Join('\n', reminder.Title, reminder.Description,
        reminder.NotifyAtUtc.Ticks, reminder.DurationMinutes, (int)reminder.Recurrence, reminder.IsCompleted));

    private static AiToolUndo UndoReminder(AiUndoKind kind, Reminder reminder, string? previous = null) => new()
    {
        Kind = kind,
        Target = reminder.Id,
        Previous = previous,
        ResultHash = ReminderHash(reminder)
    };

    private AiUndoSkipReason? LoadUnchangedReminder(AiToolUndo undo, out Reminder reminder)
    {
        reminder = _reminders.ActiveReminders.FirstOrDefault(r => r.Id == undo.Target)!;
        if (reminder is null) return AiUndoSkipReason.Missing;
        return ReminderHash(reminder) == undo.ResultHash ? null : AiUndoSkipReason.ChangedSince;
    }

    private AiUndoSkipReason? RevertReminderCreation(AiToolUndo undo)
    {
        if (LoadUnchangedReminder(undo, out var reminder) is { } problem) return problem;
        _reminders.Delete(reminder.Id);
        return null;
    }

    private AiUndoSkipReason? RevertReminderChange(AiToolUndo undo)
    {
        if (LoadUnchangedReminder(undo, out var reminder) is { } problem) return problem;
        if (undo.Previous is null || JsonSerializer.Deserialize<ReminderSnapshot>(undo.Previous) is not { } previous)
            return AiUndoSkipReason.Failed;
        if (reminder.NotifyAtUtc != previous.NotifyAtUtc) _reminders.Reschedule(reminder.Id, previous.NotifyAtUtc);
        if (reminder.DurationMinutes != previous.DurationMinutes) _reminders.RescheduleDuration(reminder.Id, previous.DurationMinutes);
        if (reminder.IsCompleted != previous.IsCompleted) _reminders.ToggleCompleted(reminder.Id);
        // Reprogramar rearma el aviso; si antes ya había sonado, devolverlo a su hora no debe hacerlo sonar otra vez.
        reminder.IsTriggered = previous.IsTriggered;
        reminder.IsRead = previous.IsRead;
        reminder.AdvanceNoticeSentForUtc = previous.AdvanceNoticeSentForUtc;
        _reminders.Save();
        return null;
    }

    private AiUndoSkipReason? RevertReminderTrash(AiToolUndo undo)
    {
        if (_reminders.DeletedReminders.All(r => r.Id != undo.Target)) return AiUndoSkipReason.Missing;
        _reminders.Restore(undo.Target);
        return null;
    }

    // ===================== Hábitos =====================

    private AiUndoSkipReason? RevertHabitMark(AiToolUndo undo)
    {
        if (_habits.Store.Habits.FirstOrDefault(h => h.Id == undo.Target) is not { } habit
            || !DateOnly.TryParseExact(undo.Previous, "yyyy-MM-dd", out var date)) return AiUndoSkipReason.Missing;
        if (HabitService.IsCompleted(habit, date) != (undo.ResultHash == "done")) return AiUndoSkipReason.ChangedSince;
        _habits.ToggleCompletion(habit, date);
        return null;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
