using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Which section a pending "vaciar" (empty) confirmation applies to — drives the wording of the
/// shared confirmation dialog in TrashView.axaml (see <see cref="TrashViewModel.PendingEmptyTitle"/>/
/// <see cref="TrashViewModel.PendingEmptyMessage"/>) and which collections <see cref="TrashViewModel.ConfirmEmpty"/> clears.</summary>
public enum TrashEmptyTarget
{
    Documents,
    Tasks,
    Files,
    PolarTasks,
    Reminders,
    Sessions,
    All
}

public sealed partial class TrashViewModel : ViewModelBase
{
    private readonly VaultService _vaultService;
    private readonly KanbanService _kanbanService;
    private readonly PolarTaskService _polarTaskService;
    private readonly ReminderService _reminderService;
    private readonly NotificationService _notificationService;
    private readonly SessionService _sessionService;
    private readonly Action? _onSessionsChanged;

    public ObservableCollection<NoteCardViewModel> Notes { get; } = [];
    public ObservableCollection<TrashedTaskCardViewModel> Tasks { get; } = [];
    public ObservableCollection<FileCardViewModel> Files { get; } = [];

    /// <summary>Categoría "Tareas Polar" (análisis alfa-3 §4.3, Alta: antes no existía ningún camino de
    /// recuperación). Ofrece restaurar y borrar permanentemente (individual y por categoría), con la misma
    /// advertencia que Recordatorios: si el tombstone aún no llegó a Supabase, purgarlo en local hará que el
    /// servidor no se entere del borrado, así que la acción solo se ejecuta tras confirmación explícita del
    /// usuario.</summary>
    public ObservableCollection<TrashedPolarListCardViewModel> PolarLists { get; } = [];
    public ObservableCollection<TrashedPolarTaskCardViewModel> PolarTasks { get; } = [];
    public ObservableCollection<TrashedReminderCardViewModel> Reminders { get; } = [];
    public ObservableCollection<TrashedSessionCardViewModel> Sessions { get; } = [];

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _isTasksEmpty;

    [ObservableProperty]
    private bool _isFilesEmpty;

    /// <summary>Whether both <see cref="PolarLists"/> and <see cref="PolarTasks"/> are empty.</summary>
    [ObservableProperty]
    private bool _isPolarTasksEmpty;

    [ObservableProperty]
    private bool _isRemindersEmpty;

    [ObservableProperty]
    private bool _isSessionsEmpty;

    /// <summary>Drives the "VACIAR PAPELERA" button, which purges documentos, tareas, archivos, tareas de
    /// Polar, recordatorios y sesiones.</summary>
    public bool IsFullyEmpty => IsEmpty && IsTasksEmpty && IsFilesEmpty && IsPolarTasksEmpty && IsRemindersEmpty && IsSessionsEmpty;

    /// <summary>Combined item count across every section — drives the "Papelera" nav badge so the user
    /// can tell there's something to review without opening the page.</summary>
    [ObservableProperty]
    private int _totalCount;

    /// <summary>Non-null while the "vaciar" confirmation dialog is open — same request/cancel/confirm
    /// shape as KanbanViewModel.CardPendingDelete, just parameterized by which section is being cleared
    /// instead of which card.</summary>
    [ObservableProperty]
    private TrashEmptyTarget? _pendingEmptyTarget;

    /// <summary>Elemento individual pendiente de borrado permanente (una nota, tarea, lista/tarea Polar,
    /// archivo o sesión de la papelera) mientras su diálogo de confirmación está abierto — Fase 3 de
    /// docs/analisis-proyecto/roadmap.md: antes estos borrados se disparaban al primer clic, mientras que
    /// "vaciar papelera" sí pasaba por un modal. Mismo patrón request/cancel/confirm que
    /// <see cref="_pendingEmptyTarget"/>, pero para un solo elemento.</summary>
    [ObservableProperty]
    private object? _pendingPermanentDelete;

