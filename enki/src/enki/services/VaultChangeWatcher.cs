using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using enki.Models;

namespace enki.Services;

/// <summary>Vigila el vault para enterarse de los cambios que otro programa —Syncthing, Dropbox, otro
/// editor, un script— hace sobre las notas que Enki tiene abiertas, y los publica por
/// <see cref="ExternalChangeDetected"/>. Ver <c>.docs/project-info/cambios-externos-en-el-vault.md</c>.
///
/// <para><b>Solo las notas registradas.</b> El <see cref="FileSystemWatcher"/> cubre el vault entero, pero
/// solo se evalúan las rutas que alguien ha registrado con <see cref="Track"/> — hoy, las pestañas
/// abiertas del editor, que son las únicas con contenido en memoria que un cambio ajeno podría pisar. Una
/// nota que no está abierta no necesita vigilancia: nadie la tiene cargada, y el siguiente escaneo del
/// vault ya la relee sola, porque <see cref="VaultNoteCache"/> compara la fecha de modificación.</para>
///
/// <para><b>Cómo se ignoran las escrituras propias.</b> No por ventanas de tiempo ni listas de
/// "escrituras recientes", que son heurísticas y fallan justo cuando importa, sino comparando: cada nota
/// registrada lleva su <see cref="NoteDiskStamp">huella</see>, el editor la actualiza en cuanto termina de
/// guardar, y al evaluar un evento se compara el disco contra esa huella. Un autoguardado de Enki deja el
/// disco exactamente igual que la huella que el propio editor acaba de registrar, así que el evento que
/// genere —que lo genera— se descarta por comparación exacta, no por sospecha.</para>
///
/// <para><b>Planes B.</b> Si el <see cref="FileSystemWatcher"/> no se puede crear (unidad de red, límite
/// de <c>inotify</c> agotado en Linux) o falla en caliente (desbordamiento de su búfer interno), se
/// degrada a sondeo periódico de las notas registradas y se deja constancia en <see cref="LogService"/>.
/// El sondeo no es un sustituto completo —un cambio tarda hasta un periodo de sondeo en verse— pero
/// junto a la comprobación previa a guardar de <c>VaultService.WriteContentChecked</c> garantiza que
/// ningún guardado pise un cambio ajeno aunque la vigilancia esté totalmente ciega.</para>
///
/// <para><b>Hilos.</b> <see cref="ExternalChangeDetected"/> se emite siempre desde un hilo del pool (el
/// del temporizador de rebote o el del sondeo), nunca desde el de interfaz — misma convención que
/// <see cref="VaultService.VaultSizeChanged"/>: quien se suscriba y toque propiedades enlazadas debe
/// marshalar con <c>Dispatcher.UIThread.Post</c> por su cuenta.</para></summary>
public sealed class VaultChangeWatcher : IDisposable
{
    /// <summary>Cuánto se espera desde el último evento de una ruta antes de mirarla. Los sincronizadores
    /// no escriben un archivo de una vez: crean un temporal, lo renombran, tocan la fecha… y cada paso es
    /// un evento. Sin este margen, una sola sincronización produciría varios avisos del mismo cambio.</summary>
    public static readonly TimeSpan DefaultDebounceDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>Periodo del sondeo cuando la vigilancia por eventos no está disponible, y también el del
    /// repaso de las notas aplazadas por tener su sesión bloqueada. Suficientemente corto para que un
    /// cambio no pase desapercibido durante una sesión de trabajo, suficientemente largo para que mirar
    /// unas pocas notas abiertas no se note.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Cuántas veces se reintenta evaluar una ruta que no se pudo leer porque otro proceso la
    /// tenía abierta en exclusiva. Pasado ese punto se deja estar: la comprobación previa a guardar
    /// volverá a mirar el disco de todos modos.</summary>
    private const int MaxReadRetries = 3;

