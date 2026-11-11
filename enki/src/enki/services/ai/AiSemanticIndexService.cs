using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using enki.Models;

namespace enki.Services;

public enum AiSemanticIndexState
{
    /// <summary>La búsqueda semántica está apagada en Ajustes.</summary>
    Off,
    /// <summary>Encendida, sin índice todavía (o se acaba de borrar): se construirá en la próxima actualización.</summary>
    Empty,
    Indexing,
    Ready,
    /// <summary>No se pudo actualizar: Ollama no responde, falta el modelo, no es de embeddings…
    /// (<see cref="AiSemanticIndexStatus.Problem"/>). El índice anterior, si lo hay, se sigue usando.</summary>
    Error
}

/// <summary>Por qué no se pudo actualizar el índice; cada valor tiene su texto en
/// <c>Settings_Ai_Semantic_Problem_*</c>.</summary>
public enum AiSemanticProblem { OllamaUnavailable, ModelMissing, NotEmbeddingModel, RemoteModel, InvalidEndpoint, Failed }

/// <summary>Estado del índice para Ajustes y para el resultado de <c>semantic_search</c>.</summary>
public sealed record AiSemanticIndexStatus(AiSemanticIndexState State, int Notes, int Chunks, int Pending = 0,
    DateTime? UpdatedUtc = null, AiSemanticProblem? Problem = null);

/// <summary>Un resultado de la búsqueda: la nota, su fragmento más parecido (posición en el contenido y título de
/// sección) y la similitud coseno, de -1 a 1.</summary>
public sealed record AiSemanticHit(string FullPath, string Title, string Heading, int Start, int Length, double Score);

/// <summary>Búsqueda semántica local (AIA-16): un índice de embeddings de las notas generado con un modelo de
/// Ollama en este equipo, para encontrar notas por lo que significan y no solo por sus palabras.
///
/// <para><b>Nada sale del equipo.</b> Los embeddings los calcula Ollama por <c>localhost</c> (misma validación de
/// dirección que el chat local, <see cref="OllamaService.Endpoint"/>), y un modelo servido desde otro equipo
/// (<c>remote_host</c>) se rechaza. Nunca se usa un proveedor en la nube para indexar.</para>
///
/// <para><b>Dónde vive.</b> En la carpeta de la instalación (<c>ai/semantic/&lt;vault&gt;.bin</c>, privada), nunca
/// en el vault, y sin texto de las notas: por cada nota, su ruta relativa, su huella y, por fragmento, su posición,
/// su título de sección y su vector. Para enseñar un fragmento se relee la nota. Borrar el índice no toca ninguna
/// nota.</para>
///
/// <para><b>Qué se indexa.</b> La raíz del vault y las sesiones sin contraseña. Una sesión protegida no se indexa
/// nunca, ni siquiera desbloqueada. El ámbito de cada chat se aplica después, al buscar.</para>
///
/// <para><b>Actualización incremental.</b> Cada actualización compara fecha y tamaño de cada nota con el índice y
/// solo vuelve a calcular los vectores de las que cambiaron de verdad (hash del texto). Se actualiza al encenderlo,
/// tras las escrituras de Enki (<see cref="VaultService.ContentChanged"/>, con un margen), cada pocos minutos para
/// recoger los cambios de un sincronizador, y antes de cada búsqueda. Cambiar de modelo obliga a reconstruirlo.</para></summary>
public sealed class AiSemanticIndexService : IDisposable
{
    private const uint Magic = 0x314D4553; // "SEM1"
    private const int FormatVersion = 1;
    private const int BatchSize = 16;
    private const int SaveEveryNotes = 40;
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PeriodicRefresh = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    private readonly SettingsService _settings;
    private readonly VaultService _vault;
    private readonly SessionService _sessions;
    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Timer _debounce;
    private readonly Timer _periodic;