    /// <summary>Cuerpo del diálogo de confirmación de borrado permanente individual — el título del
    /// elemento se interpola aquí y no en XAML porque el orden de las palabras alrededor del placeholder
    /// no es fijo entre idiomas (docs/agent-docs/internacionalizacion-i18n.md §1.3).</summary>
    public string PendingPermanentDeleteMessage => PendingPermanentDelete is { } item
        ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Trash_DeleteForeverModal_Message"], Describe(item))
        : string.Empty;

    private static string Describe(object item) => item switch
    {
        NoteCardViewModel note => note.Title,
        FileCardViewModel file => file.FileName,
        TrashedTaskCardViewModel task => task.Title,
        TrashedPolarListCardViewModel polarList => polarList.Title,
        TrashedPolarTaskCardViewModel polarTask => polarTask.Title,
        TrashedSessionCardViewModel session => session.Name,
        _ => string.Empty
    };

    partial void OnPendingPermanentDeleteChanged(object? value) =>
        OnPropertyChanged(nameof(PendingPermanentDeleteMessage));

    public string PendingEmptyTitle => PendingEmptyTarget switch
    {
        TrashEmptyTarget.Documents => LocalizationService.Instance["Trash_EmptyDocumentsModal_Title"],
        TrashEmptyTarget.Tasks => LocalizationService.Instance["Trash_EmptyTasksModal_Title"],
        TrashEmptyTarget.Files => LocalizationService.Instance["Trash_EmptyFilesModal_Title"],
        TrashEmptyTarget.PolarTasks => LocalizationService.Instance["Trash_EmptyPolarTasksModal_Title"],
        TrashEmptyTarget.Reminders => LocalizationService.Instance["Trash_EmptyRemindersModal_Title"],
        TrashEmptyTarget.Sessions => LocalizationService.Instance["Trash_EmptySessionsModal_Title"],
        TrashEmptyTarget.All => LocalizationService.Instance["Trash_EmptyAllModal_Title"],
        _ => string.Empty
    };

    public string PendingEmptyMessage => PendingEmptyTarget switch
    {
        TrashEmptyTarget.Documents => LocalizationService.Instance["Trash_EmptyDocumentsModal_Message"],
        TrashEmptyTarget.Tasks => LocalizationService.Instance["Trash_EmptyTasksModal_Message"],
        TrashEmptyTarget.Files => LocalizationService.Instance["Trash_EmptyFilesModal_Message"],
        TrashEmptyTarget.PolarTasks => LocalizationService.Instance["Trash_EmptyPolarTasksModal_Message"],
        TrashEmptyTarget.Reminders => LocalizationService.Instance["Trash_EmptyRemindersModal_Message"],
        TrashEmptyTarget.Sessions => LocalizationService.Instance["Trash_EmptySessionsModal_Message"],
        TrashEmptyTarget.All => LocalizationService.Instance["Trash_EmptyAllModal_Message"],
        _ => string.Empty
    };

    public TrashViewModel(VaultService vaultService, KanbanService kanbanService, PolarTaskService polarTaskService,
        ReminderService reminderService, NotificationService notificationService, SessionService sessionService,
        Action? onSessionsChanged = null)
    {
        _vaultService = vaultService;
        _kanbanService = kanbanService;
        _polarTaskService = polarTaskService;
        _reminderService = reminderService;
        _notificationService = notificationService;
        _sessionService = sessionService;
        _onSessionsChanged = onSessionsChanged;

        Reload();
    }

