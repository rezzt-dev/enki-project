using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>
/// Búsqueda global que consulta en paralelo notas, tarjetas Kanban, tareas Polar (solo si la cuenta
/// está vinculada) y recordatorios. Los resultados se muestran en un popup anclado a la barra de
/// título; al seleccionar uno se invoca el callback de navegación que el shell proporciona.
/// </summary>
public sealed partial class GlobalSearchViewModel : ObservableObject
{
    private readonly VaultService _vaultService;
    private readonly NoteSearchService _noteSearchService;
    private readonly KanbanService _kanbanService;
    private readonly PolarTaskService _polarTaskService;
    private readonly ReminderService _reminderService;
    private readonly PolarAuthService _polarAuthService;
    private readonly Action<GlobalSearchResultViewModel> _onResultSelected;
    private readonly DispatcherTimer? _searchDebounceTimer;

    /// <summary>
    /// Texto introducido en el cuadro de búsqueda. Cada cambio reinicia un debounce de 200 ms
    /// antes de ejecutar la consulta, igual que el buscador de notas.
    /// </summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// Indica si el popup de búsqueda está abierto.
    /// </summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>
    /// Resultados de la última búsqueda.
    /// </summary>
    public ObservableCollection<GlobalSearchResultViewModel> Results { get; } = [];

    /// <summary>
    /// True cuando hay al menos un resultado.
    /// </summary>
    public bool HasResults => Results.Count > 0;

    /// <summary>
    /// True cuando el usuario ha escrito algo pero no hay resultados.
    /// </summary>
    public bool IsEmpty => !HasResults && !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>
    /// True cuando no hay texto de búsqueda; muestra el mensaje inicial del popup.
    /// </summary>
    public bool ShowSearchPrompt => string.IsNullOrWhiteSpace(SearchText);

    /// <summary>
    /// True cuando hay texto pero no hay resultados; muestra el estado vacío.
    /// </summary>
    public bool ShowNoResults => IsEmpty && !IsSearching;

    /// <summary>
    /// True mientras las cuatro búsquedas paralelas (notas/Kanban/Polar/recordatorios) están en curso —
    /// evita que el popup muestre "no se encontraron resultados" por un instante antes de que lleguen
    /// los resultados reales.
    /// </summary>
    [ObservableProperty]
    private bool _isSearching;

    /// <summary>Cancela la búsqueda en vuelo anterior cuando llega una nueva (Fase 10,
    /// docs/analisis-proyecto/roadmap.md — "propagar CancellationToken... para cancelar una búsqueda en
    /// vuelo si el usuario seguía tecleando"). El debounce de 200 ms ya evita lanzar una búsqueda por cada
    /// pulsación, pero no evitaba que una búsqueda lenta (vault grande) terminase *después* de una más
    /// reciente y pisara sus resultados con datos obsoletos — este token es lo que de verdad lo resuelve.</summary>
    private CancellationTokenSource? _searchCts;

