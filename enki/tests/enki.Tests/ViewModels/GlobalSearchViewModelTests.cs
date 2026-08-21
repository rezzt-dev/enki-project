using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="GlobalSearchViewModel"/>: búsqueda en paralelo sobre notas, tarjetas
/// Kanban, tareas Polar y recordatorios, ordenamiento, estado vacío y exclusión de Polar cuando no
/// está vinculado.</summary>
public class GlobalSearchViewModelTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultDirectory;
    private readonly SettingsService _settings;
    private readonly VaultService _vault;
    private readonly KanbanService _kanban;
    private readonly PolarTaskService _polarTasks;
    private readonly ReminderService _reminders;
    private readonly PolarAuthService _polarAuth;
    private GlobalSearchResultViewModel? _selectedResult;

    static GlobalSearchViewModelTests()
    {
        // PolarAuthService.IsLinked depende de SupabaseConfig.IsConfigured, que lee local.properties
        // en AppContext.BaseDirectory. En tests que necesitan Polar vinculado aseguramos un archivo
        // dummy para que IsConfigured sea true; las credenciales reales no se usan nunca.
        var localProperties = Path.Combine(AppContext.BaseDirectory, "local.properties");
        if (!File.Exists(localProperties))
        {
            File.WriteAllText(
                localProperties,
                "SUPABASE_URL=https://example.supabase.co\nSUPABASE_ANON_KEY=dummy-test-key\n");
        }
    }

    public GlobalSearchViewModelTests()
    {
        Directory.CreateDirectory(_storageDirectory);
        _vaultDirectory = Path.Combine(_storageDirectory, "vault");
        Directory.CreateDirectory(_vaultDirectory);

        _settings = new SettingsService(_storageDirectory);
        _vault = new VaultService(_settings);
        _vault.InitializeVault(_vaultDirectory, seedWelcomeNote: false);
        _kanban = new KanbanService(_storageDirectory);
        _polarTasks = new PolarTaskService(_storageDirectory);
        _reminders = new ReminderService(_storageDirectory);

        _settings.Settings.PolarLinked = true;
        _settings.Save();
        _polarAuth = new PolarAuthService(_settings);

        Assert.True(_polarAuth.IsLinked);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private GlobalSearchViewModel CreateViewModel() =>
        new(_vault, _kanban, _polarTasks, _reminders, _polarAuth, r => _selectedResult = r);

    private static async Task RunSearchAsync(GlobalSearchViewModel vm)
    {
        var method = typeof(GlobalSearchViewModel).GetMethod(
            "SearchAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method.Invoke(vm, null)!;
        await task;
    }

    [Fact]
    public async Task SearchAsync_FindsResult_FromEachSource()
    {
        var notePath = Path.Combine(_vaultDirectory, "nota-global.md");
        File.WriteAllText(
            notePath,
            "# Nota global\n\nTexto de prueba.\n\n---\ntags: [global]\n---");

        _kanban.Board.Columns[0].Cards.Add(new KanbanCard
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "Tarjeta global",
            Description = "Descripción de la tarjeta"
        });
        _kanban.Save();

        var list = _polarTasks.CreateList("Lista global", "ic_list", "#7F52FF", false);
        _polarTasks.CreateTask(
            list, "Tarea global", "Detalle de la tarea", 1, null, PolarRecurrence.None, 0, ["global"]);

        _reminders.Create("Recordatorio global", "Detalle", DateTime.UtcNow.AddDays(1), "BellRingOutline");

        var vm = CreateViewModel();
        vm.SearchText = "global";
        await RunSearchAsync(vm);

        Assert.Equal(4, vm.Results.Count);
        Assert.Contains(vm.Results, r => r.Type == GlobalSearchResultType.Note);
        Assert.Contains(vm.Results, r => r.Type == GlobalSearchResultType.KanbanCard);
        Assert.Contains(vm.Results, r => r.Type == GlobalSearchResultType.PolarTask);
        Assert.Contains(vm.Results, r => r.Type == GlobalSearchResultType.Reminder);
        Assert.True(vm.HasResults);
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task SearchAsync_SortsResultsByTitle()
    {
        File.WriteAllText(
            Path.Combine(_vaultDirectory, "a-nota.md"),
            "# A nota\n\nCoincide.");

        _kanban.Board.Columns[0].Cards.Add(new KanbanCard
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "B tarjeta",
            Description = "Coincide"
        });
        _kanban.Save();

        var list = _polarTasks.CreateList("Lista", "ic_list", "#7F52FF", false);
        _polarTasks.CreateTask(
            list, "C tarea", "Coincide", 1, null, PolarRecurrence.None, 0, []);

        _reminders.Create("D recordatorio", "Coincide", DateTime.UtcNow.AddDays(1), "BellRingOutline");

        var vm = CreateViewModel();
        vm.SearchText = "Coincide";
        await RunSearchAsync(vm);

        var titles = vm.Results.Select(r => r.Title).ToList();
        Assert.Equal(["A nota", "B tarjeta", "C tarea", "D recordatorio"], titles);
    }

    [Fact]
    public async Task SearchAsync_NoMatches_LeavesResultsEmptyAndIsEmptyTrue()
    {
        var vm = CreateViewModel();
        vm.SearchText = "xyz-nothing";
        await RunSearchAsync(vm);

        Assert.Empty(vm.Results);
        Assert.False(vm.HasResults);
        Assert.True(vm.IsEmpty);
        Assert.True(vm.ShowNoResults);
    }

    [Fact]
    public async Task SearchAsync_ExcludesPolar_WhenNotLinked()
    {
        _settings.Settings.PolarLinked = false;
        _settings.Save();
        var polarAuthNotLinked = new PolarAuthService(_settings);
        Assert.False(polarAuthNotLinked.IsLinked);

        File.WriteAllText(
            Path.Combine(_vaultDirectory, "nota.md"),
            "# Nota visible\n\nCoincide.");

        var list = _polarTasks.CreateList("Lista", "ic_list", "#7F52FF", false);
        _polarTasks.CreateTask(
            list, "Tarea oculta", "Coincide", 1, null, PolarRecurrence.None, 0, []);

        var vm = new GlobalSearchViewModel(
            _vault, _kanban, _polarTasks, _reminders, polarAuthNotLinked, r => _selectedResult = r);
        vm.SearchText = "Coincide";
        await RunSearchAsync(vm);

        Assert.Single(vm.Results);
        Assert.Equal(GlobalSearchResultType.Note, vm.Results[0].Type);
        Assert.DoesNotContain(vm.Results, r => r.Type == GlobalSearchResultType.PolarTask);
    }

    [Fact]
    public async Task SearchAsync_FindsByTag()
    {
        File.WriteAllText(
            Path.Combine(_vaultDirectory, "nota-con-tag.md"),
            "---\ntags: [etiqueta-global]\n---\n\n# Nota con tag\n\nCuerpo.");

        _kanban.Board.Columns[0].Cards.Add(new KanbanCard
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "Tarjeta con tag",
            Tags = ["etiqueta-global"]
        });
        _kanban.Save();

        var vm = CreateViewModel();
        vm.SearchText = "etiqueta-global";
        await RunSearchAsync(vm);

        Assert.Equal(2, vm.Results.Count);
    }

    /// <summary>Fase 10 (docs/analisis-proyecto/roadmap.md — "propagar CancellationToken... para cancelar
    /// una búsqueda en vuelo si el usuario seguía tecleando"). Comprueba el mecanismo exacto en vez de una
    /// carrera end-to-end: <c>SearchAsync</c> cancela el <c>CancellationTokenSource</c> anterior de forma
    /// síncrona, antes de su primer <c>await</c> — así que el token de la primera llamada ya está cancelado
    /// en el instante en que <c>Invoke</c> de la segunda devuelve el control, sin depender de en qué hilo
    /// resuma cada continuación (una carrera real sobre <see cref="GlobalSearchViewModel.Results"/> no sería
    /// determinista aquí: a diferencia de la app real, este proceso de test no tiene un
    /// <see cref="System.Threading.SynchronizationContext"/> de UI que serialice las dos fusiones).</summary>
    [Fact]
    public async Task SearchAsync_NewerCall_CancelsThePreviousInFlightToken()
    {
        var method = typeof(GlobalSearchViewModel).GetMethod("SearchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ctsField = typeof(GlobalSearchViewModel).GetField("_searchCts", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var vm = CreateViewModel();
        vm.SearchText = "primera";
        var firstTask = (Task)method.Invoke(vm, null)!;
        var firstCts = Assert.IsType<System.Threading.CancellationTokenSource>(ctsField.GetValue(vm));

        vm.SearchText = "segunda";
        var secondTask = (Task)method.Invoke(vm, null)!;

        Assert.True(firstCts.IsCancellationRequested);

        await Task.WhenAll(firstTask, secondTask);
    }

    [Fact]
    public void OpenResult_InvokesCallback()
    {
        var vm = CreateViewModel();
        var result = vm.Results.FirstOrDefault();
        if (result is null)
        {
            // Si no hay resultados previos, creamos uno artificial para probar el callback.
            result = new GlobalSearchResultViewModel(
                GlobalSearchResultType.Note,
                "id",
                "Título",
                "Subtítulo",
                "NoteTextOutline",
                null,
                r => _selectedResult = r);
        }

        vm.OpenResult(result);

        Assert.Same(result, _selectedResult);
    }
}