    /// <summary>Un desbordamiento del búfer interno significa "han pasado más cosas de las que caben"; el
    /// tamaño por defecto (8 KB) se queda corto en cuanto un sincronizador vuelca una carpeta entera.</summary>
    private const int InternalBufferBytes = 64 * 1024;

    private readonly VaultService _vaultService;
    private readonly SettingsService _settingsService;
    private readonly TimeSpan _debounceDelay;
    private readonly TimeSpan _pollInterval;

    /// <summary>Protege <see cref="_tracked"/>, <see cref="_pending"/>, <see cref="_deferred"/> y el ciclo
    /// de vida de <see cref="_watcher"/>/<see cref="_pollTimer"/>/<see cref="_deferredTimer"/>: los eventos
    /// del sistema de archivos llegan en hilos del pool y <see cref="Track"/>/<see cref="UpdateStamp"/> se
    /// llaman desde el de interfaz.</summary>
    private readonly object _gate = new();

    private readonly Dictionary<string, TrackedNote> _tracked;
    private readonly Dictionary<string, Timer> _pending;
    private readonly HashSet<string> _deferred;

    private FileSystemWatcher? _watcher;
    private Timer? _pollTimer;
    private Timer? _deferredTimer;
    private string _root = string.Empty;
    private bool _enabled;
    private bool _disposed;

    /// <summary>Una nota registrada: la huella con la que comparar y cuántos reintentos de lectura lleva
    /// acumulados el evento que se está evaluando.</summary>
    private sealed class TrackedNote(NoteDiskStamp stamp)
    {
        public NoteDiskStamp Stamp { get; set; } = stamp;
        public int ReadRetries { get; set; }
    }

    /// <summary>Un cambio externo sobre una nota registrada. Ver la nota sobre hilos en la documentación
    /// de la clase.</summary>
    public event Action<VaultExternalChange>? ExternalChangeDetected;

    /// <summary>Si la vigilancia está encendida (Ajustes › Editor › "Vigilar cambios externos en el
    /// vault"). Apagada, este servicio no mira nada; la red de seguridad que sigue en pie es la
    /// comprobación previa a guardar de <c>VaultService.WriteContentChecked</c>, que no depende de él.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Si se está en el plan B por sondeo en vez de en vigilancia por eventos. Público para que
    /// las pruebas puedan afirmar la degradación sin depender de los registros.</summary>
    public bool IsPolling { get; private set; }

    /// <summary>Rutas registradas ahora mismo — para pruebas y diagnóstico.</summary>
    internal IReadOnlyCollection<string> TrackedPaths
    {
        get { lock (_gate) return _tracked.Keys.ToList(); }
    }

    public VaultChangeWatcher(VaultService vaultService, SettingsService settingsService)
        : this(vaultService, settingsService, DefaultDebounceDelay, DefaultPollInterval)
    {
    }

