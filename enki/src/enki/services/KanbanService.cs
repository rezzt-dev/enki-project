using System;
using System.IO;
using System.Linq;
using enki.Localization;
using enki.Models;

namespace enki.Services;

public sealed class KanbanService
{
    private readonly string _boardPath;
    public KanbanBoard Board { get; private set; }

    /// <summary>True cuando <see cref="Board"/> tuvo que reemplazarse por el tablero semilla porque
    /// <c>kanban.json</c> existía pero no se pudo leer o deserializar (archivo dañado) — no se activa en
    /// una instalación nueva donde el archivo simplemente no existe todavía. El compositor
    /// (<see cref="ViewModels.MainWindowViewModel"/>) lo consulta justo después de construir este servicio
    /// para avisar al usuario con <c>NotificationService.Push</c>.</summary>
    public bool LoadedFromCorruptData { get; private set; }

    /// <summary>Raised after every successful <see cref="Save"/> — lets panels that only read the board
    /// (the reminder inbox) stay live without <see cref="ViewModels.KanbanViewModel"/> needing a direct
    /// reference to them. See docs/notificaciones-y-recordatorios.md.</summary>
    public event Action? BoardChanged;

    /// <summary>Raised once per card id right after it's removed for good from
    /// <see cref="Models.KanbanBoard.TrashedCards"/> — either through an explicit "borrar para siempre"
    /// (<see cref="PermanentlyDeleteTrashedCard"/>/<see cref="PermanentlyDeleteAllTrashedCards"/>) or the
    /// automatic 30-day sweep (<see cref="PurgeOldTrashedCards"/>). The composition root wires this to
    /// <see cref="DismissedReminderService.RemoveForCard"/> (Fase 10, docs/analisis-proyecto/roadmap.md) so
    /// a stale dismissal doesn't linger in <c>dismissed-reminders.json</c> forever pointing at a card that no
    /// longer exists.</summary>
    public event Action<string>? CardPermanentlyDeleted;

    /// <param name="storageDirectory">Overrides the default <c>%AppData%/Enki</c> folder — unused in the
    /// running app, but lets tests point persistence at a throwaway directory instead of the developer's
    /// real kanban file.</param>
    public KanbanService(string? storageDirectory = null)
    {
        var configDir = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Enki");
        Directory.CreateDirectory(configDir);
        _boardPath = Path.Combine(configDir, "kanban.json");
        Board = Load();
        PurgeOldTrashedCards();
    }

    /// <summary>Purga automática de la papelera del tablero (Fase 3 de docs/analisis-proyecto/roadmap.md):
    /// las tarjetas llevan <see cref="Models.KanbanTrashedCard.DeletedUtc"/> desde que se borraron, así que
    /// al arrancar se eliminan físicamente las que superan la misma retención de 30 días que ya usa
    /// <see cref="ReminderService"/>'s <c>PurgeOldTombstones</c> para sus borrados lógicos — la papelera
    /// deja de crecer sin límite sin quitar al usuario un margen razonable para restaurar. A diferencia de
    /// los recordatorios no hay sincronización pendiente que proteger: las tarjetas del tablero local
    /// nunca salen del dispositivo.</summary>
    private void PurgeOldTrashedCards()
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var removed = Board.TrashedCards.Where(t => t.DeletedUtc < cutoff).ToList();
        if (removed.Count == 0) return;

