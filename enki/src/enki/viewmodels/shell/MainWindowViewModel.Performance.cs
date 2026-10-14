using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>Orden en el que se precargan las secciones al arrancar: primero las que el usuario abre
    /// con más frecuencia. La página de inicio elegida se adelanta siempre a esta lista —ver
    /// <see cref="BuildWarmUpSteps"/>, que la coloca la primera—, porque es la que el usuario va a mirar
    /// en cuanto la cortina se retire.</summary>
    private static readonly AppPage[] SectionWarmUpOrder =
    [
        AppPage.Dashboard, AppPage.Notes, AppPage.Kanban, AppPage.Calendar, AppPage.Files,
        AppPage.Habits, AppPage.Sessions, AppPage.Skills, AppPage.Statistics, AppPage.Trash,
        AppPage.TagManager, AppPage.Reminders, AppPage.Notifications, AppPage.Settings
    ];

    /// <summary>Tiempo máximo que se dedica a la precarga de fondo. Pasado este límite se abandona: lo que
    /// falte se construye al abrir su sección, que es como funcionaba la aplicación antes de que existiera
    /// esta precarga. Nunca puede bloquear ni retrasar nada de lo que el usuario ya tiene delante.
    ///
    /// <para>Es un techo generoso a propósito, porque el presupuesto no se gasta en trabajo sino esperando:
    /// entre un tramo caro y el siguiente la precarga se aparta varios segundos (ver
    /// <see cref="BreathAfter"/>), así que un límite corto no repartiría el trabajo, solo lo abandonaría a
    /// medias —y lo que se abandona se acaba construyendo al abrir la sección, que es justo lo que esta
    /// precarga viene a evitar.</para></summary>
    private static readonly TimeSpan SectionWarmUpMaxWait = TimeSpan.FromSeconds(60);

    /// <summary>Un tramo de UI por debajo de este umbral no llega a notarse: el propio dispatcher ya da paso
    /// al compositor entre dos entregas de fondo. Por encima, se le devuelve un fotograma entero antes de
    /// encadenar el siguiente (ver <see cref="RunStepAsync"/>).</summary>
    private const int HeavySliceMilliseconds = 6;

    /// <summary>Calma mínima que tiene que haber desde la última señal del usuario —una tecla, un clic— antes
    /// de meterle un tramo de precarga al hilo de interfaz. Construir una sección entera puede costar más de
    /// un segundo y no puede caerle encima a alguien que ya está usando la aplicación: el único hueco
    /// razonable para ese trabajo es uno en el que no esté haciendo nada.</summary>
    private static readonly TimeSpan WarmUpQuietPeriod = TimeSpan.FromMilliseconds(400);

    /// <summary>Cadencia con la que se reintenta un tramo que todavía no encontró su hueco de calma.</summary>
    private static readonly TimeSpan WarmUpQuietPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Cuánto se aparta la precarga de fondo por cada milisegundo que le robó al hilo de interfaz.
    ///
    /// <para>Es la pieza que faltaba para que el arranque no se leyera a tirones. Con solo la calma de
    /// <see cref="WarmUpQuietPeriod"/> la precarga esperaba a que el usuario dejara de teclear y entonces
    /// encadenaba sección tras sección —Notas 851 ms, Kanban 993 ms, Ajustes 1100 ms— con un solo fotograma
    /// entre medias: cuatro segundos de aplicación congelada de una tirada, justo cuando el usuario acababa
    /// de empezar a usarla. Devolviendo varias veces lo que se le quitó, cada tramo caro queda seguido de un
    /// hueco en el que la aplicación responde con normalidad, y esos huecos son los que el usuario percibe
    /// como que todo va bien.</para></summary>
    private const int WarmUpBreathFactor = 8;

    /// <summary>Techo del respiro de <see cref="BreathAfter"/>. Sin él, un tramo de un segundo deja la
    /// precarga parada ocho y las últimas secciones no llegarían a prepararse antes de que el usuario las
    /// abra.</summary>
    private static readonly TimeSpan WarmUpBreathMax = TimeSpan.FromSeconds(4);

    /// <summary>Un tramo de la precarga: qué sección prepara, con qué texto se anuncia y el trabajo en sí.
    /// Un mismo tipo de trabajo partido en dos tramos —construir la vista y cargar sus datos— en vez de uno
    /// largo, porque un solo tramo largo es exactamente lo que deja a tirones la pantalla de arranque.</summary>
    private readonly record struct WarmUpStep(AppPage Page, string Status, Action Run);

    /// <summary>Ticks UTC de la última señal de que el usuario está usando la aplicación (una tecla, un
    /// clic). La precarga de fondo espera a que pase <see cref="WarmUpQuietPeriod"/> desde aquí antes de
    /// meterle un tramo al hilo de interfaz — ver <see cref="RunSectionWarmUpAsync"/>. Se escribe desde los
    /// manejadores de entrada de <c>MainWindow</c> y se lee desde aquí, por eso va con <see cref="Interlocked"/>.
    /// Arranca en el momento de construir el ViewModel para que una aplicación recién abierta no tenga que
    /// esperar a la primera tecla para considerarse en calma.</summary>
    private long _lastInteractionTicks = DateTime.UtcNow.Ticks;

    /// <summary>Momento (UTC) de la última señal del usuario.</summary>
    private DateTime LastInteractionUtc => new(Interlocked.Read(ref _lastInteractionTicks), DateTimeKind.Utc);

    private readonly HashSet<AppPage> _dirtyPages = [];
    private readonly DispatcherTimer _pageRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _pageRefreshQueued;
    private int _vaultInvalidationQueued;
    private DateOnly _pageRefreshDate = DateOnly.FromDateTime(DateTime.Today);

    /// <summary>Generación de la precarga en curso. Cada arranque la incrementa, así que un tramo que se
    /// quede esperando (al hueco libre del hilo de UI, o a un fotograma) mientras otro arranque toma el
    /// relevo se descarta al volver en vez de seguir pisando estado ajeno.</summary>
    private int _warmUpGeneration;

    /// <summary>True mientras se construyen y cargan las secciones al arrancar. <c>MainWindow</c> tapa el
    /// shell con la pantalla de arranque para que el usuario no vea vistas a medio cargar.</summary>
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
        _vaultSettingsService.SettingsChanged += OnVaultSettingsChanged;
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
    private void OnVaultSettingsChanged() => InvalidatePages(AppPage.Dashboard);

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
    /// marcadas, una por paso.</summary>
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

    /// <summary>Recarga los datos de una sección y la retira de las pendientes.
    ///
    /// <para>Salta sin más si esa sección todavía no existe: cualquier evento de servicio puede marcar
    /// páginas como sucias (un cambio de vault, un guardado, una ráfaga de un sincronizador) y no todas las
    /// secciones tienen por qué estar construidas —en un arranque sin vault configurado no lo está ninguna—,
    /// así que marcarlas no puede acabar en un puntero nulo aquí. La página se retira igual, que es lo que
    /// impide que se vuelva a programar su recarga en bucle.</summary>
    private void ReloadPage(AppPage page)
    {
        _dirtyPages.Remove(page);
        if (ViewModelFor(page) is null) return;

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

    /// <summary>Arranca la precarga de secciones con la pantalla de arranque puesta (ver <c>MainWindow</c>).
    /// El trabajo real vive en <see cref="RunSectionWarmUpAsync"/>, que es asíncrono: la firma de aquí se
    /// queda síncrona para que la composición de la shell no tenga que esperar a nada.</summary>
    private void StartSectionWarmUp(AppPage startupPage) => _ = RunSectionWarmUpAsync(startupPage);

    /// <summary>Precarga las secciones en dos fases, y la frontera entre ambas es lo que hace que el arranque
    /// se sienta fluido:
    ///
    /// <list type="number">
    /// <item><b>Índice del vault, en un hilo de trabajo.</b> Es la fase más larga de un vault grande y es
    /// E/S pura (<c>VaultNoteCache</c> es seguro entre hilos). El hilo de UI se queda libre, así que la
    /// pantalla de arranque se anima sin un solo tirón mientras dura, y los tramos de después encuentran
    /// las notas ya en memoria y resultan mucho más baratos.</item>
    /// <item><b>La página de inicio, y con ella se retira la cortina.</b> Se construye y carga solo la
    /// sección que el usuario va a ver, así que la aplicación queda usable sin esperar a las demás. Antes
    /// se tapaba la ventana hasta tener las catorce secciones listas, y eso es justo lo que convertía el
    /// arranque en una espera larga.</item>
    /// <item><b>El resto, en los huecos libres del hilo de UI.</b> A partir de ahí la primera sección que
    /// abra cada quien ya suele estar construida, pero si no lo está se construye al abrirla (es lo que
    /// <c>SectionHost</c> hace siempre) y su entrada se anuncia con el fundido de <c>MotionReveal</c>. En
    /// ningún caso se compite con lo que el usuario está haciendo: cada tramo espera a prioridad de reposo y
    /// a que hayan pasado unos cientos de milisegundos desde su última tecla o clic, así que la precarga se
    /// aparta mientras él trabaja y retoma sola en la siguiente pausa.</item>
    /// </list>
    ///
    /// <para>Cada tramo de UI va seguido de un fotograma entero si ha durado lo bastante como para notarse
    /// (ver <see cref="RunStepAsync"/>): es la otra mitad de que la animación no vaya a tirones.</para></summary>
    private async Task RunSectionWarmUpAsync(AppPage startupPage)
    {
        if (_shutdownCompleted) return;
        var generation = ++_warmUpGeneration;

        // El arranque ya carga todas las secciones; las pendientes de antes no hacen falta.
        _dirtyPages.Clear();
        IsSectionWarmUpActive = true;
        var warmUp = Stopwatch.StartNew();

        try
        {
            await Task.Run(_vaultService.GetAllNotesAcrossVaultWithContent).ConfigureAwait(true);
            if (_shutdownCompleted || generation != _warmUpGeneration) return;
            LogService.Debug($"[arranque] índice del vault: {warmUp.ElapsedMilliseconds} ms en segundo plano");

            var steps = BuildWarmUpSteps(startupPage);
            var total = steps.Count;
            var index = 0;
            var waited = Stopwatch.StartNew();

            bool StillCurrent() => !_shutdownCompleted && generation == _warmUpGeneration;

            // Momento a partir del cual se permite el siguiente tramo de fondo, más allá de la calma del
            // usuario: ver BreathAfter. Arranca en el pasado, porque el primero no tiene nada que devolver.
            var nextStepAllowedUtc = DateTime.UtcNow;

            // ===================== Fase 2: la sección de inicio =====================
            while (index < total && steps[index].Page == startupPage && StillCurrent())
            {
                await RunStepAsync(steps[index]);
                if (!StillCurrent()) return;
                index++;
            }

            // El dashboard también completa sus recuentos y notas en segundo plano: espera su resultado
            // para que el fundido revele el contenido definitivo, no un resumen que se rellena después.
            if (startupPage == AppPage.Dashboard && Dashboard is not null)
                await Dashboard.PendingVaultLoad.WaitAsync(SectionWarmUpMaxWait);

            // Estadísticas lee el vault en segundo plano: si es la página de inicio, es justo la pantalla
            // que el usuario está mirando, así que aquí sí se espera a que termine (con límite). Para
            // cualquier otra página de inicio no tiene sentido esperar por una sección que no se ve.
            if (startupPage == AppPage.Statistics && Statistics?.IsLoading == true)
            {
                while (Statistics.IsLoading && waited.Elapsed < SectionWarmUpMaxWait && StillCurrent())
                    await Task.Delay(30);
            }

            if (!StillCurrent()) return;

            // La vista puede conservar la cortina hasta completar sus dos segundos mínimos.
            IsSectionWarmUpActive = false;
            QueueCurrentPageRefresh();
            LogService.Debug($"[arranque] pantalla de arranque retirada a los {warmUp.ElapsedMilliseconds} ms "
                + $"({index}/{total} tramos)");

            // ===================== Fase 3: el resto, ya sin cortina y en los huecos libres =====================
            // Construir una sección cuesta cientos de milisegundos de hilo de interfaz, y eso solo es
            // aceptable en un hueco en el que el usuario no esté haciendo nada. Cada tramo espera a que el
            // dispatcher esté vacío y a que hayan pasado unos cientos de milisegundos desde la última tecla o
            // clic: si el usuario se pone a trabajar, la precarga no compite — se aparta y espera a su
            // siguiente pausa. Lo que no llegue a prepararse se construye al abrir su sección, como funcionaba
            // antes de que existiera esta precarga.
            // Deja terminar la permanencia mínima y el fundido antes de construir más vistas: arrancar
            // aquí inmediatamente robaba el hilo de UI a la propia animación de salida.
            await Task.Delay(StartupGate.MinimumDisplayDuration);
            while (index < total && StillCurrent() && waited.Elapsed < SectionWarmUpMaxWait)
            {
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                if (!StillCurrent()) break;

                // El tramo anterior costó lo suyo: se le devuelve al hilo de interfaz un múltiplo de ese
                // tiempo antes de volver a interrumpirlo (ver WarmUpBreathFactor). Es lo que impide que la
                // precarga se lea como una aplicación atascada en vez de como trabajo de fondo.
                if (DateTime.UtcNow < nextStepAllowedUtc)
                {
                    await Task.Delay(WarmUpQuietPollInterval);
                    continue;
                }

                if (DateTime.UtcNow - LastInteractionUtc < WarmUpQuietPeriod)
                {
                    await Task.Delay(WarmUpQuietPollInterval);
                    continue;
                }

                var breath = BreathAfter(await RunStepAsync(steps[index]));
                index++;
                nextStepAllowedUtc = DateTime.UtcNow + breath;
            }

            LogService.Debug($"[arranque] precarga terminada: {index}/{total} tramos en {warmUp.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            // Una sección que falle no puede dejar la cortina puesta: se registra y se muestra la shell.
            LogService.Warning("No se pudo precargar una sección al arrancar", ex);
        }
        finally
        {
            if (generation == _warmUpGeneration)
            {
                IsSectionWarmUpActive = false;
                QueueCurrentPageRefresh();
            }
        }
    }

    /// <summary>Avisa de que el usuario acaba de tocar algo —una tecla o un clic—, que es lo único que
    /// necesita saber la precarga de fondo para saber cuándo no debe trabajar (ver
    /// <see cref="RunSectionWarmUpAsync"/>). Lo llama <c>MainWindow</c> desde sus manejadores de entrada: es
    /// donde se sabe de verdad, sin cronometrar una heurística de inactividad.</summary>
    public void NoteUserInteraction() => Interlocked.Exchange(ref _lastInteractionTicks, DateTime.UtcNow.Ticks);

    /// <summary>Ejecuta un tramo y, si ha durado lo bastante como para que el compositor lo note, le
    /// devuelve un fotograma entero antes de encadenar el siguiente. Un tramo corto no lo necesita: el
    /// dispatcher ya da paso a las tareas de render entre dos entregas de prioridad Background. El coste del
    /// fotograma es acotado y solo se paga cuando de verdad había algo que perder.</summary>
    /// <summary>Ejecuta un tramo y devuelve lo que tardó en el hilo de interfaz, que es lo que decide
    /// cuánto tiene que esperar la precarga antes del siguiente (ver <see cref="BreathAfter"/>).</summary>
    private static async Task<long> RunStepAsync(WarmUpStep step)
    {
        var slice = Stopwatch.StartNew();
        try
        {
            step.Run();
        }
        catch (Exception ex)
        {
            // Una sección que falle no debe dejar el velo puesto: se registra y se sigue con la siguiente.
            LogService.Warning("No se pudo precargar una sección al arrancar", ex);
        }

        var elapsed = slice.ElapsedMilliseconds;
        // Un arranque que va lento tiene que poder diagnosticarse sin instrumentar nada a mano: qué sección
        // se llevó el tiempo es la primera pregunta, y es la única que no se puede deducir después.
        if (elapsed >= HeavySliceMilliseconds)
            LogService.Debug($"[arranque] {step.Page} ({step.Status}): {elapsed} ms en el hilo de UI");

        // Un tramo por encima del presupuesto deja la animación a medias: se le devuelve un fotograma entero
        // al compositor antes de encadenar el siguiente.
        if (elapsed >= HeavySliceMilliseconds) await Task.Delay(1);
        return elapsed;
    }

    /// <summary>Respiro que se le concede al hilo de interfaz después de un tramo de precarga que costó
    /// <paramref name="costMilliseconds"/>. Un tramo corto no necesita nada —el propio dispatcher da paso al
    /// compositor entre dos entregas de fondo—, así que solo los caros pagan espera, y la pagan proporcional
    /// a lo que robaron.</summary>
    private static TimeSpan BreathAfter(long costMilliseconds) => costMilliseconds < HeavySliceMilliseconds
        ? TimeSpan.Zero
        : TimeSpan.FromMilliseconds(Math.Min(costMilliseconds * WarmUpBreathFactor, WarmUpBreathMax.TotalMilliseconds));

    /// <summary>Convierte "las secciones a preparar" en "los tramos a ejecutar": la página de inicio la
    /// primera, y cada sección partida en dos —construir su vista y cargar sus datos— para que ningún tramo
    /// de hilo de UI sea el doble de largo de lo necesario.</summary>
    private List<WarmUpStep> BuildWarmUpSteps(AppPage startupPage)
    {
        var order = new List<AppPage>(SectionWarmUpOrder.Length + 1) { startupPage };
        foreach (var page in SectionWarmUpOrder)
            if (page != startupPage) order.Add(page);

        var steps = new List<WarmUpStep>(order.Count * 2);
        foreach (var page in order)
        {
            if (ViewModelFor(page) is not { } target) continue;
            var status = LocalizedTextFormatter.Resolve(LocalizedTextFormatter.Format(
                "Startup_PreparingSection", LocalizationService.Instance[SectionLabelKey(page)]));

            steps.Add(new WarmUpStep(page, status, () => SectionPreloadRequested?.Invoke(target)));
            steps.Add(new WarmUpStep(page, status, () => LoadSectionForWarmUp(page)));
        }

        return steps;
    }

    /// <summary>Rótulo con el que se anuncia una sección en la pantalla de arranque. Se toma del mismo
    /// catálogo que arma la barra lateral —la clave es el nombre del valor de <see cref="AppPage"/>, que es
    /// como están indexadas ambas cosas— para no mantener una segunda lista de rótulos. Ajustes es el único
    /// que no vive en ningún grupo, así que cae a su propia etiqueta.</summary>
    private static string SectionLabelKey(AppPage page) =>
        SidebarSectionCatalog.Meta.TryGetValue(page.ToString(), out var meta) ? meta.LabelKey : "Nav_SettingsLabel";

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
        _vaultSettingsService.SettingsChanged -= OnVaultSettingsChanged;
        _dirtyPages.Clear();
        // Un tramo de precarga que vuelva después de esto se descarta en vez de seguir trabajando.
        _warmUpGeneration++;
    }
}