    /// <summary>Índice cargado: notas por ruta relativa al vault.</summary>
    private Dictionary<string, IndexedNote> _notes = new(StringComparer.Ordinal);
    private string? _loadedKey;
    private string _model = string.Empty;
    private int _dimension;
    private DateTime? _updatedUtc;
    private AiSemanticIndexStatus _status;
    private CancellationTokenSource? _refreshCancellation;
    private bool _disposed;

    /// <param name="storageDirectory">Sustituye a la carpeta de la instalación, para los tests.</param>
    public AiSemanticIndexService(SettingsService settings, VaultService vault, SessionService sessions,
        string? storageDirectory = null, HttpClient? httpClient = null)
    {
        _settings = settings;
        _vault = vault;
        _sessions = sessions;
        _http = httpClient ?? EnkiMcpClient.LoopbackHttp;
        _directory = Path.Combine(storageDirectory ?? AppDataDirectory.Resolve(), "ai", "semantic");
        _status = new AiSemanticIndexStatus(IsEnabled ? AiSemanticIndexState.Empty : AiSemanticIndexState.Off, 0, 0);
        _debounce = new Timer(_ => _ = RefreshInBackgroundAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _periodic = new Timer(_ => _ = RefreshInBackgroundAsync(), null, PeriodicRefresh, PeriodicRefresh);
        _vault.ContentChanged += ScheduleRefresh;
    }

    /// <summary>Cambió el estado (desde cualquier hilo: quien lo pinte debe pasar al de interfaz).</summary>
    public event Action? StatusChanged;

    public bool IsEnabled => _settings.Settings.Ai.SemanticSearchEnabled;

    public AiSemanticIndexStatus Status
    {
        get
        {
            lock (_gate) return _status;
        }
    }

    /// <summary>Hay un índice del vault abierto con el modelo configurado que se puede consultar.</summary>
    public bool IsReady
    {
        get
        {
            EnsureLoaded();
            lock (_gate) return IsEnabled && _notes.Count > 0 && _model == ConfiguredModel;
        }
    }

    private string ConfiguredModel => _settings.Settings.Ai.EmbeddingModel;

    /// <summary>Tras una escritura de Enki: actualiza el índice al cabo de un rato, juntando las ráfagas de guardados.</summary>
    public void ScheduleRefresh()
    {
        if (!IsEnabled || _disposed) return;
        _debounce.Change(ChangeDebounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Se encendió, se apagó o cambió el modelo en Ajustes, o se abrió otro vault.</summary>
    public void SettingsChanged()
    {
        if (!IsEnabled)
        {
            _refreshCancellation?.Cancel();
            SetStatus(new AiSemanticIndexStatus(AiSemanticIndexState.Off, 0, 0));
            return;
        }
        // Cargar un índice grande y compararlo con el vault no debe frenar la interfaz: todo va en segundo plano.
        _ = Task.Run(() => TryRefreshAsync(CancellationToken.None));
    }

    private Task RefreshInBackgroundAsync() => TryRefreshAsync(CancellationToken.None);

    /// <summary>Como <see cref="RefreshAsync"/>, pero sin lanzar: un fallo queda en el estado y en el log. Para quien
    /// no espera al resultado (temporizadores, la búsqueda que solo da unos segundos de margen).</summary>
    public async Task TryRefreshAsync(CancellationToken cancellation)
    {
        try
        {
            await RefreshAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Una actualización en segundo plano nunca debe tumbar la app: queda en el estado y en el log.
            LogService.Warning("[ia] no se pudo actualizar el índice semántico", ex);
        }
    }

    /// <summary>Pone el índice al día con el vault. Si ya hay una actualización en marcha, espera a que termine en
    /// vez de lanzar otra.</summary>
    public async Task RefreshAsync(CancellationToken cancellation)
    {
        if (!IsEnabled || _disposed) return;
        await _refreshGate.WaitAsync(cancellation).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _refreshCancellation = linked;
        try
        {
            await RefreshCoreAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _refreshCancellation = null;
            _refreshGate.Release();
        }
    }

    /// <summary>Borra el índice del vault abierto (memoria y disco) y, si sigue encendido, lo vuelve a construir.</summary>
    public async Task RebuildAsync()
    {
        DeleteIndex();
        await RefreshInBackgroundAsync().ConfigureAwait(false);
    }

    /// <summary>Borra el índice del vault abierto. No toca ninguna nota.</summary>
    public void DeleteIndex()
    {
        _refreshCancellation?.Cancel();
        var key = VaultKey();
        lock (_gate)
        {
            _notes = new Dictionary<string, IndexedNote>(StringComparer.Ordinal);
            _model = string.Empty;
            _dimension = 0;
            _updatedUtc = null;
            _loadedKey = key;
        }
        try
        {
            var path = IndexPath(key);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning("[ia] no se pudo borrar el índice semántico", ex);
        }
        SetStatus(new AiSemanticIndexStatus(IsEnabled ? AiSemanticIndexState.Empty : AiSemanticIndexState.Off, 0, 0));
    }

    // ===================== Consultas =====================

    /// <summary>Las notas cuyo contenido más se parece a <paramref name="query"/>, de las que acepta
    /// <paramref name="includePath"/> (ruta completa): el ámbito del chat. Calcula el vector de la consulta con el
    /// mismo modelo local.</summary>
    public async Task<IReadOnlyList<AiSemanticHit>> SearchAsync(string query, int limit, Func<string, bool> includePath,
        CancellationToken cancellation)
    {
        var model = ConfiguredModel;
        var vectors = await EmbedAsync(model, [QueryPrefix(model) + query], cancellation).ConfigureAwait(false);
        return Rank(vectors[0], limit, includePath, excludePath: null);
    }

    /// <summary>Las notas más parecidas a una nota ya indexada, sin llamar a Ollama: se compara con la media de los
    /// vectores de sus fragmentos.</summary>
    public IReadOnlyList<AiSemanticHit> SimilarTo(string fullPath, int limit, Func<string, bool> includePath)
    {
        EnsureLoaded();
        float[]? centroid;
        lock (_gate)
        {
            if (!_notes.TryGetValue(Relative(fullPath), out var note) || note.Chunks.Count == 0) return [];
            centroid = new float[_dimension];
            foreach (var chunk in note.Chunks)
                for (var i = 0; i < centroid.Length; i++) centroid[i] += chunk.Vector[i];
            Normalize(centroid);
        }
        return Rank(centroid, limit, includePath, excludePath: fullPath);
    }

    private List<AiSemanticHit> Rank(float[] query, int limit, Func<string, bool> includePath, string? excludePath)
    {
        var best = new List<AiSemanticHit>();
        lock (_gate)
        {
            if (query.Length != _dimension) return best;
            foreach (var (relative, note) in _notes)
            {
                var full = Absolute(relative);
                if (excludePath is not null && VaultService.PathsEqual(full, excludePath)) continue;
                IndexedChunk? top = null;
                var topScore = double.MinValue;
                foreach (var chunk in note.Chunks)
                {
                    var score = Dot(query, chunk.Vector);
                    if (score > topScore) (top, topScore) = (chunk, score);
                }
                if (top is not null) best.Add(new AiSemanticHit(full, note.Title, top.Heading, top.Start, top.Length, topScore));
            }
        }
        // El ámbito se comprueba fuera del candado: puede tocar el disco (enlaces simbólicos, sesiones).
        return best.OrderByDescending(h => h.Score).Where(h => includePath(h.FullPath)).Take(limit).ToList();
    }

    // ===================== Actualización =====================

    private async Task RefreshCoreAsync(CancellationToken cancellation)
    {
        if (!_vault.IsVaultConfigured) return;
        EnsureLoaded();
        var model = ConfiguredModel;
        if (await CheckModelAsync(model, cancellation).ConfigureAwait(false) is { } problem)
        {
            SetStatus(CurrentStatus(AiSemanticIndexState.Error, problem: problem));
            return;
        }
        lock (_gate)
        {
            // Vectores de modelos distintos no se pueden comparar: otro modelo empieza de cero.
            if (_model != model) (_notes, _model, _dimension) = (new Dictionary<string, IndexedNote>(StringComparer.Ordinal), model, 0);
        }

        var files = await Task.Run(EnumerateIndexableNotes, cancellation).ConfigureAwait(false);
        var present = files.Select(f => f.Relative).ToHashSet(StringComparer.Ordinal);
        List<NoteSource> changed;
        lock (_gate)
        {
            foreach (var gone in _notes.Keys.Where(k => !present.Contains(k)).ToList()) _notes.Remove(gone);
            changed = files.Where(f => !_notes.TryGetValue(f.Relative, out var known)
                || known.LastWriteTicks != f.LastWriteTicks || known.Size != f.Size).ToList();
        }
        if (changed.Count == 0)
        {
            SetStatus(CurrentStatus(AiSemanticIndexState.Ready));
            return;
        }

        var done = 0;
        var sinceSave = 0;
        SetStatus(CurrentStatus(AiSemanticIndexState.Indexing, pending: changed.Count));
        foreach (var source in changed)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                await IndexNoteAsync(source, model, cancellation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException
                                           || ex is OperationCanceledException && !cancellation.IsCancellationRequested)
            {
                // Ollama dejó de responder (o tardó más que el límite): lo hecho hasta aquí se conserva.
                LogService.Warning("[ia] Ollama no respondió al calcular embeddings", ex);
                Save();
                SetStatus(CurrentStatus(AiSemanticIndexState.Error, problem: AiSemanticProblem.OllamaUnavailable));
                return;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
            {
                LogService.Warning("[ia] respuesta de embeddings inesperada", ex);
                Save();
                SetStatus(CurrentStatus(AiSemanticIndexState.Error, problem: AiSemanticProblem.Failed));
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Una nota que no se puede leer ahora (bloqueada por otro programa) se intenta en la próxima vuelta.
                LogService.Debug($"[ia] nota no indexada: {ex.Message}");
            }
            done++;
            if (++sinceSave >= SaveEveryNotes)
            {
                Save();
                sinceSave = 0;
            }
            SetStatus(CurrentStatus(AiSemanticIndexState.Indexing, pending: changed.Count - done));
        }
        lock (_gate) _updatedUtc = DateTime.UtcNow;
        Save();
        SetStatus(CurrentStatus(AiSemanticIndexState.Ready));
    }

    private async Task IndexNoteAsync(NoteSource source, string model, CancellationToken cancellation)
    {
        var content = await File.ReadAllTextAsync(source.FullPath, cancellation).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var title = NoteTitle(content, source.FullPath);
        lock (_gate)
        {
            // Solo cambió la fecha (un sincronizador que reescribe lo mismo): no hay nada que recalcular.
            if (_notes.TryGetValue(source.Relative, out var known) && known.ContentHash == hash)
            {
                _notes[source.Relative] = known with { LastWriteTicks = source.LastWriteTicks, Size = source.Size };
                return;
            }
        }

        var chunks = AiSemanticChunker.Split(content);
        var vectors = new List<float[]>();
        var prefix = DocumentPrefix(model);
        for (var i = 0; i < chunks.Count; i += BatchSize)
        {
            var batch = chunks.Skip(i).Take(BatchSize)
                .Select(c => prefix + title + (c.Heading.Length > 0 ? " › " + c.Heading : string.Empty) + "\n\n"
                    + content.Substring(c.Start, c.Length))
                .ToList();
            vectors.AddRange(await EmbedAsync(model, batch, cancellation).ConfigureAwait(false));
        }
        lock (_gate)
        {
            if (vectors.Count > 0)
            {
                if (_dimension == 0) _dimension = vectors[0].Length;
                if (vectors.Any(v => v.Length != _dimension)) throw new InvalidDataException("Embedding dimension changed.");
            }
            _notes[source.Relative] = new IndexedNote(title, source.LastWriteTicks, source.Size, hash,
                chunks.Select((c, i) => new IndexedChunk(c.Start, c.Length, c.Heading, vectors[i])).ToList());
        }
    }

    /// <summary>Notas de la raíz del vault y de las sesiones sin contraseña. Una sesión protegida no se indexa nunca.</summary>
    private List<NoteSource> EnumerateIndexableNotes()
    {
        var roots = new List<string> { _vault.VaultPath };
        roots.AddRange(_sessions.List().Where(s => !s.IsEncrypted).Select(s => s.RootPath));
        var result = new List<NoteSource>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var note in _vault.GetAllNotesRecursive(root))
            {
                if (_vault.IsPathInEncryptedSession(note.FullPath)) continue;
                var info = new FileInfo(note.FullPath);
                if (!info.Exists) continue;
                result.Add(new NoteSource(note.FullPath, Relative(note.FullPath), info.LastWriteTimeUtc.Ticks, info.Length));
            }
        }
        return result;
    }

    private async Task<AiSemanticProblem?> CheckModelAsync(string model, CancellationToken cancellation)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _http.PostAsJsonAsync(OllamaService.Endpoint(_settings.Settings.Ai.OllamaEndpoint, "api/show"),
                new { model }, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return AiSemanticProblem.ModelMissing;
            response.EnsureSuccessStatusCode();
            var info = await response.Content.ReadFromJsonAsync<JsonObject>(timeout.Token).ConfigureAwait(false);
            if (OllamaService.IsRemote(info)) return AiSemanticProblem.RemoteModel;
            // Ollama anuncia "embedding" en los modelos que lo son; una versión que no informe capacidades se acepta.
            if (info?["capabilities"] is JsonArray capabilities && capabilities.Count > 0
                && !capabilities.Any(c => c?.GetValue<string>() == "embedding")) return AiSemanticProblem.NotEmbeddingModel;
            return null;
        }
        catch (ArgumentException)
        {
            return AiSemanticProblem.InvalidEndpoint;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return AiSemanticProblem.OllamaUnavailable;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            return AiSemanticProblem.OllamaUnavailable;
        }
    }

