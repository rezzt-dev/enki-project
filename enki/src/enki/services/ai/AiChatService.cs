using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Historial de conversaciones del asistente: un JSON por chat en
/// <c>%AppData%/Enki/ai/chats/</c> (<c>.devdata/ai/chats/</c> en Debug). Uno por chat y no un único archivo
/// porque cada mensaje nuevo reescribe su conversación, y reescribir todo el historial en cada turno crecería
/// sin límite. Vive en la instalación y no en el vault por el mismo motivo que
/// <see cref="Models.AiSettings"/>: el CLI guarda su parte de cada conversación en este equipo, y retomarla
/// desde otro no funcionaría. Dentro de la instalación, cada chat sabe de qué vault es
/// (<see cref="AiChatSession.VaultId"/>) y el asistente solo enseña los del vault abierto. Ver
/// <c>.docs/project-info/asistente-ia.md</c>.</summary>
public sealed class AiChatService
{
    private readonly string _directory;
    private readonly string _draftsPath;
    private List<AiChatSession>? _sessions;
    private Dictionary<string, AiChatDraft>? _newChatDrafts;

    /// <summary>Se dispara tras guardar, borrar o vaciar el historial.</summary>
    public event Action? ChatsChanged;

    /// <summary>Archivos de chat que existían pero no se pudieron leer en la última carga. Se dejan en disco
    /// tal cual —no se sobrescriben— para no perder nada que pueda recuperarse a mano.</summary>
    public int UnreadableFileCount { get; private set; }

    /// <param name="storageDirectory">Sustituye a <c>%AppData%/Enki</c> como raíz — para los tests.</param>
    public AiChatService(string? storageDirectory = null)
    {
        var root = Path.Combine(storageDirectory ?? AppDataDirectory.Resolve(), "ai");
        _directory = Path.Combine(root, "chats");
        _draftsPath = Path.Combine(root, "drafts.json");
    }

    /// <summary>Todas las conversaciones, la más reciente primero. Se leen de disco la primera vez que se
    /// piden, no al arrancar la app: con la IA apagada nadie paga ese coste.</summary>
    public IReadOnlyList<AiChatSession> Sessions => _sessions ??= Load();

    public AiChatSession? Find(string? id) =>
        id is null ? null : Sessions.FirstOrDefault(s => s.Id == id);

    /// <summary>Conversaciones guardadas antes de que el historial se separara por vault: no se sabe de cuál
    /// son, así que se conservan aparte hasta que el usuario las asocia (ver <see cref="AssignToVault"/>).</summary>
    public IEnumerable<AiChatSession> UnassignedSessions => Sessions.Where(s => s.VaultId is null);

    /// <summary>Asocia una conversación sin vault al vault indicado, por decisión expresa del usuario. Una
    /// conversación que ya pertenece a un vault no se reasigna.</summary>
    public void AssignToVault(AiChatSession session, string vaultId, string? vaultName)
    {
        if (session.VaultId is not null) throw new InvalidOperationException("El chat ya pertenece a un vault.");
        session.VaultId = vaultId;
        session.VaultName = vaultName;
        try
        {
            Save(session);
        }
        catch
        {
            session.VaultId = null;
            session.VaultName = null;
            throw;
        }
    }

    private List<AiChatSession> Load()
    {
        UnreadableFileCount = 0;
        if (!Directory.Exists(_directory)) return [];

        var sessions = new List<AiChatSession>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            if (JsonFileStore.TryLoad<AiChatSession>(file, out var session) && IsValid(session!, file))
            {
                session!.Messages ??= [];
                sessions.Add(session);
            }
            else
            {
                UnreadableFileCount++;
            }
        }
        return sessions.OrderByDescending(s => s.UpdatedUtc).ToList();
    }

    /// <summary>El id es también el nombre del archivo: uno que no coincida (copiado a mano, renombrado)
    /// haría que guardar escribiera en otro archivo y el chat apareciera duplicado.</summary>
    private static bool IsValid(AiChatSession session, string file) =>
        IsValidId(session.Id)
        && Enum.IsDefined(session.Provider)
        && string.Equals(Path.GetFileNameWithoutExtension(file), session.Id, StringComparison.Ordinal);

    /// <summary>Persiste <paramref name="session"/> y la sube al principio del historial. Lanza
    /// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> si el disco falla: el llamador
    /// decide cómo avisar.</summary>
    public void Save(AiChatSession session)
    {
        var sessions = (List<AiChatSession>)Sessions;
        PrivateDataPermissions.EnsurePrivateDirectory(_directory);
        JsonFileStore.Save(PathFor(session.Id), session);

        sessions.Remove(session);
        sessions.Insert(0, session);
        sessions.Sort((a, b) => b.UpdatedUtc.CompareTo(a.UpdatedUtc));
        ChatsChanged?.Invoke();
    }

    public void Delete(string id)
    {
        var sessions = (List<AiChatSession>)Sessions;
        var path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
        if (sessions.RemoveAll(s => s.Id == id) > 0) ChatsChanged?.Invoke();
    }

    /// <summary>Vacía el historial de Enki. No toca las conversaciones que el CLI guarda por su cuenta en
    /// <c>~/.claude/</c>: esas son del usuario y de su CLI, y Enki no lee ni escribe ahí.</summary>
    public void DeleteAll()
    {
        if (Directory.Exists(_directory))
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json")) File.Delete(file);
        if (File.Exists(_draftsPath)) File.Delete(_draftsPath);
        _sessions = [];
        _newChatDrafts = [];
        UnreadableFileCount = 0;
        ChatsChanged?.Invoke();
    }

    // ===================== Borradores de chats nuevos =====================

    /// <summary>Borradores de chats que aún no se han empezado, uno por vault y ámbito (la clave la compone
    /// el asistente). Los de un chat empezado viven en el propio chat (<see cref="AiChatSession.Draft"/>).
    /// Todos juntos en un archivo pequeño: solo guardan texto y referencias, nunca contenido de notas.</summary>
    private Dictionary<string, AiChatDraft> NewChatDrafts => _newChatDrafts ??= LoadDrafts();

    private Dictionary<string, AiChatDraft> LoadDrafts() =>
        File.Exists(_draftsPath) && JsonFileStore.TryLoad<Dictionary<string, AiChatDraft>>(_draftsPath, out var drafts) && drafts is not null
            ? new Dictionary<string, AiChatDraft>(drafts.Where(d => d.Value is not null), StringComparer.Ordinal)
            : new Dictionary<string, AiChatDraft>(StringComparer.Ordinal);

    public AiChatDraft? FindNewChatDraft(string key) => NewChatDrafts.GetValueOrDefault(key);

    /// <summary>Guarda (o, vacío o <see langword="null"/>, borra) el borrador de un chat nuevo. Lanza
    /// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> si el disco falla.</summary>
    public void SaveNewChatDraft(string key, AiChatDraft? draft)
    {
        var drafts = NewChatDrafts;
        if (draft is null || draft.IsEmpty)
        {
            if (!drafts.Remove(key)) return;
        }
        else
        {
            drafts[key] = draft;
        }
        PrivateDataPermissions.EnsurePrivateDirectory(Path.GetDirectoryName(_draftsPath)!);
        JsonFileStore.Save(_draftsPath, drafts);
    }

    private string PathFor(string id)
    {
        // El id lo genera Enki (Guid "N"), pero llega también desde archivos que se pueden editar a mano.
        if (!IsValidId(id)) throw new ArgumentException("Identificador de chat no válido.", nameof(id));
        return Path.Combine(_directory, id + ".json");
    }

    private static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && id.All(char.IsAsciiLetterOrDigit);
}