    /// <summary>Con los tiempos de rebote y sondeo explícitos — solo para las pruebas, que necesitan
    /// milisegundos donde la app usa cientos de milisegundos y segundos. Mismo criterio de
    /// comprobabilidad que el <c>storageDirectory</c> opcional del resto de servicios.</summary>
    internal VaultChangeWatcher(VaultService vaultService, SettingsService settingsService,
        TimeSpan debounceDelay, TimeSpan pollInterval)
    {
        _vaultService = vaultService;
        _settingsService = settingsService;
        _debounceDelay = debounceDelay;
        _pollInterval = pollInterval;

        var comparer = VaultService.PathComparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        _tracked = new Dictionary<string, TrackedNote>(comparer);
        _pending = new Dictionary<string, Timer>(comparer);
        _deferred = new HashSet<string>(comparer);

        _enabled = settingsService.Settings.VaultWatchExternalChanges;
        // El ajuste se lee del propio SettingsService en vez de recibir otro callback desde
        // SettingsViewModel: este servicio ya depende de él y así encender o apagar la vigilancia no
        // necesita cablear nada nuevo en la composición del shell.
        settingsService.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>Empieza (o reinicia) la vigilancia sobre <paramref name="vaultPath"/>. Libera lo que
    /// hubiera del vault anterior, así que cambiar de vault es simplemente volver a llamar aquí. Las notas
    /// registradas se olvidan: pertenecían al vault que se acaba de dejar.</summary>
    public void Start(string vaultPath)
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopUnderLock(forgetTracked: true);
            _root = vaultPath;
            if (_enabled && !string.IsNullOrWhiteSpace(vaultPath)) StartUnderLock();
        }
    }

    /// <summary>Detiene la vigilancia y olvida las notas registradas.</summary>
    public void Stop()
    {
        lock (_gate) StopUnderLock(forgetTracked: true);
    }

    /// <summary>Registra una nota para vigilarla, con la huella que quien la abrió acaba de leer. Volver a
    /// registrar la misma ruta solo refresca su huella.</summary>
    public void Track(string fullPath, NoteDiskStamp stamp)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _tracked[fullPath] = new TrackedNote(stamp);
        }
    }

    /// <summary>Adopta una huella nueva para una nota ya registrada — lo llama el editor tras cada
    /// guardado propio y tras cada recarga, y es lo que hace que las escrituras de Enki no se confundan
    /// con las de fuera. No registra nada si la ruta no estaba vigilada.</summary>
    public void UpdateStamp(string fullPath, NoteDiskStamp stamp)
    {
        lock (_gate)
        {
            if (_tracked.TryGetValue(fullPath, out var tracked))
            {
                tracked.Stamp = stamp;
                tracked.ReadRetries = 0;
            }
            _deferred.Remove(fullPath);
        }
    }

    /// <summary>Deja de vigilar una nota — al cerrar su pestaña, o justo antes de que Enki misma la
    /// borre o la renombre (en cuyo caso se vuelve a registrar con la ruta nueva).</summary>
    public void Untrack(string fullPath)
    {
        lock (_gate)
        {
            _tracked.Remove(fullPath);
            _deferred.Remove(fullPath);
            if (_pending.Remove(fullPath, out var timer)) timer.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _settingsService.SettingsChanged -= OnSettingsChanged;
            StopUnderLock(forgetTracked: true);
        }
    }

    // ===================== Ciclo de vida =====================

    private void OnSettingsChanged()
    {
        var enabled = _settingsService.Settings.VaultWatchExternalChanges;
        lock (_gate)
        {
            if (_disposed || enabled == _enabled) return;
            _enabled = enabled;

            if (!enabled)
            {
                // Sin olvidar las notas registradas: son las pestañas que el usuario sigue teniendo
                // abiertas, y volver a encender el ajuste tiene que retomar la vigilancia sobre ellas sin
                // obligarlas a reabrirse. Sus huellas siguen al día porque el editor llama a
                // UpdateStamp en cada guardado, esté esto encendido o no.
                StopUnderLock(forgetTracked: false);
                return;
            }
            if (!string.IsNullOrWhiteSpace(_root)) StartUnderLock();
        }
    }

    private void StartUnderLock()
    {
        try
        {
            var watcher = new FileSystemWatcher(_root)
            {
                Filter = "*.md",
                IncludeSubdirectories = true,
                InternalBufferSize = InternalBufferBytes,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
            IsPolling = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            LogService.Warning(
                $"No se pudo vigilar '{_root}' con FileSystemWatcher; se pasa a sondeo cada {_pollInterval.TotalSeconds:0} s", ex);
            StartPollingUnderLock();
        }
    }

    private void StopUnderLock(bool forgetTracked)
    {
        if (_watcher is { } watcher)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnFileChanged;
            watcher.Created -= OnFileChanged;
            watcher.Deleted -= OnFileChanged;
            watcher.Renamed -= OnFileRenamed;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
            _watcher = null;
        }

        _pollTimer?.Dispose();
        _pollTimer = null;
        _deferredTimer?.Dispose();
        _deferredTimer = null;
        IsPolling = false;

        foreach (var timer in _pending.Values) timer.Dispose();
        _pending.Clear();
        _deferred.Clear();
        if (forgetTracked) _tracked.Clear();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        LogService.Error(
            $"La vigilancia del vault '{_root}' falló (posible desbordamiento del búfer de eventos); se pasa a sondeo cada {_pollInterval.TotalSeconds:0} s",
            e.GetException());

        lock (_gate)
        {
            if (_disposed || !_enabled) return;
            if (_watcher is { } watcher)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= OnFileChanged;
                watcher.Created -= OnFileChanged;
                watcher.Deleted -= OnFileChanged;
                watcher.Renamed -= OnFileRenamed;
                watcher.Error -= OnWatcherError;
                watcher.Dispose();
                _watcher = null;
            }
            StartPollingUnderLock();
        }
    }

    private void StartPollingUnderLock()
    {
        _pollTimer?.Dispose();
        _pollTimer = new Timer(_ => PollTrackedNotes(), null, _pollInterval, _pollInterval);
        IsPolling = true;
    }

    private void PollTrackedNotes()
    {
        List<string> paths;
        lock (_gate)
        {
            if (_disposed || !_enabled) return;
            paths = _tracked.Keys.ToList();
        }
        foreach (var path in paths) Evaluate(path);
    }

    // ===================== Eventos del sistema de archivos =====================

    private void OnFileChanged(object sender, FileSystemEventArgs e) => Schedule(e.FullPath);

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        // Un renombrado *hacia* una ruta vigilada es, casi siempre, la escritura atómica de la propia
        // Enki (DurableFile escribe un temporal y lo mueve encima) o la de un sincronizador que hace lo
        // mismo: se trata como cualquier otro cambio del destino y la comparación de huellas decidirá.
        Schedule(e.FullPath);

        if (!IsTracked(e.OldFullPath)) return;
        RaiseRenamed(e.OldFullPath, e.FullPath);
    }

    private void RaiseRenamed(string oldFullPath, string newFullPath)
    {
        lock (_gate)
        {
            // Se deja de vigilar la ruta vieja aquí mismo: ya no existe. Quien atienda el aviso decidirá
            // si registra la nueva (el editor reapunta su pestaña) o si no vuelve a vigilar nada.
            _tracked.Remove(oldFullPath);
            _deferred.Remove(oldFullPath);
            if (_pending.Remove(oldFullPath, out var timer)) timer.Dispose();
        }

        ExternalChangeDetected?.Invoke(
            new VaultExternalChange(oldFullPath, VaultExternalChangeKind.Renamed, newFullPath));
    }

    private bool IsTracked(string fullPath)
    {
        lock (_gate) return _tracked.ContainsKey(fullPath);
    }

    /// <summary>Arranca (o reinicia) el rebote de una ruta. Reiniciarlo en cada evento es lo que colapsa
    /// la ráfaga de un sincronizador en una sola evaluación.</summary>
    private void Schedule(string fullPath)
    {
        if (IsIgnored(fullPath)) return;

        lock (_gate)
        {
            if (_disposed || !_enabled || !_tracked.ContainsKey(fullPath)) return;

            if (_pending.TryGetValue(fullPath, out var existing))
            {
                existing.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
                return;
            }

            _pending[fullPath] = new Timer(_ => OnDebounceElapsed(fullPath), null, _debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebounceElapsed(string fullPath)
    {
        lock (_gate)
        {
            if (_pending.Remove(fullPath, out var timer)) timer.Dispose();
            if (_disposed || !_enabled) return;
        }
        Evaluate(fullPath);
    }

    // ===================== Evaluación =====================

    /// <summary>Compara el disco con la huella registrada y publica el cambio si lo hay. Es el único sitio
    /// donde se decide que algo es "externo": todo lo demás (eventos, rebote, sondeo) solo decide
    /// <em>cuándo</em> mirar.</summary>
    private void Evaluate(string fullPath)
    {
        NoteDiskStamp baseline;
        lock (_gate)
        {
            if (_disposed || !_enabled || !_tracked.TryGetValue(fullPath, out var tracked)) return;
            baseline = tracked.Stamp;
        }

        // Una sesión protegida y bloqueada no se lee ni se compara: se aplaza hasta que se desbloquee.
        // El repaso periódico de aplazadas es quien lo reintenta, así que esto no se pierde.
        if (_vaultService.IsPathInLockedSession(fullPath))
        {
            Defer(fullPath);
            return;
        }

        NoteDiskState state;
        NoteDiskStamp current;
        try
        {
            state = NoteDiskStamps.Check(fullPath, baseline, out current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RetryLater(fullPath, ex);
            return;
        }

        lock (_gate)
        {
            if (!_tracked.TryGetValue(fullPath, out var tracked)) return;
            tracked.ReadRetries = 0;
            // La huella se adopta también cuando hay cambio: así un archivo que un sincronizador toca
            // varias veces seguidas no produce un aviso por escritura, solo uno por contenido distinto.
            if (state != NoteDiskState.Missing) tracked.Stamp = current;
            _deferred.Remove(fullPath);
        }

        switch (state)
        {
            case NoteDiskState.Modified:
                ExternalChangeDetected?.Invoke(new VaultExternalChange(fullPath, VaultExternalChangeKind.Modified));
                break;
            case NoteDiskState.Missing:
                Untrack(fullPath);
                ExternalChangeDetected?.Invoke(new VaultExternalChange(fullPath, VaultExternalChangeKind.Deleted));
                break;
        }
    }

    private void RetryLater(string fullPath, Exception ex)
    {
        lock (_gate)
        {
            if (!_tracked.TryGetValue(fullPath, out var tracked)) return;
            if (++tracked.ReadRetries > MaxReadRetries)
            {
                LogService.Warning(
                    $"No se pudo comprobar el estado de '{fullPath}' tras {MaxReadRetries} intentos; " +
                    "queda a cargo de la comprobación previa a guardar", ex);
                tracked.ReadRetries = 0;
                return;
            }
        }
        Schedule(fullPath);
    }

    /// <summary>Apunta una ruta como pendiente de revisar y asegura que el repaso periódico está en
    /// marcha. El temporizador solo existe mientras haya algo aplazado.</summary>
    private void Defer(string fullPath)
    {
        lock (_gate)
        {
            if (_disposed || !_enabled || !_tracked.ContainsKey(fullPath)) return;
            _deferred.Add(fullPath);
            _deferredTimer ??= new Timer(_ => ReviewDeferred(), null, _pollInterval, _pollInterval);
        }
    }

    private void ReviewDeferred()
    {
        List<string> paths;
        lock (_gate)
        {
            if (_disposed || !_enabled) return;
            paths = _deferred.ToList();
            if (paths.Count == 0)
            {
                _deferredTimer?.Dispose();
                _deferredTimer = null;
                return;
            }
        }

        foreach (var path in paths)
        {
            // Mientras siga bloqueada, Evaluate vuelve a aplazarla y esto no hace nada más.
            if (!_vaultService.IsPathInLockedSession(path)) Evaluate(path);
        }
    }

    // ===================== Filtros =====================

    /// <summary>Rutas que nunca son una nota del usuario aunque acaben en <c>.md</c>: el historial local
    /// de versiones, la papelera, los temporales de la escritura atómica y los archivos de conflicto que
    /// dejan los sincronizadores. Solo se evalúan rutas registradas, así que esto es sobre todo ahorro:
    /// descartar el ruido antes de tomar el cerrojo o armar un temporizador.</summary>
    private static bool IsIgnored(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (name.Contains(".sync-conflict", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".tmp-", StringComparison.Ordinal)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(directory))
        {
            var segment = Path.GetFileName(directory);
            if (segment is ".enki" or ".trash" or ".history") return true;
            directory = Path.GetDirectoryName(directory);
        }
        return false;
    }
}