    private async Task<List<float[]>> EmbedAsync(string model, List<string> inputs, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(RequestTimeout);
        using var response = await _http.PostAsJsonAsync(OllamaService.Endpoint(_settings.Settings.Ai.OllamaEndpoint, "api/embed"),
            new { model, input = inputs, truncate = true }, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<JsonObject>(timeout.Token).ConfigureAwait(false);
        var embeddings = data?["embeddings"]?.AsArray()
            ?? throw new HttpRequestException("Ollama returned no embeddings.");
        if (embeddings.Count != inputs.Count) throw new HttpRequestException("Ollama returned a different number of embeddings.");
        return embeddings.Select(e =>
        {
            var vector = e!.AsArray().Select(v => v!.GetValue<float>()).ToArray();
            Normalize(vector);
            return vector;
        }).ToList();
    }

    /// <summary>Los modelos <c>nomic-embed-text</c> esperan un prefijo que distingue documentos de consultas.</summary>
    private static string DocumentPrefix(string model) => model.Contains("nomic", StringComparison.OrdinalIgnoreCase) ? "search_document: " : string.Empty;

    private static string QueryPrefix(string model) => model.Contains("nomic", StringComparison.OrdinalIgnoreCase) ? "search_query: " : string.Empty;

    // ===================== Persistencia =====================

    private string VaultKey() => VaultSettingsService.PathIdentity(_vault.VaultPath);

    private string IndexPath(string key) => Path.Combine(_directory, key + ".bin");

    /// <summary>Carga el índice del vault abierto si todavía no está en memoria (o se cambió de vault).</summary>
    private void EnsureLoaded()
    {
        var key = VaultKey();
        lock (_gate)
        {
            if (_loadedKey == key) return;
            _loadedKey = key;
            _notes = new Dictionary<string, IndexedNote>(StringComparer.Ordinal);
            _model = string.Empty;
            _dimension = 0;
            _updatedUtc = null;
            try
            {
                var path = IndexPath(key);
                if (File.Exists(path)) Read(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException
                                           or ArgumentException)
            {
                // Un índice dañado no es un dato del usuario: se descarta y se reconstruye.
                LogService.Warning("[ia] índice semántico ilegible; se reconstruirá", ex);
                _notes.Clear();
                _model = string.Empty;
            }
        }
        SetStatus(CurrentStatus(!IsEnabled ? AiSemanticIndexState.Off
            : _notes.Count > 0 ? AiSemanticIndexState.Ready : AiSemanticIndexState.Empty));
    }

    private void Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion) throw new InvalidDataException("Unknown semantic index format.");
        _model = reader.ReadString();
        _dimension = reader.ReadInt32();
        var updated = reader.ReadInt64();
        _updatedUtc = updated > 0 ? new DateTime(updated, DateTimeKind.Utc) : null;
        var count = reader.ReadInt32();
        for (var n = 0; n < count; n++)
        {
            var relative = reader.ReadString();
            var title = reader.ReadString();
            var ticks = reader.ReadInt64();
            var size = reader.ReadInt64();
            var hash = reader.ReadString();
            var chunkCount = reader.ReadInt32();
            var chunks = new List<IndexedChunk>(chunkCount);
            for (var c = 0; c < chunkCount; c++)
            {
                var start = reader.ReadInt32();
                var length = reader.ReadInt32();
                var heading = reader.ReadString();
                var vector = new float[_dimension];
                for (var i = 0; i < vector.Length; i++) vector[i] = reader.ReadSingle();
                chunks.Add(new IndexedChunk(start, length, heading, vector));
            }
            _notes[relative] = new IndexedNote(title, ticks, size, hash, chunks);
        }
    }