        foreach (var trashed in removed) Board.TrashedCards.Remove(trashed);
        LogService.Debug($"KanbanService: purgadas {removed.Count} tarjetas de la papelera con más de 30 días.");
        Save();
        foreach (var trashed in removed) CardPermanentlyDeleted?.Invoke(trashed.Card.Id);
    }

    /// <summary>Borrado permanente individual desde la Papelera — la acción confirmada por
    /// <c>TrashViewModel.ConfirmPermanentDelete</c>. Centralizada aquí (en vez de que el ViewModel manipule
    /// <see cref="Models.KanbanBoard.TrashedCards"/> directamente) para que <see cref="CardPermanentlyDeleted"/>
    /// se dispare desde el único sitio que de verdad borra una tarjeta para siempre, junto con
    /// <see cref="PurgeOldTrashedCards"/> y <see cref="PermanentlyDeleteAllTrashedCards"/> (Fase 10,
    /// docs/analisis-proyecto/roadmap.md).</summary>
    public void PermanentlyDeleteTrashedCard(KanbanTrashedCard trashed)
    {
        Board.TrashedCards.Remove(trashed);
        Save();
        CardPermanentlyDeleted?.Invoke(trashed.Card.Id);
    }

    /// <summary>Borrado permanente masivo — la acción "vaciar papelera" (categoría Tareas o "todo") de
    /// <c>TrashViewModel.ConfirmEmpty</c>. Devuelve cuántas se borraron para que el llamador siga pudiendo
    /// componer su propio recuento agregado sin recontar <see cref="Models.KanbanBoard.TrashedCards"/> por su
    /// cuenta.</summary>
    public int PermanentlyDeleteAllTrashedCards()
    {
        var removed = Board.TrashedCards.Select(t => t.Card.Id).ToList();
        if (removed.Count == 0) return 0;

        Board.TrashedCards.Clear();
        Save();
        foreach (var cardId in removed) CardPermanentlyDeleted?.Invoke(cardId);
        return removed.Count;
    }

    private KanbanBoard Load()
    {
        if (JsonFileStore.TryLoad<KanbanBoard>(_boardPath, out var loaded) && loaded is not null)
        {
            MergeLegacyReviewColumn(loaded);
            Board = loaded;
            if (MigrateCoreColumnRoles(loaded)) Save();
            return loaded;
        }

        if (File.Exists(_boardPath)) LoadedFromCorruptData = true;
        return CreateSeedBoard();
    }

    public void Save()
    {
        JsonFileStore.Save(_boardPath, Board);
        BoardChanged?.Invoke();
    }

    /// <summary>Updates every card's <see cref="Models.KanbanCard.LinkedNotePath"/> pointing at
    /// <paramref name="oldPath"/> to <paramref name="newPath"/> instead — called after a note rename/move
    /// (see <c>NotesViewModel.CommitRenameNote</c>/<c>MoveNoteToFolder</c>) so the nota↔tarea link doesn't
    /// silently go stale the moment its target moves, the same way an open editor tab is re-synced on
    /// rename. Covers trashed cards too, so restoring one from the papelera later doesn't resurrect a
    /// dangling link. No-ops (and doesn't <see cref="Save"/>) if nothing actually pointed at
    /// <paramref name="oldPath"/>.</summary>
    public void RelinkNotePath(string oldPath, string newPath)
    {
        var cards = Board.Columns.SelectMany(c => c.Cards)
            .Concat(Board.TrashedCards.Select(t => t.Card));

        var changed = false;
        foreach (var card in cards)
        {
            if (!string.Equals(card.LinkedNotePath, oldPath, StringComparison.OrdinalIgnoreCase)) continue;
            card.LinkedNotePath = newPath;
            changed = true;
        }

        if (changed) Save();
    }

    /// <summary>The "En revisión" column was retired from the default workflow. Boards saved before
    /// that change still carry it on disk, so any cards left in it are folded into "En progreso"
    /// (the closest still-open state) before the column itself is dropped, instead of silently
    /// discarding whatever the user had in review.</summary>
    private static void MergeLegacyReviewColumn(KanbanBoard board)
    {
        var reviewColumn = board.Columns.FirstOrDefault(c =>
            NormalizeTitle(c.Title) == "EN REVISION");
        if (reviewColumn is null) return;

        var inProgressColumn = board.Columns.FirstOrDefault(c =>
            NormalizeTitle(c.Title) == "EN PROGRESO");
        if (inProgressColumn is not null) inProgressColumn.Cards.AddRange(reviewColumn.Cards);

        board.Columns.Remove(reviewColumn);
    }

    /// <summary>Asigna <see cref="CoreColumnRole"/> a tableros legacy que solo tuvieran
    /// <see cref="KanbanColumn.IsCore"/> = true. La inferencia conserva el comportamiento anterior:
    /// primera columna core = Backlog, segunda = InProgress, tercera = Done. Las columnas no core o que
    /// ya tengan un rol definido no se tocan. Devuelve <c>true</c> si hubo cambios para que el cargador
    /// persista la migración.</summary>
    private static bool MigrateCoreColumnRoles(KanbanBoard board)
    {
        var coreColumns = board.Columns.Where(c => c.IsCore).ToList();
        var changed = false;
        for (var i = 0; i < coreColumns.Count; i++)
        {
            if (coreColumns[i].Role != CoreColumnRole.None) continue;

            coreColumns[i].Role = i switch
            {
                0 => CoreColumnRole.Backlog,
                1 => CoreColumnRole.InProgress,
                2 => CoreColumnRole.Done,
                _ => CoreColumnRole.None
            };
            changed = true;
        }

        return changed;
    }

    private static string NormalizeTitle(string title) => title.Trim().ToUpperInvariant()
        .Replace('Á', 'A').Replace('É', 'E').Replace('Í', 'I').Replace('Ó', 'O').Replace('Ú', 'U');

    /// <summary>Seed board/column/card copy is read from <see cref="LocalizationService"/> at call time
    /// (not cached in a constant) so a board created right after the first-run language picker
    /// (docs/agent-docs/internacionalizacion-i18n.md §3.1/§3.2/§5.4) comes out in whichever language the
    /// user just chose. Once written to <c>kanban.json</c> this becomes ordinary user content (§5.2/§5.4) —
    /// it is never re-translated on a later language change.</summary>
    private static KanbanBoard CreateSeedBoard() => new()
    {
        Name = LocalizationService.Instance["Kanban_Seed_BoardTitle"],
        Columns =
        [
            new KanbanColumn
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = LocalizationService.Instance["Kanban_Seed_ColumnTodo"],
                IsCore = true,
                Role = CoreColumnRole.Backlog,
                Cards =
                [
                    new KanbanCard
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Title = LocalizationService.Instance["Kanban_Seed_Card1_Title"],
                        Description = LocalizationService.Instance["Kanban_Seed_Card1_Description"],
                        Priority = KanbanPriority.High
                    },
                    new KanbanCard
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Title = LocalizationService.Instance["Kanban_Seed_Card2_Title"],
                        Priority = KanbanPriority.Low
                    }
                ]
            },
            new KanbanColumn
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = LocalizationService.Instance["Kanban_Seed_ColumnInProgress"],
                IsCore = true,
                Role = CoreColumnRole.InProgress,
                Cards =
                [
                    new KanbanCard
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Title = LocalizationService.Instance["Kanban_Seed_Card3_Title"],
                        Description = LocalizationService.Instance["Kanban_Seed_Card3_Description"],
                        Priority = KanbanPriority.High,
                        DueDateUtc = DateTime.UtcNow.AddDays(2)
                    }
                ]
            },
            new KanbanColumn
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = LocalizationService.Instance["Kanban_Seed_ColumnDone"],
                IsCore = true,
                Role = CoreColumnRole.Done,
                Cards =
                [
                    new KanbanCard
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Title = LocalizationService.Instance["Kanban_Seed_Card4_Title"],
                        Priority = KanbanPriority.Medium
                    }
                ]
            }
        ]
    };
}
