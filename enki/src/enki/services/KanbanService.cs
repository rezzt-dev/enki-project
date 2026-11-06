using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using System.Linq;
using enki.Localization;
using enki.Models;

namespace enki.Services;

public sealed partial class KanbanService
{
    private readonly string? _storageDirectory;
    private readonly VaultService? _vaultService;
    private string _boardPath => _vaultService is not null
        ? VaultDataStorage.FilePath(_vaultService, "kanban.json")
        : Path.Combine(_storageDirectory!, "kanban.json");
    private bool _seedPending;
    private KanbanStore _store = new();
    public IReadOnlyList<KanbanBoard> Boards => _store.Boards;
    public KanbanBoard DefaultBoard => Boards.First(b => b.Id == _store.DefaultBoardId);
    public IEnumerable<KanbanColumn> AllColumns => Boards.SelectMany(b => b.Columns);
    public IEnumerable<KanbanTrashedCard> AllTrashedCards => Boards.SelectMany(b => b.TrashedCards);


    /// <summary>El almacén no se pudo cargar o usa un formato desconocido. Se muestra un tablero vacío
    /// de recuperación y se bloquean las escrituras para conservar el archivo original.</summary>
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
    public KanbanService(string? storageDirectory = null, bool deferSeedCreation = false)
    {
        _storageDirectory = storageDirectory ?? AppDataDirectory.Resolve();
        Directory.CreateDirectory(_storageDirectory);
        _seedPending = deferSeedCreation;
        Load();
        PurgeOldTrashedCards();
    }

    public KanbanService(VaultService vaultService, bool deferSeedCreation = false)
    {
        _vaultService = vaultService;
        _seedPending = deferSeedCreation;
        Load();
        PurgeOldTrashedCards();
    }