    private void Save()
    {
        byte[] bytes;
        string key;
        lock (_gate)
        {
            if (_loadedKey is null) return;
            key = _loadedKey;
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(_model);
                writer.Write(_dimension);
                writer.Write(_updatedUtc?.Ticks ?? 0);
                writer.Write(_notes.Count);
                foreach (var (relative, note) in _notes)
                {
                    writer.Write(relative);
                    writer.Write(note.Title);
                    writer.Write(note.LastWriteTicks);
                    writer.Write(note.Size);
                    writer.Write(note.ContentHash);
                    writer.Write(note.Chunks.Count);
                    foreach (var chunk in note.Chunks)
                    {
                        writer.Write(chunk.Start);
                        writer.Write(chunk.Length);
                        writer.Write(chunk.Heading);
                        foreach (var value in chunk.Vector) writer.Write(value);
                    }
                }
            }
            bytes = buffer.ToArray();
        }
        try
        {
            PrivateDataPermissions.EnsurePrivateDirectory(_directory);
            DurableFile.WriteAllBytesAtomic(IndexPath(key), bytes, privateFile: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning("[ia] no se pudo guardar el índice semántico", ex);
        }
    }

    // ===================== Utilidades =====================

    private AiSemanticIndexStatus CurrentStatus(AiSemanticIndexState state, int pending = 0, AiSemanticProblem? problem = null)
    {
        lock (_gate)
            return new AiSemanticIndexStatus(IsEnabled ? state : AiSemanticIndexState.Off, _notes.Count,
                _notes.Values.Sum(n => n.Chunks.Count), pending, _updatedUtc, problem);
    }