    /// <summary>Refresca las etiquetas localizadas cuando <see cref="MainWindowViewModel"/> coordina
    /// un cambio de idioma en tiempo real (i18n Fase 4).</summary>
    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(PendingEmptyTitle));
        OnPropertyChanged(nameof(PendingEmptyMessage));
        OnPropertyChanged(nameof(PendingPermanentDeleteMessage));
        Reload();
    }

    partial void OnPendingEmptyTargetChanged(TrashEmptyTarget? value)
    {
        OnPropertyChanged(nameof(PendingEmptyTitle));
        OnPropertyChanged(nameof(PendingEmptyMessage));
    }

    partial void OnIsEmptyChanged(bool value) => OnPropertyChanged(nameof(IsFullyEmpty));

    partial void OnIsTasksEmptyChanged(bool value) => OnPropertyChanged(nameof(IsFullyEmpty));

    partial void OnIsFilesEmptyChanged(bool value) => OnPropertyChanged(nameof(IsFullyEmpty));

    partial void OnIsRemindersEmptyChanged(bool value) => OnPropertyChanged(nameof(IsFullyEmpty));

    partial void OnIsPolarTasksEmptyChanged(bool value) => OnPropertyChanged(nameof(IsFullyEmpty));

    partial void OnIsSessionsEmptyChanged(bool value) => OnPropertyChanged(nameof(IsFullyEmpty));

    public void Reload()
    {
        Notes.Clear();
        foreach (var note in _vaultService.GetTrashedNotes()) Notes.Add(new NoteCardViewModel(note));
        IsEmpty = Notes.Count == 0;

        Tasks.Clear();
        foreach (var trashed in _kanbanService.Board.TrashedCards) Tasks.Add(new TrashedTaskCardViewModel(trashed));
        IsTasksEmpty = Tasks.Count == 0;

        Files.Clear();
        foreach (var file in _vaultService.GetTrashedFiles()) Files.Add(new FileCardViewModel(_vaultService, file));
        IsFilesEmpty = Files.Count == 0;

        PolarLists.Clear();
        foreach (var list in _polarTaskService.Store.Lists.Where(l => l.IsDeleted)) PolarLists.Add(new TrashedPolarListCardViewModel(list));
        PolarTasks.Clear();
        foreach (var task in _polarTaskService.Store.Tasks.Where(t => t.IsDeleted)) PolarTasks.Add(new TrashedPolarTaskCardViewModel(task));
        IsPolarTasksEmpty = PolarLists.Count == 0 && PolarTasks.Count == 0;

        Reminders.Clear();
        foreach (var reminder in _reminderService.DeletedReminders) Reminders.Add(new TrashedReminderCardViewModel(reminder));
        IsRemindersEmpty = Reminders.Count == 0;

        Sessions.Clear();
        foreach (var session in _sessionService.ListTrashedSessions()) Sessions.Add(new TrashedSessionCardViewModel(session));
        IsSessionsEmpty = Sessions.Count == 0;

        TotalCount = Notes.Count + Tasks.Count + Files.Count + PolarLists.Count + PolarTasks.Count + Reminders.Count + Sessions.Count;
    }

    [RelayCommand]
    private void Restore(NoteCardViewModel card)
    {
        _vaultService.RestoreNote(card.Note);
        Reload();
    }

    [RelayCommand]
    private void RestoreFile(FileCardViewModel card)
    {
        _vaultService.RestoreFile(card.File);
        Reload();
    }

    // ===================== Borrado permanente individual (con confirmación) =====================
    // Fase 3 de docs/analisis-proyecto/roadmap.md: estos borrados se ejecutaban al primer clic, sin el
    // modal que sí exige "vaciar papelera" — la acción más fácil de disparar por error era la menos
    // protegida. Ahora todos pasan por el mismo diálogo de confirmación (Trash_DeleteForeverModal_*).

    [RelayCommand]
    private void RequestDeleteForever(NoteCardViewModel card) => PendingPermanentDelete = card;

    [RelayCommand]
    private void RequestDeleteFileForever(FileCardViewModel card) => PendingPermanentDelete = card;

    [RelayCommand]
    private void RequestDeleteTaskForever(TrashedTaskCardViewModel task) => PendingPermanentDelete = task;

    [RelayCommand]
    private void RequestDeletePolarListForever(TrashedPolarListCardViewModel list) => PendingPermanentDelete = list;

    [RelayCommand]
    private void RequestDeletePolarTaskForever(TrashedPolarTaskCardViewModel task) => PendingPermanentDelete = task;

    [RelayCommand]
    private void RequestDeleteSessionForever(TrashedSessionCardViewModel session) => PendingPermanentDelete = session;

    [RelayCommand]
    private void CancelPermanentDelete() => PendingPermanentDelete = null;

    [RelayCommand]
    private void ConfirmPermanentDelete()
    {
        switch (PendingPermanentDelete)
        {
            case NoteCardViewModel note:
                _vaultService.PermanentlyDelete(note.Note);
                break;
            case FileCardViewModel file:
                _vaultService.PermanentlyDelete(file.File);
                break;
            case TrashedTaskCardViewModel task:
                _kanbanService.PermanentlyDeleteTrashedCard(task.TrashedCard);
                break;
            case TrashedPolarListCardViewModel polarList:
                _polarTaskService.PurgeList(polarList.List);
                break;
            case TrashedPolarTaskCardViewModel polarTask:
                _polarTaskService.PurgeTask(polarTask.Task);
                break;
            case TrashedSessionCardViewModel session:
                SessionService.PermanentlyDeleteTrashedSession(session.Session);
                break;
            default:
                return; // Sin nada pendiente no hay diálogo que cerrar ni nada que borrar.
        }

        PendingPermanentDelete = null;
        Reload();
    }

    [RelayCommand]
    private void RestoreTask(TrashedTaskCardViewModel task)
    {
        var trashed = task.TrashedCard;

        // La columna destino se resuelve ANTES de sacar la tarjeta de la papelera (Fase 3 de
        // docs/analisis-proyecto/roadmap.md): si el tablero no tiene ninguna columna válida, la tarjeta
        // se queda en la papelera — recuperable — en vez de quedar fuera de ambas listas, perdida en
        // silencio. La columna original pudo ser borrada desde entonces: se recurre a la primera.
        var targetColumn = _kanbanService.Board.Columns.FirstOrDefault(c => c.Id == trashed.ColumnId)
            ?? _kanbanService.Board.Columns.FirstOrDefault();
        if (targetColumn is null)
        {
            _notificationService.Push(
                LocalizationService.Instance["Trash_Notify_CardRestoreFailed_Title"],
                string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Trash_Notify_CardRestoreFailed_Message"], trashed.Card.Title),
                NotificationSeverity.Warning);
            return;
        }

        _kanbanService.Board.TrashedCards.Remove(trashed);

        var insertIndex = trashed.CardIndex is { } idx
            ? Math.Min(idx, targetColumn.Cards.Count)
            : targetColumn.Cards.Count;
        targetColumn.Cards.Insert(insertIndex, trashed.Card);

        _kanbanService.Save();
        _notificationService.Push(
            LocalizationService.Instance["Trash_Notify_CardRestored_Title"],
            string.Format(
                CultureInfo.CurrentCulture,
                LocalizationService.Instance["Trash_Notify_CardRestored_Message"],
                trashed.Card.Title,
                targetColumn.Title),
            NotificationSeverity.Success);
        Reload();
    }

    /// <summary>Devuelve una sesión de la papelera a <c>.sessions/</c> (Fase 3, top 10 #10 del análisis) —
    /// con las notas que contuviera, que es exactamente lo que la hace distinta de restaurar una nota
    /// suelta. Avisa al resto de pantallas vía <c>_onSessionsChanged</c> (mismo callback que usa
    /// <c>SessionsViewModel</c> al crear/renombrar/borrar) para que los selectores de sesión de Notas y
    /// Archivos la vuelvan a ofrecer sin esperar a la próxima navegación.</summary>
    [RelayCommand]
    private void RestoreSession(TrashedSessionCardViewModel card)
    {
        if (!_sessionService.RestoreTrashedSession(card.Session))
        {
            // La carpeta ya no estaba en la papelera (borrada a mano entre el listado y el clic) — basta
            // con recargar para que la tarjeta desaparezca de la lista.
            Reload();
            return;
        }

        _notificationService.Push(
            LocalizationService.Instance["Trash_Notify_SessionRestored_Title"],
            string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Trash_Notify_SessionRestored_Message"], card.Name),
            NotificationSeverity.Success);
        _onSessionsChanged?.Invoke();
        Reload();
    }

    [RelayCommand]
    private void RestorePolarList(TrashedPolarListCardViewModel card)
    {
        _polarTaskService.RestoreList(card.List);
        Reload();
    }

    [RelayCommand]
    private void RestorePolarTask(TrashedPolarTaskCardViewModel card)
    {
        _polarTaskService.RestoreTask(card.Task);
        Reload();
    }

    /// <summary>No "eliminar definitivamente" counterpart for a single trashed reminder here on purpose:
    /// a just-deleted reminder is usually still <c>Dirty</c> (its tombstone hasn't reached Polar yet), so
    /// purging it locally before that push happens would leave the server never finding out it was deleted.
    /// Bulk purging of all trashed reminders is available through <see cref="ConfirmEmpty"/>/<see
    /// cref="PurgeAllDeleted"/> instead, after explicit user confirmation.</summary>
    [RelayCommand]
    private void RestoreReminder(TrashedReminderCardViewModel reminder)
    {
        _reminderService.Restore(reminder.Reminder.Id);
        _notificationService.Push(
            LocalizationService.Instance["Trash_Notify_ReminderRestored_Title"],
            string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Trash_Notify_ReminderRestored_Message"], reminder.Title),
            NotificationSeverity.Success);
        Reload();
    }

    // ===================== "Vaciar" (bulk purge) confirmation dialog =====================

    [RelayCommand]
    private void RequestEmptyDocuments() => PendingEmptyTarget = TrashEmptyTarget.Documents;

    [RelayCommand]
    private void RequestEmptyTasks() => PendingEmptyTarget = TrashEmptyTarget.Tasks;

    [RelayCommand]
    private void RequestEmptyFiles() => PendingEmptyTarget = TrashEmptyTarget.Files;

    [RelayCommand]
    private void RequestEmptyPolarTasks() => PendingEmptyTarget = TrashEmptyTarget.PolarTasks;

    [RelayCommand]
    private void RequestEmptyReminders() => PendingEmptyTarget = TrashEmptyTarget.Reminders;

    [RelayCommand]
    private void RequestEmptySessions() => PendingEmptyTarget = TrashEmptyTarget.Sessions;

    /// <summary>Abre el diálogo de confirmación de "vaciar papelera". La guarda replica en el comando la
    /// condición <c>IsEnabled = !IsFullyEmpty</c> del botón de la vista, porque el atajo Ctrl+Shift+E
    /// (TrashView.axaml KeyBindings) ejecuta el comando sin pasar por ese IsEnabled.</summary>
    [RelayCommand]
    private void RequestEmptyTrash()
    {
        if (IsFullyEmpty) return;
        PendingEmptyTarget = TrashEmptyTarget.All;
    }

    [RelayCommand]
    private void CancelEmpty() => PendingEmptyTarget = null;

    [RelayCommand]
    private void ConfirmEmpty()
    {
        var notesDeleted = 0;
        var tasksDeleted = 0;
        var filesDeleted = 0;
        var polarTasksDeleted = 0;
        var remindersDeleted = 0;
        var sessionsDeleted = 0;

        if (PendingEmptyTarget is TrashEmptyTarget.Documents or TrashEmptyTarget.All)
        {
            var trashedNotes = _vaultService.GetTrashedNotes();
            notesDeleted = trashedNotes.Count;
            foreach (var note in trashedNotes) _vaultService.PermanentlyDelete(note);
        }

        if (PendingEmptyTarget is TrashEmptyTarget.Tasks or TrashEmptyTarget.All)
        {
            tasksDeleted = _kanbanService.PermanentlyDeleteAllTrashedCards();
        }

        if (PendingEmptyTarget is TrashEmptyTarget.Files or TrashEmptyTarget.All)
        {
            var trashedFiles = _vaultService.GetTrashedFiles();
            filesDeleted = trashedFiles.Count;
            foreach (var file in trashedFiles) _vaultService.PermanentlyDelete(file);
        }

        if (PendingEmptyTarget is TrashEmptyTarget.PolarTasks or TrashEmptyTarget.All)
        {
            polarTasksDeleted = _polarTaskService.Store.Lists.Count(l => l.IsDeleted)
                              + _polarTaskService.Store.Tasks.Count(t => t.IsDeleted);
            _polarTaskService.PurgeAllDeleted();
        }

        if (PendingEmptyTarget is TrashEmptyTarget.Reminders or TrashEmptyTarget.All)
        {
            remindersDeleted = _reminderService.DeletedReminders.Count();
            _reminderService.PurgeAllDeleted();
        }

        if (PendingEmptyTarget is TrashEmptyTarget.Sessions or TrashEmptyTarget.All)
        {
            var trashedSessions = _sessionService.ListTrashedSessions();
            sessionsDeleted = trashedSessions.Count;
            foreach (var session in trashedSessions) SessionService.PermanentlyDeleteTrashedSession(session);
        }

        if (notesDeleted + tasksDeleted + filesDeleted + polarTasksDeleted + remindersDeleted + sessionsDeleted > 0)
            _notificationService.Push(
                LocalizationService.Instance["Trash_Notify_TrashEmptied_Title"],
                BuildEmptyPushMessage(notesDeleted, tasksDeleted, filesDeleted, polarTasksDeleted, remindersDeleted, sessionsDeleted),
                NotificationSeverity.Warning);

        PendingEmptyTarget = null;
        Reload();
    }

    /// <summary>Builds the "N documents, M tasks and K files permanently deleted." notification body for a
    /// bulk purge that can span multiple sections at once (only the "empty everything" target can hit more
    /// than one category in the same call). Each count is resolved through <see cref="Plural"/> so it picks
    /// the correct singular/plural form per locale instead of the old hand-rolled "s" suffix, and the
    /// per-category fragments are joined into the final sentence via a resource-keyed template (see
    /// docs/agent-docs/internacionalizacion-i18n.md §1.3/§1.4) instead of a fixed-word-order concatenation.</summary>
    private static string BuildEmptyPushMessage(int notesDeleted, int tasksDeleted, int filesDeleted, int polarTasksDeleted, int remindersDeleted, int sessionsDeleted)
    {
        var parts = new List<string>();
        if (notesDeleted > 0) parts.Add(Plural.Get("Trash_Notify_DocumentsCount", notesDeleted, notesDeleted));
        if (tasksDeleted > 0) parts.Add(Plural.Get("Trash_Notify_TasksCount", tasksDeleted, tasksDeleted));
        if (filesDeleted > 0) parts.Add(Plural.Get("Trash_Notify_FilesCount", filesDeleted, filesDeleted));
        if (polarTasksDeleted > 0) parts.Add(Plural.Get("Trash_Notify_PolarTasksCount", polarTasksDeleted, polarTasksDeleted));
        if (remindersDeleted > 0) parts.Add(Plural.Get("Trash_Notify_RemindersCount", remindersDeleted, remindersDeleted));
        if (sessionsDeleted > 0) parts.Add(Plural.Get("Trash_Notify_SessionsCount", sessionsDeleted, sessionsDeleted));

        var itemsList = parts.Count <= 1
            ? string.Join(string.Empty, parts)
            : string.Join(", ", parts.Take(parts.Count - 1)) + $" {LocalizationService.Instance["Trash_Notify_ListConjunction"]} " + parts[^1];

        return string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Trash_Notify_TrashEmptied_Message"], itemsList);
    }
}