    /// <summary>Lee el tablero del vault activo después de cambiar de carpeta.</summary>
    public void Reload()
    {
        LoadedFromCorruptData = false;
        Load();
        PurgeOldTrashedCards();
        BoardChanged?.Invoke();
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
        var removed = AllTrashedCards.Where(t => t.DeletedUtc < cutoff).ToList();
        if (removed.Count == 0) return;

        try { RemoveTrashedCards(removed); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        { LogService.Warning("No se pudo purgar la papelera Kanban; se conservan las tarjetas.", ex); }
    }

    /// <summary>Borrado permanente individual desde la Papelera — la acción confirmada por
    /// <c>TrashViewModel.ConfirmPermanentDelete</c>. Centralizada aquí (en vez de que el ViewModel manipule
    /// <see cref="Models.KanbanBoard.TrashedCards"/> directamente) para que <see cref="CardPermanentlyDeleted"/>
    /// se dispare desde el único sitio que de verdad borra una tarjeta para siempre, junto con
    /// <see cref="PurgeOldTrashedCards"/> y <see cref="PermanentlyDeleteAllTrashedCards"/> (Fase 10,
    /// docs/analisis-proyecto/roadmap.md).</summary>
    public void PermanentlyDeleteTrashedCard(KanbanTrashedCard trashed)
    {
        if (FindTrashBoard(trashed) is not null) RemoveTrashedCards([trashed]);
    }

    /// <summary>Vacía todas las papeleras; los avisos solo se publican tras guardar.</summary>
    public int PermanentlyDeleteAllTrashedCards()
    {
        var removed = AllTrashedCards.ToList();
        if (removed.Count > 0) RemoveTrashedCards(removed);
        return removed.Count;
    }

    private void RemoveTrashedCards(List<KanbanTrashedCard> removed)
    {
        var originals = Boards.Select(b => (Board: b, Cards: b.TrashedCards.ToList())).ToList();
        foreach (var trashed in removed) FindTrashBoard(trashed)?.TrashedCards.Remove(trashed);
        try { Save(); }
        catch
        {
            foreach (var (board, cards) in originals) { board.TrashedCards.Clear(); board.TrashedCards.AddRange(cards); }
            throw;
        }
        foreach (var trashed in removed) CardPermanentlyDeleted?.Invoke(trashed.Card.Id);
    }

    /// <summary>Valida un archivo de respaldo sin migrarlo ni escribir sobre él.</summary>
    internal static void ValidateBackupFile(string path)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty(nameof(KanbanStore.SchemaVersion), out _))
        {
            ValidateVersionedBoardIds(document.RootElement);
            ValidateStore(JsonSerializer.Deserialize<KanbanStore>(json) ?? throw new InvalidDataException("Almacén vacío."));
        }
        else
        {
            if (!document.RootElement.TryGetProperty(nameof(KanbanBoard.Columns), out _)) throw new InvalidDataException("Formato Kanban desconocido.");
            var board = JsonSerializer.Deserialize<KanbanBoard>(json) ?? throw new InvalidDataException("Tablero vacío.");
            ValidateStore(new KanbanStore { Boards = [board], DefaultBoardId = board.Id });
        }
    }

    private void Load()
    {
        LoadedFromCorruptData = false;
        try
        {
            JsonFileStore.WithLock(_boardPath, () =>
            {
                if (!File.Exists(_boardPath))
                {
                    var board = _seedPending ? new KanbanBoard { Name = string.Empty } : CreateSeedBoard();
                    _store = new KanbanStore { DefaultBoardId = board.Id, Boards = [board] };
                    return;
                }
                var original = File.ReadAllText(_boardPath);
                using var document = JsonDocument.Parse(original);
                if (document.RootElement.TryGetProperty(nameof(KanbanStore.SchemaVersion), out _))
                {
                    ValidateVersionedBoardIds(document.RootElement);
                    var store = JsonSerializer.Deserialize<KanbanStore>(original)
                        ?? throw new InvalidDataException("Almacén Kanban vacío.");
                    ValidateStore(store);
                    _store = store;
                }
                else
                {
                    if (!document.RootElement.TryGetProperty(nameof(KanbanBoard.Columns), out _))
                        throw new InvalidDataException("Formato Kanban desconocido.");
                    var board = JsonSerializer.Deserialize<KanbanBoard>(original)
                        ?? throw new InvalidDataException("Tablero Kanban vacío.");
                    var store = new KanbanStore { DefaultBoardId = board.Id, Boards = [board] };
                    ValidateStore(store);
                    // No se migra si no puede conservarse el original exacto.
                    var backup = _boardPath + ".pre-multiboard.bak";
                    if (File.Exists(backup) && File.ReadAllText(backup) != original)
                        backup += "." + Guid.NewGuid().ToString("N");
                    if (!File.Exists(backup)) File.Copy(_boardPath, backup);
                    MergeLegacyReviewColumn(board);
                    MigrateCoreColumnRoles(board);
                    JsonFileStore.Save(_boardPath, store);
                    _store = store;
                }
                _seedPending = false;
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                                       or InvalidOperationException or TimeoutException)
        {
            LogService.Warning("No se pudo cargar el almacén Kanban; se protege el archivo original.", ex);
            LoadedFromCorruptData = true;
            _seedPending = false;
            var board = new KanbanBoard { Name = LocalizationService.Instance["Kanban_Seed_BoardTitle"] };
            _store = new KanbanStore { DefaultBoardId = board.Id, Boards = [board] };
        }
    }

    private static void ValidateVersionedBoardIds(JsonElement root)
    {
        if (!root.TryGetProperty(nameof(KanbanStore.Boards), out var boards) || boards.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Falta la colección de tableros.");
        foreach (var board in boards.EnumerateArray())
            if (board.ValueKind != JsonValueKind.Object || !board.TryGetProperty(nameof(KanbanBoard.Id), out var id)
                || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                throw new InvalidDataException("Falta la identidad de un tablero.");
    }

    private static void ValidateStore(KanbanStore store)
    {
        if (store.SchemaVersion != 1 || store.Boards is not { Count: > 0 })
            throw new InvalidDataException("Versión o colección de tableros no válida.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void AddId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                throw new InvalidDataException("Identificador Kanban vacío o duplicado.");
        }
        foreach (var board in store.Boards)
        {
            if (board is null || board.Name is null || board.Columns is null || board.TrashedCards is null)
                throw new InvalidDataException("Tablero Kanban incompleto.");
            AddId(board.Id);
            foreach (var column in board.Columns)
            {
                if (column is null || column.Title is null || column.Cards is null) throw new InvalidDataException("Columna incompleta.");
                AddId(column.Id);
                foreach (var card in column.Cards)
                {
                    ValidateCard(card);
                    AddId(card.Id);
                }
            }
            foreach (var trash in board.TrashedCards)
            {
                if (trash?.Card is null) throw new InvalidDataException("Papelera incompleta.");
                ValidateCard(trash.Card);
                AddId(trash.Card.Id);
            }
        }
        if (!store.Boards.Any(b => b.Id == store.DefaultBoardId))
            store.DefaultBoardId = store.Boards[0].Id;
    }

    private static void ValidateCard(KanbanCard card)
    {
        if (card is null || card.Title is null || card.Description is null || card.Tags is null || card.Subtasks is null
            || card.Tags.Any(t => t is null) || card.Subtasks.Any(t => t is null || t.Title is null || string.IsNullOrWhiteSpace(t.Id)))
            throw new InvalidDataException("Tarjeta incompleta.");
    }

    /// <summary>Crea los datos de bienvenida cuando ya se conoce el idioma de primer arranque.
    /// Un tablero existente, aunque tenga nombres predeterminados, nunca se retraduce.</summary>
    public void EnsureSeedBoard()
    {
        if (!_seedPending) return;
        var board = CreateSeedBoard();
        _store = new KanbanStore { DefaultBoardId = board.Id, Boards = [board] };
        Save();
        _seedPending = false;
    }

    public void Save()
    {
        if (LoadedFromCorruptData) throw new InvalidOperationException("Kanban_Multi_LoadFailed");
        ValidateStore(_store);
        Directory.CreateDirectory(Path.GetDirectoryName(_boardPath)!);
        JsonFileStore.Save(_boardPath, _store);
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
        var cards = AllColumns.SelectMany(c => c.Cards)
            .Concat(AllTrashedCards.Select(t => t.Card));

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
        if (inProgressColumn is null) return;
        inProgressColumn.Cards.AddRange(reviewColumn.Cards);

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