    private void SetStatus(AiSemanticIndexStatus status)
    {
        lock (_gate) _status = status;
        StatusChanged?.Invoke();
    }

    private string Relative(string fullPath) => Path.GetRelativePath(_vault.VaultPath, fullPath).Replace('\\', '/');

    private string Absolute(string relative) => Path.GetFullPath(Path.Combine(_vault.VaultPath, relative));

    private static string NoteTitle(string content, string path)
    {
        foreach (var line in NoteTagHelper.StripFrontMatter(content).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal)) return trimmed[2..].Trim();
            if (trimmed.Length > 0) break;
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static double Dot(float[] a, float[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    private static void Normalize(float[] vector)
    {
        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (norm <= 0) return;
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vault.ContentChanged -= ScheduleRefresh;
        _refreshCancellation?.Cancel();
        _debounce.Dispose();
        _periodic.Dispose();
    }

    private sealed record NoteSource(string FullPath, string Relative, long LastWriteTicks, long Size);

    private sealed record IndexedNote(string Title, long LastWriteTicks, long Size, string ContentHash, List<IndexedChunk> Chunks);

    private sealed record IndexedChunk(int Start, int Length, string Heading, float[] Vector);
}

/// <summary>Un fragmento de una nota: posición en el contenido original y título de la sección en la que está.</summary>
public readonly record struct AiSemanticChunk(int Start, int Length, string Heading);

/// <summary>Trocea una nota para el índice semántico: por secciones (títulos Markdown) y, dentro de cada una, por
/// párrafos hasta un tamaño máximo, sin el frontmatter. Devuelve posiciones sobre el texto original, no copias, para
/// que el índice no guarde el contenido de las notas.</summary>
public static class AiSemanticChunker
{
    private const int MinChars = 24;

    public static IReadOnlyList<AiSemanticChunk> Split(string content, int maxChars = 1_500, int maxChunks = 64)
    {
        var chunks = new List<AiSemanticChunk>();
        var bodyStart = content.Length - NoteTagHelper.StripFrontMatter(content).Length;
        var heading = string.Empty;
        var (start, end) = (-1, -1);

        void Add(int from, int length)
        {
            if (chunks.Count < maxChunks && CountNonWhitespace(content.AsSpan(from, length)) >= MinChars)
                chunks.Add(new AiSemanticChunk(from, length, heading));
        }

        void Flush()
        {
            if (start >= 0) Add(start, end - start);
            (start, end) = (-1, -1);
        }

        for (var cursor = bodyStart; cursor < content.Length && chunks.Count < maxChunks;)
        {
            var lineEnd = content.IndexOf('\n', cursor);
            var next = lineEnd < 0 ? content.Length : lineEnd + 1;
            var line = content.AsSpan(cursor, next - cursor).Trim();
            if (line.StartsWith("#") && line.TrimStart('#').StartsWith(" "))
            {
                Flush();
                heading = line.TrimStart('#').Trim().ToString();
            }
            else if (line.IsEmpty)
            {
                // Un párrafo termina en una línea en blanco: se cierra el fragmento si ya va bastante lleno.
                if (start >= 0 && end - start >= maxChars * 2 / 3) Flush();
            }
            else if (next - cursor > maxChars)
            {
                // Una sola línea más larga que el máximo se parte en trozos del máximo.
                Flush();
                for (var piece = cursor; piece < next; piece += maxChars) Add(piece, Math.Min(maxChars, next - piece));
            }
            else
            {
                if (start >= 0 && next - start > maxChars) Flush();
                if (start < 0) start = cursor;
                end = next;
            }
            cursor = next;
        }
        Flush();
        // Una nota muy corta (solo un título) se indexa entera para que se pueda encontrar por su nombre.
        if (chunks.Count == 0 && content.Length > bodyStart)
            chunks.Add(new AiSemanticChunk(bodyStart, Math.Min(maxChars, content.Length - bodyStart), heading));
        return chunks;
    }

    private static int CountNonWhitespace(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var c in text) if (!char.IsWhiteSpace(c)) count++;
        return count;
    }
}
