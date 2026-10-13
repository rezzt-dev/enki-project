using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>Orden en el que se precargan las secciones al arrancar: primero las que el usuario abre
    /// con más frecuencia, para que el progreso del velo se note desde el principio.</summary>
    private static readonly AppPage[] SectionWarmUpOrder =
    [
        AppPage.Dashboard, AppPage.Notes, AppPage.Kanban, AppPage.Calendar, AppPage.Files,
        AppPage.Habits, AppPage.Sessions, AppPage.Skills, AppPage.Statistics, AppPage.Trash,
        AppPage.TagManager, AppPage.Reminders, AppPage.Notifications, AppPage.Settings
    ];

    /// <summary>Tiempo máximo que el velo de arranque espera a las lecturas en segundo plano de Estadísticas.
    /// Pasado este límite se muestra la app aunque esa lectura siga en curso, para no bloquearla nunca.</summary>
    private static readonly TimeSpan SectionWarmUpMaxWait = TimeSpan.FromSeconds(15);

    private readonly HashSet<AppPage> _dirtyPages = [];
    private readonly DispatcherTimer _pageRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _pageRefreshQueued;
    private int _vaultInvalidationQueued;
    private DateOnly _pageRefreshDate = DateOnly.FromDateTime(DateTime.Today);
    private Queue<Action>? _sectionWarmUpSteps;

    /// <summary>True mientras se construyen y cargan las secciones al arrancar. <c>MainWindow</c> tapa el
    /// shell con un velo para que el usuario no vea vistas a medio cargar.</summary>
    [ObservableProperty]
    private bool _isSectionWarmUpActive;

    /// <summary>Pide a <c>MainWindow</c> que construya la vista de una sección sin mostrarla
    /// (ver <c>SectionHost.Preload</c>). Un evento y no una referencia directa porque la vista
    /// vive en la capa de UI y este ViewModel no debe conocerla.</summary>
    public event Action<ViewModelBase>? SectionPreloadRequested;

    /// <summary>Los cambios invalidan proyecciones, no reconstruyen todas las pantallas. Una ráfaga de
    /// guardados se agrupa y las secciones afectadas se actualizan en segundo plano, la visible primero.</summary>
    private void InitializePageRefresh()
    {
        _pageRefreshTimer.Tick += (_, _) =>
        {
            _pageRefreshTimer.Stop();
            QueueCurrentPageRefresh();
        };
        _vaultService.ContentChanged += OnVaultContentChanged;
        _vaultChangeWatcher.ContentsChanged += OnVaultContentChanged;
        _kanbanService.BoardChanged += OnBoardDataChanged;
        _polarTaskService.DataChanged += OnPolarDataChanged;
        _polarAuthService.LinkStateChanged += OnPolarDataChanged;
        _habitService.HabitsChanged += OnHabitDataChanged;
        _reminderService.RemindersChanged += OnReminderDataChanged;
    }

    private void OnVaultContentChanged()
    {
        // FileSystemWatcher llama desde el pool: una sola entrega a la UI por lote, incluso cuando
        // un sincronizador importa cientos de archivos. No se toca ninguna colección desde ese hilo.
        if (Interlocked.Exchange(ref _vaultInvalidationQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _vaultInvalidationQueued, 0);
            InvalidatePages(AppPage.Dashboard, AppPage.Files, AppPage.Sessions, AppPage.Trash,
                AppPage.TagManager, AppPage.Statistics, AppPage.Skills);
        }, DispatcherPriority.Background);
    }

    private void OnBoardDataChanged() => InvalidatePages(AppPage.Kanban, AppPage.Dashboard,
        AppPage.Statistics, AppPage.TagManager, AppPage.Trash, AppPage.Sessions);
    private void OnPolarDataChanged() => InvalidatePages(AppPage.Dashboard, AppPage.Statistics,
        AppPage.TagManager, AppPage.Trash);
    private void OnHabitDataChanged() => InvalidatePages(AppPage.Habits, AppPage.Dashboard, AppPage.Statistics);
    private void OnReminderDataChanged() => InvalidatePages(AppPage.Dashboard, AppPage.Statistics, AppPage.Trash);

    private void InvalidatePages(params AppPage[] pages)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => InvalidatePages(pages), DispatcherPriority.Background);
            return;
        }
        if (_shutdownCompleted) return;
        foreach (var page in pages) _dirtyPages.Add(page);
        if (!_pageRefreshTimer.IsEnabled) _pageRefreshTimer.Start();
    }

    /// <summary>Programa el vaciado de páginas pendientes en segundo plano. Cada paso recarga una sola sección
    /// y deja libre el hilo de UI entre pasos, así que un clic del usuario nunca espera a más de una recarga.</summary>
    private void QueueCurrentPageRefresh()
    {
        if (_pageRefreshQueued || _shutdownCompleted) return;
        _pageRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pageRefreshQueued = false;
            if (_shutdownCompleted) return;
            RefreshNextDirtyPage();
        }, DispatcherPriority.Background);
    }

    /// <summary>Recarga primero la página visible si está pendiente y, después, el resto de páginas
    /// marcadas, una por paso. Durante el arranque el planificador de precarga ya recarga las secciones,
    /// así que este método solo atiende las invalidaciones posteriores.</summary>
    private void RefreshNextDirtyPage()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (today != _pageRefreshDate)
        {
            _pageRefreshDate = today;
            _dirtyPages.UnionWith([AppPage.Dashboard, AppPage.Habits, AppPage.Statistics, AppPage.Kanban]);
        }

        var current = CurrentAppPage();
        AppPage? next = current is not null && _dirtyPages.Contains(current.Value)
            ? current
            : _dirtyPages.Count > 0 ? _dirtyPages.First() : null;
        if (next is null) return;

        ReloadPage(next.Value);
        if (_dirtyPages.Count > 0) QueueCurrentPageRefresh();
    }

    private AppPage? CurrentAppPage() => CurrentPage switch
    {
        DashboardViewModel => AppPage.Dashboard,
        FilesViewModel => AppPage.Files,
        SessionsViewModel => AppPage.Sessions,
        SkillsViewModel => AppPage.Skills,
        KanbanViewModel => AppPage.Kanban,
        HabitsViewModel => AppPage.Habits,
        StatisticsViewModel => AppPage.Statistics,
        TrashViewModel => AppPage.Trash,
        TagManagerViewModel => AppPage.TagManager,
        _ => null
    };

    /// <summary>Recarga los datos de una sección y la retira de las pendientes.</summary>
    private void ReloadPage(AppPage page)
    {
        _dirtyPages.Remove(page);
        switch (page)
        {
            case AppPage.Dashboard: Dashboard!.Reload(); break;
            case AppPage.Files: Files!.Refresh(); break;
            case AppPage.Sessions: Sessions!.Reload(); break;
            case AppPage.Skills: Skills!.Reload(); break;
            case AppPage.Kanban: Kanban!.Reload(); break;
            case AppPage.Habits: Habits!.Reload(); break;
            case AppPage.Statistics: Statistics!.Reload(); break;
            case AppPage.Trash: Trash!.Reload(); UpdateTrashBadge(); break;
            case AppPage.TagManager: TagManager!.Reload(); break;
        }
    }

    /// <summary>Crea la vista y carga los datos de cada sección al arrancar, en pasos de segundo plano, con
    /// el velo <see cref="IsSectionWarmUpActive"/> encima. Al terminar, cualquier sección abierta después
    /// aparece ya construida y con sus datos.</summary>
    private void StartSectionWarmUp()
    {
        if (_shutdownCompleted) return;

        // El arranque ya carga todas las secciones; las pendientes de antes no hacen falta.
        _dirtyPages.Clear();
        var steps = new Queue<Action>();
        foreach (var page in SectionWarmUpOrder)
        {
            var target = ViewModelFor(page);
            if (target is null) continue;
            steps.Enqueue(() =>
            {
                SectionPreloadRequested?.Invoke(target);
                LoadSectionForWarmUp(page);
            });
        }

        _sectionWarmUpSteps = steps;
        IsSectionWarmUpActive = true;
        var waited = Stopwatch.StartNew();
        Dispatcher.UIThread.Post(() => RunNextSectionWarmUpStep(waited), DispatcherPriority.Background);
    }

    private void RunNextSectionWarmUpStep(Stopwatch waited)
    {
        if (_shutdownCompleted || _sectionWarmUpSteps is null) return;

        if (_sectionWarmUpSteps.Count > 0)
        {
            var step = _sectionWarmUpSteps.Dequeue();
            try
            {
                step();
            }
            catch (Exception ex)
            {
                // Una sección que falle no debe dejar el velo puesto: se registra y se sigue con la siguiente.
                LogService.Warning("No se pudo precargar una sección al arrancar", ex);
            }
            Dispatcher.UIThread.Post(() => RunNextSectionWarmUpStep(waited), DispatcherPriority.Background);
            return;
        }

        // Estadísticas lee el vault en segundo plano: el velo espera a que termine, con un límite.
        if (Statistics?.IsLoading == true && waited.Elapsed < SectionWarmUpMaxWait)
        {
            _ = Task.Delay(30).ContinueWith(_ =>
                Dispatcher.UIThread.Post(() => RunNextSectionWarmUpStep(waited), DispatcherPriority.Background));
            return;
        }

        _sectionWarmUpSteps = null;
        IsSectionWarmUpActive = false;
        QueueCurrentPageRefresh();
    }

    /// <summary>Carga los datos de una sección durante el arranque. Notas y Recordatorios/Notificaciones
    /// no tienen recarga de pestaña, así que se les pide directamente lo que el cambio de vault ya hace.</summary>
    private void LoadSectionForWarmUp(AppPage page)
    {
        switch (page)
        {
            case AppPage.Notes:
                Notes!.Reload();
                Notes.ReloadSessions();
                break;
            case AppPage.Reminders: Reminders?.Reload(); break;
            case AppPage.Notifications: Notifications?.Reload(); break;
            case AppPage.Calendar: Calendar?.ReloadPreferences(); break;
            default: ReloadPage(page); break;
        }
    }

    /// <summary>Resuelve el ViewModel que muestra cada página de la barra lateral.</summary>
    private ViewModelBase? ViewModelFor(AppPage page) => page switch
    {
        AppPage.Dashboard => Dashboard,
        AppPage.Calendar => Calendar,
        AppPage.Notes => Notes,
        AppPage.Files => Files,
        AppPage.Skills => Skills,
        AppPage.Sessions => Sessions,
        AppPage.Kanban => Kanban,
        AppPage.Trash => Trash,
        AppPage.TagManager => TagManager,
        AppPage.Settings => Settings,
        AppPage.Habits => Habits,
        AppPage.Statistics => Statistics,
        AppPage.Reminders => Reminders,
        AppPage.Notifications => Notifications,
        _ => Dashboard
    };

    private void StopPageRefresh()
    {
        _pageRefreshTimer.Stop();
        _vaultService.ContentChanged -= OnVaultContentChanged;
        _vaultChangeWatcher.ContentsChanged -= OnVaultContentChanged;
        _kanbanService.BoardChanged -= OnBoardDataChanged;
        _polarTaskService.DataChanged -= OnPolarDataChanged;
        _polarAuthService.LinkStateChanged -= OnPolarDataChanged;
        _habitService.HabitsChanged -= OnHabitDataChanged;
        _reminderService.RemindersChanged -= OnReminderDataChanged;
        _dirtyPages.Clear();
        _sectionWarmUpSteps = null;
    }
}