    public GlobalSearchViewModel(
        VaultService vaultService,
        NoteSearchService noteSearchService,
        KanbanService kanbanService,
        PolarTaskService polarTaskService,
        ReminderService reminderService,
        PolarAuthService polarAuthService,
        Action<GlobalSearchResultViewModel> onResultSelected)
    {
        _vaultService = vaultService;
        _noteSearchService = noteSearchService;
        _kanbanService = kanbanService;
        _polarTaskService = polarTaskService;
        _reminderService = reminderService;
        _polarAuthService = polarAuthService;
        _onResultSelected = onResultSelected;

        try
        {
            var dispatcher = Dispatcher.UIThread;
            if (dispatcher is not null)
            {
                _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(200)
                };
                _searchDebounceTimer.Tick += async (_, _) =>
                {
                    _searchDebounceTimer.Stop();
                    await SearchAsync();
                };
            }
        }
        catch
        {
            // En entornos sin dispatcher (p. ej. tests unitarios) el temporizador simplemente no se crea.
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounceTimer?.Stop();
        _searchDebounceTimer?.Start();
        NotifySearchStateChanged();
    }

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(ShowNoResults));

    /// <summary>
    /// Abre el popup, limpia la búsqueda anterior y deja el campo listo para escribir.
    /// </summary>
    public void Open()
    {
        Results.Clear();
        SearchText = string.Empty;
        IsOpen = true;
        NotifySearchStateChanged();
    }

    /// <summary>
    /// Cierra el popup.
    /// </summary>
    public void Close() => IsOpen = false;

    /// <summary>
    /// Abre el popup si está cerrado o lo cierra si está abierto.
    /// </summary>
    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    /// <summary>
    /// Invoca el callback de selección para un resultado.
    /// </summary>
    public void OpenResult(GlobalSearchResultViewModel result) => _onResultSelected(result);

    private void NotifySearchStateChanged()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowSearchPrompt));
        OnPropertyChanged(nameof(ShowNoResults));
    }

    private async Task SearchAsync()
    {
        // Cancela la búsqueda anterior (si seguía en vuelo) antes de lanzar esta — nunca dos tokens vivos
        // a la vez, así que como mucho una búsqueda obsoleta queda cancelándose de fondo.
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;

        try
        {
            var query = SearchText.Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                Results.Clear();
                NotifySearchStateChanged();
                return;
            }

            IsSearching = true;

            // El .ToList() dentro de cada Task.Run es necesario, no cosmético: Search* devuelve un
            // IEnumerable perezoso (yield return), así que sin materializarlo aquí el recorrido real del
            // vault/tablero/tareas ocurriría al enumerarlo más abajo (SelectMany/OrderBy), es decir en el
            // hilo de UI tras el await — justo el trabajo que Task.Run pretendía sacar de ese hilo.
            var noteTask = Task.Run(() => SearchNotes(query, token).ToList(), token);
            var kanbanTask = Task.Run(() => SearchKanban(query, token).ToList(), token);
            var polarTask = Task.Run(() => SearchPolar(query, token).ToList(), token);
            var reminderTask = Task.Run(() => SearchReminders(query, token).ToList(), token);

            var results = await Task.WhenAll(noteTask, kanbanTask, polarTask, reminderTask);

            var combined = results
                .SelectMany(r => r)
                .OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Results.Clear();
            foreach (var result in combined) Results.Add(result);

            IsSearching = false;
            NotifySearchStateChanged();
        }
        catch (OperationCanceledException)
        {
            // Llegó un tecleo más reciente: esta búsqueda ya no importa. No se toca Results/IsSearching —
            // la búsqueda que la reemplazó es la que decide el estado final del popup.
        }
        catch
        {
            // La búsqueda global no debe romper la app. En caso de fallo inesperado se limpia la
            // lista y se actualiza el estado vacío; el usuario puede intentar de nuevo.
            Results.Clear();
            IsSearching = false;
            NotifySearchStateChanged();
        }
    }

    /// <summary>Notas que casan con la consulta, delegando en <see cref="NoteSearchService"/> — que busca en
    /// el <b>contenido completo</b> de cada nota y en todas las notas del vault, incluidas las que viven
    /// dentro de sesiones.
    ///
    /// <para>Esta parte de la búsqueda global recorría antes <c>GetAllNotesRecursive(VaultPath)</c> y
    /// comparaba contra <see cref="Models.NoteFile.Preview"/>, con dos consecuencias que se notaban a
    /// diario: cualquier texto más allá de los primeros 160 caracteres de una nota no aparecía nunca, y
    /// ninguna nota guardada dentro de una sesión aparecía tampoco, porque ese recorrido excluye
    /// <c>.sessions/</c>.</para></summary>
    private IEnumerable<GlobalSearchResultViewModel> SearchNotes(string query, CancellationToken token)
    {
        foreach (var hit in _noteSearchService.Search(query, token: token))
        {
            token.ThrowIfCancellationRequested();

            yield return new GlobalSearchResultViewModel(
                GlobalSearchResultType.Note,
                hit.Note.FullPath,
                hit.Note.Title,
                BuildSubtitle(hit),
                "NoteTextOutline",
                hit.Note,
                _onResultSelected,
                hit.Field == NoteSearchField.Content ? hit.Offset : null);
        }
    }

    /// <summary>El fragmento del cuerpo donde apareció la consulta cuando el acierto fue de contenido —lo
    /// que de verdad le dice al usuario por qué esta nota está en la lista— y la ruta relativa de la nota
    /// en cualquier otro caso, que es lo que este subtítulo mostraba siempre.</summary>
    private string BuildSubtitle(NoteSearchHit hit)
    {
        if (hit.Field == NoteSearchField.Content && !string.IsNullOrWhiteSpace(hit.Snippet))
            return hit.Snippet;

        return string.IsNullOrWhiteSpace(_vaultService.VaultPath)
            ? hit.Note.FileName
            : Path.GetRelativePath(_vaultService.VaultPath, hit.Note.FullPath);
    }

    private IEnumerable<GlobalSearchResultViewModel> SearchKanban(string query, CancellationToken token)
    {
        foreach (var column in _kanbanService.Board.Columns)
        {
            foreach (var card in column.Cards)
            {
                token.ThrowIfCancellationRequested();

                if (!Matches(card.Title, query)
                    && !Matches(card.Description, query)
                    && !card.Tags.Any(tag => Matches(tag, query)))
                {
                    continue;
                }

                yield return new GlobalSearchResultViewModel(
                    GlobalSearchResultType.KanbanCard,
                    card.Id,
                    card.Title,
                    column.Title,
                    "ViewColumnOutline",
                    card,
                    _onResultSelected);
            }
        }
    }

    private IEnumerable<GlobalSearchResultViewModel> SearchPolar(string query, CancellationToken token)
    {
        if (!_polarAuthService.IsLinked) yield break;

        var tasks = _polarTaskService.Store.Tasks.Where(t => !t.IsDeleted);
        foreach (var task in tasks)
        {
            token.ThrowIfCancellationRequested();

            if (!Matches(task.Title, query)
                && !Matches(task.Description, query)
                && !task.Tags.Any(tag => Matches(tag, query)))
            {
                continue;
            }

            var list = _polarTaskService.Store.Lists.FirstOrDefault(l => l.Id == task.ListId);

            yield return new GlobalSearchResultViewModel(
                GlobalSearchResultType.PolarTask,
                task.Id,
                task.Title,
                list?.Title ?? LocalizationService.Instance["Search_UnknownPolarList"],
                "ListStatus",
                task,
                _onResultSelected);
        }
    }

    private IEnumerable<GlobalSearchResultViewModel> SearchReminders(string query, CancellationToken token)
    {
        foreach (var reminder in _reminderService.ActiveReminders)
        {
            token.ThrowIfCancellationRequested();

            if (!Matches(reminder.Title, query)
                && !Matches(reminder.Description, query))
            {
                continue;
            }

            var localNotifyAt = reminder.NotifyAtUtc.ToLocalTime();
            var culture = LocalizationService.Instance.CurrentCulture;
            var subtitle = localNotifyAt.TimeOfDay == TimeSpan.Zero
                ? localNotifyAt.ToString("dd MMM", culture)
                : localNotifyAt.ToString("dd MMM HH:mm", culture);

            yield return new GlobalSearchResultViewModel(
                GlobalSearchResultType.Reminder,
                reminder.Id,
                reminder.Title,
                subtitle,
                "BellRingOutline",
                reminder,
                _onResultSelected);
        }
    }

    private static bool Matches(string? text, string query) =>
        text is not null && text.Contains(query, StringComparison.OrdinalIgnoreCase);
}
