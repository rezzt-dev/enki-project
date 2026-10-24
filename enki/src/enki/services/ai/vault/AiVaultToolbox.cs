using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>Resultado de una herramienta tal como se devuelve al modelo: JSON con los datos, o un mensaje de
/// error que el modelo puede leer y corregir.</summary>
public sealed record AiToolOutcome(string Text, bool IsError);

/// <summary>Todo lo que el asistente puede hacer en el vault, como herramientas que se le sirven por MCP
/// (<see cref="EnkiMcpServer"/>): leer, buscar, crear y organizar notas, carpetas y archivos; gestionar
/// sesiones, tarjetas de Kanban, tareas de Polar y recordatorios; consultar la agenda; y recuperar o vaciar
/// la papelera. Cada herramienta trabaja directamente sobre la entidad real a través de su servicio de
/// siempre (<see cref="VaultService"/>, <see cref="KanbanService"/>, <see cref="PolarTaskService"/>…), con las
/// mismas reglas que la interfaz: no hay un segundo almacén ni copia de datos para la IA.
///
/// <para><b>Ámbito.</b> Lo que se ofrece y lo que se puede tocar lo decide <see cref="AiAccessScope"/>, en
/// cada llamada y no solo al listar: una herramienta fuera del ámbito no aparece, y una ruta, sesión o
/// elemento fuera de él se rechaza. Las rutas son siempre relativas a un espacio y nunca salen de él, no
/// entran en carpetas internas (<c>.trash</c>, <c>.sessions</c>, adjuntos) ni siguen enlaces simbólicos.</para>
///
/// <para><b>Seguridad de los datos.</b> Toda escritura sobre una nota pasa por
/// <see cref="VaultService.WriteContentChecked"/> contra la huella de lo que se leyó, después de guardar el
/// contenido anterior en el historial de la nota (<see cref="NoteVersionTrigger.Assistant"/>). Borrar envía a
/// la papelera; borrar de verdad, o sacar contenido de una sesión protegida, pide confirmación en el chat.
/// Con <see cref="AiSettings.AllowVaultChanges"/> apagado solo se ofrecen las herramientas de lectura.</para>
///
/// <para>Se ejecuta en el hilo de interfaz (lo garantiza <see cref="EnkiMcpServer"/>), igual que las
/// pantallas que comparten estos servicios. Ver <c>.docs/project-info/asistente-ia.md</c>.</para></summary>
public sealed partial class AiVaultToolbox
{
    /// <summary>Lo que se espera a que el usuario confirme una acción antes de darla por denegada.</summary>
    public static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(5);

    private const int DefaultListLimit = 100;
    private const int MaxListLimit = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly VaultService _vault;
    private readonly SessionService _sessions;
    private readonly KanbanService _kanban;
    private readonly PolarTaskService _polar;
    private readonly PolarAuthService _polarAuth;
    private readonly ReminderService _reminders;
    private readonly SettingsService _settings;
    private readonly CalendarAggregationService? _calendar;
    private readonly AiVaultHost _host;
    private readonly NoteSearchService _search;
    private readonly Dictionary<string, AiToolDefinition> _tools = new(StringComparer.Ordinal);

    public AiVaultToolbox(
        VaultService vaultService,
        SessionService sessionService,
        KanbanService kanbanService,
        PolarTaskService polarTaskService,
        PolarAuthService polarAuthService,
        ReminderService reminderService,
        SettingsService settingsService,
        CalendarAggregationService? calendarAggregationService = null,
        AiVaultHost? host = null)
    {
        _vault = vaultService;
        _sessions = sessionService;
        _kanban = kanbanService;
        _polar = polarTaskService;
        _polarAuth = polarAuthService;
        _reminders = reminderService;
        _settings = settingsService;
        _calendar = calendarAggregationService;
        _host = host ?? new AiVaultHost();
        _search = new NoteSearchService(vaultService);

        RegisterOverviewTools();
        RegisterNoteTools();
        RegisterFolderAndFileTools();
        RegisterSessionTools();
        RegisterKanbanTools();
        RegisterPolarTools();
        RegisterReminderTools();
        RegisterTrashTools();
    }

    /// <summary>Todas las herramientas que existen, se ofrezcan o no en un chat concreto.</summary>
    public IReadOnlyCollection<AiToolDefinition> AllTools => _tools.Values;

    private bool ChangesAllowed => _settings.Settings.Ai.AllowVaultChanges;

    /// <summary>Las herramientas que se ofrecen al modelo en este turno.</summary>
    public IReadOnlyList<AiToolDefinition> AvailableTools(AiToolContext context) =>
        _tools.Values.Where(tool => IsAvailable(tool, context.Scope)).ToList();

    private bool IsAvailable(AiToolDefinition tool, AiAccessScope scope)
    {
        if (tool.Effect != AiToolEffect.Read && !ChangesAllowed) return false;
        return tool.Scope switch
        {
            AiToolScope.Content => true,
            AiToolScope.Vault => scope.IncludesVault,
            AiToolScope.Polar => scope.IncludesVault && _polarAuth.IsLinked,
            AiToolScope.Sessions => scope.IncludesVault && scope.FullVaultAccess,
            _ => false
        };
    }

    /// <summary>Ejecuta una herramienta. Nunca lanza por un fallo de la herramienta: lo devuelve como
    /// resultado con error, para que el modelo lo lea y siga. Sí deja pasar la cancelación del turno.</summary>
    public async Task<AiToolOutcome> ExecuteAsync(string name, JsonElement arguments, AiToolContext context)
    {
        context.Cancellation.ThrowIfCancellationRequested();
        if (!_tools.TryGetValue(name, out var tool))
            return new AiToolOutcome($"Unknown tool '{name}'.", IsError: true);
        if (!IsAvailable(tool, context.Scope))
            return new AiToolOutcome(tool.Effect != AiToolEffect.Read && !ChangesAllowed
                ? "The user has disabled vault changes in Settings: you can only read."
                : $"'{name}' is not available in this chat's scope.", IsError: true);

        var activity = new AiToolActivity { Tool = name };
        context.ActivityReported?.Invoke(activity);
        context.Activity?.Touch();
        try
        {
            var result = await tool.Handler(new AiToolCall(context, arguments, activity));
            // Dentro del try: los resultados llevan secuencias perezosas que se recorren al serializar.
            var json = JsonSerializer.Serialize(result ?? new { Ok = true }, JsonOptions);
            activity.State = AiToolActivityState.Succeeded;
            if (tool.Effect != AiToolEffect.Read) _host.VaultChanged();
            return new AiToolOutcome(json, IsError: false);
        }
        catch (Exception ex) when (Describe(ex) is { } failure)
        {
            activity.State = failure.Kind == AiToolErrorKind.Denied ? AiToolActivityState.Denied : AiToolActivityState.Failed;
            activity.Error = LocalizedTextFormatter.Resolve(failure.UserMessage);
            if (failure.Kind is AiToolErrorKind.Failed or AiToolErrorKind.Unavailable)
                LogService.Warning($"[ia] la herramienta '{name}' falló", ex);
            return new AiToolOutcome(failure.ModelMessage, IsError: true);
        }
        finally
        {
            // Solo sigue "en curso" si el turno se canceló a mitad: no llegó a completarse.
            if (activity.State == AiToolActivityState.Running) activity.State = AiToolActivityState.Failed;
            context.ActivityReported?.Invoke(activity);
            context.Activity?.Touch();
        }
    }

    private sealed record Failure(AiToolErrorKind Kind, string ModelMessage, LocalizedText UserMessage);

    /// <summary>Traduce cualquier fallo esperable de los servicios a lo que necesita oír cada uno: el modelo,
    /// qué pasó y qué puede hacer; el usuario, el motivo en su idioma. La cancelación no se traduce: corta el
    /// turno.</summary>
    private static Failure? Describe(Exception ex)
    {
        static LocalizedText User(AiToolErrorKind kind) => LocalizedTextFormatter.Format("Assistant_ToolError_" + kind);
        return ex switch
        {
            OperationCanceledException => null,
            AiToolException tool => new Failure(tool.Kind, tool.Message, User(tool.Kind)),
            SessionLockedException => new Failure(AiToolErrorKind.Locked,
                "That content belongs to a password-protected session that is locked. Ask the user to unlock it in Enki (Sessions) and try again.",
                User(AiToolErrorKind.Locked)),
            VaultUnavailableException => new Failure(AiToolErrorKind.Unavailable,
                "The vault folder is not available right now (disconnected drive or moved folder).", User(AiToolErrorKind.Unavailable)),
            ExternalModificationException => new Failure(AiToolErrorKind.Conflict,
                "The note changed on disk while you were working on it. Read it again and retry with the current content.",
                User(AiToolErrorKind.Conflict)),
            FileNotFoundException or DirectoryNotFoundException => new Failure(AiToolErrorKind.NotFound,
                "The item no longer exists. List the content again to get current paths.", User(AiToolErrorKind.NotFound)),
            IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
                or ArgumentException or TimeoutException => new Failure(AiToolErrorKind.Failed,
                    "The operation failed: " + ServiceMessage(ex), VaultFailureReasons.DescribeLocalized(ex)),
            _ => null
        };
    }

    /// <summary>Algunos servicios lanzan con el identificador de un recurso como mensaje
    /// (<c>Kanban_Multi_MissingDestination</c>…): se resuelve al texto, que el modelo entiende mejor.</summary>
    private static string ServiceMessage(Exception ex)
    {
        var message = ex.Message;
        if (message.Length > 0 && !message.Contains(' ', StringComparison.Ordinal)
            && Strings.ResourceManager.GetString(message, LocalizationService.Instance.CurrentCulture) is { Length: > 0 } resolved)
            return resolved;
        return message;
    }

    private void Register(string name, AiToolScope scope, AiToolEffect effect, string description, JsonObject schema,
        Func<AiToolCall, object?> handler) =>
        _tools.Add(name, new AiToolDefinition(name, scope, effect, description, schema, call => Task.FromResult(handler(call))));

    private void RegisterAsync(string name, AiToolScope scope, AiToolEffect effect, string description, JsonObject schema,
        Func<AiToolCall, Task<object?>> handler) =>
        _tools.Add(name, new AiToolDefinition(name, scope, effect, description, schema, handler));

    // ===================== Confirmación =====================

    /// <summary>Pide confirmación al usuario y lanza <see cref="AiToolErrorKind.Denied"/> si no la da. Mientras
    /// espera, mantiene vivo el turno para que el límite de inactividad no lo corte.</summary>
    private async Task RequireApprovalAsync(AiToolCall call, LocalizedText title, IReadOnlyList<string> items)
    {
        var context = call.Context;
        if (context.ApprovalRequested is null)
            throw new AiToolException(AiToolErrorKind.Denied, "This action needs the user's confirmation, which is not available here.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation);
        timeout.CancelAfter(ApprovalTimeout);
        using var heartbeat = new Timer(_ => context.Activity?.Touch(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        bool approved;
        try
        {
            approved = await context.ApprovalRequested(new AiApprovalRequest(call.Activity.Tool, title, items), timeout.Token);
        }
        catch (OperationCanceledException) when (!context.Cancellation.IsCancellationRequested)
        {
            approved = false;
        }
        context.Cancellation.ThrowIfCancellationRequested();
        if (!ChangesAllowed)
            throw new AiToolException(AiToolErrorKind.Denied, "The user disabled vault changes while this action was waiting for approval.");
        if (!approved)
            throw new AiToolException(AiToolErrorKind.Denied,
                "The user did not confirm this action, so nothing was done. Do not retry it unless the user asks again.");
    }

    // ===================== Espacios =====================

    private AiWorkspace VaultWorkspace => new(AiWorkspace.VaultId, LocalizationService.Instance["Kanban_NotePicker_VaultWorkspaceLabel"],
        _vault.VaultPath, null);

    /// <summary>Los espacios del ámbito, bloqueados incluidos (se informa de ello al usarlos).</summary>
    private List<AiWorkspace> Workspaces(AiAccessScope scope)
    {
        var result = new List<AiWorkspace>();
        if (!_vault.IsVaultConfigured) return result;
        if (scope.IncludesVault) result.Add(VaultWorkspace);
        foreach (var session in _sessions.List().Where(scope.Includes))
            result.Add(new AiWorkspace(session.Id, session.Name, session.RootPath, session));
        return result;
    }

    /// <summary>Resuelve el argumento <c>workspace</c>: vacío es el espacio del chat, <c>vault</c> la raíz del
    /// vault, y cualquier otro valor el id o el nombre de una sesión del ámbito.</summary>
    private AiWorkspace Workspace(AiToolCall call, string argument = "workspace")
    {
        if (!_vault.IsVaultConfigured) throw new VaultUnavailableException(_vault.VaultPath);
        var scope = call.Context.Scope;
        var requested = call.GetText(argument)?.Trim();
        var workspaces = Workspaces(scope);

        AiWorkspace? match;
        if (string.IsNullOrEmpty(requested))
        {
            match = scope.IsSessionChat
                ? workspaces.FirstOrDefault(w => w.Id == scope.SessionId)
                    ?? throw new AiToolException(AiToolErrorKind.NotFound,
                        "This chat's session no longer exists in the open vault." + (scope.FullVaultAccess ? " Use workspace \"vault\"." : string.Empty))
                : VaultWorkspace;
        }
        else if (string.Equals(requested, AiWorkspace.VaultId, StringComparison.OrdinalIgnoreCase))
        {
            match = scope.IncludesVault ? VaultWorkspace : throw OutsideScope();
        }
        else
        {
            var candidates = workspaces.Where(w => !w.IsVault && (w.Id == requested
                || string.Equals(w.Name, requested, StringComparison.OrdinalIgnoreCase))).ToList();
            if (candidates.Count > 1)
                throw AiToolCall.Invalid($"Several sessions are called \"{requested}\". Use the session id instead: "
                    + string.Join(", ", candidates.Select(c => c.Id)) + ".");
            match = candidates.FirstOrDefault() ?? throw (_sessions.List().Any(s => s.Id == requested
                || string.Equals(s.Name, requested, StringComparison.OrdinalIgnoreCase))
                ? OutsideScope()
                : new AiToolException(AiToolErrorKind.NotFound, $"There is no workspace called \"{requested}\". Call get_workspace_overview to see them."));
        }

        if (match.Session is { } session && !_sessions.IsUnlocked(session))
            throw new AiToolException(AiToolErrorKind.Locked,
                $"The session \"{session.Name}\" is password-protected and locked. Ask the user to unlock it in Enki (Sessions).");
        return match;
    }

    private static AiToolException OutsideScope() => new(AiToolErrorKind.AccessDenied,
        "That workspace is outside this chat's scope. The user can widen it from the scope selector in the chat header.");

    /// <summary>El espacio al que pertenece una ruta absoluta que ya está en el ámbito (una nota vinculada a
    /// una tarjeta, un resultado de búsqueda), o <see langword="null"/> si queda fuera.</summary>
    private AiWorkspace? WorkspaceOf(AiAccessScope scope, string fullPath)
    {
        foreach (var workspace in Workspaces(scope).OrderByDescending(w => w.RootPath.Length))
        {
            if (!VaultService.IsWithinFolder(fullPath, workspace.RootPath)
                || workspace.Session is { } session && !_sessions.IsUnlocked(session)) continue;
            try
            {
                ResolvePath(workspace, RelativePath(workspace, fullPath));
                return workspace;
            }
            catch (AiToolException) { }
        }
        return null;
    }

    // ===================== Rutas =====================

    /// <summary>Convierte una ruta relativa del modelo en una ruta absoluta dentro de <paramref name="workspace"/>,
    /// o la rechaza: rutas absolutas, <c>..</c>, carpetas ocultas o internas (<c>.trash</c>, <c>.sessions</c>,
    /// <c>.enki</c>), carpetas de adjuntos y enlaces simbólicos.</summary>
    private static string ResolvePath(AiWorkspace workspace, string? relative, bool allowRoot = false)
    {
        var cleaned = (relative ?? string.Empty).Replace('\\', '/').Trim();
        if (cleaned.Length == 0 || cleaned == ".")
            return allowRoot ? workspace.RootPath : throw AiToolCall.Invalid("A path is required.");
        if (Path.IsPathRooted(cleaned) || cleaned.Contains(':', StringComparison.Ordinal))
            throw AiToolCall.Invalid("Paths are relative to the workspace root, like \"Projects/Plan.md\".");

        var segments = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.StartsWith('.')
                || segment.EndsWith(NoteImageAttachments.FolderSuffix, StringComparison.OrdinalIgnoreCase))
                throw new AiToolException(AiToolErrorKind.AccessDenied, $"\"{relative}\" points to an internal location that is not accessible.");
        }

        var full = Path.GetFullPath(Path.Combine([workspace.RootPath, .. segments]));
        if (!VaultService.IsWithinFolder(full, workspace.RootPath))
            throw new AiToolException(AiToolErrorKind.AccessDenied, $"\"{relative}\" is outside the workspace.");

        for (var cursor = full; !VaultService.PathsEqual(cursor, workspace.RootPath); cursor = Path.GetDirectoryName(cursor)!)
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new AiToolException(AiToolErrorKind.AccessDenied, $"\"{relative}\" goes through a symbolic link, which is not followed.");
        }
        return full;
    }

    private static string RelativePath(AiWorkspace workspace, string fullPath) =>
        Path.GetRelativePath(workspace.RootPath, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Una carpeta existente del espacio (vacía = la raíz).</summary>
    private static string ExistingFolder(AiWorkspace workspace, string? relative)
    {
        var full = ResolvePath(workspace, relative, allowRoot: true);
        return Directory.Exists(full)
            ? full
            : throw new AiToolException(AiToolErrorKind.NotFound, $"The folder \"{relative}\" does not exist. Create it with create_folder first.");
    }

    // ===================== Fechas =====================

    /// <summary>Fecha de entrada del modelo: <c>2026-10-24</c> es un día entero (medianoche local, la misma
    /// convención que el selector de fechas de la app); <c>2026-10-24T18:30</c>, hora local; con desplazamiento,
    /// ese instante.</summary>
    private static DateTime? Date(AiToolCall call, string name)
    {
        if (call.GetText(name) is not { Length: > 0 } text) return null;
        text = text.Trim();
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var moment))
            return moment.UtcDateTime;
        throw AiToolCall.Invalid($"'{name}' must be an ISO 8601 date (2026-10-24) or local date-time (2026-10-24T18:30).");
    }

    /// <summary>Fecha de salida: un día entero se escribe como día; cualquier otra, con hora local y desplazamiento.</summary>
    private static string? FormatDate(DateTime? utc)
    {
        if (utc is not { } value) return null;
        var local = (value.Kind == DateTimeKind.Local ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToLocalTime();
        return local.TimeOfDay == TimeSpan.Zero
            ? local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : new DateTimeOffset(local).ToString("yyyy-MM-dd'T'HH:mmzzz", CultureInfo.InvariantCulture);
    }

    private static string FormatInstant(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToLocalTime().ToString("yyyy-MM-dd'T'HH:mmzzz", CultureInfo.InvariantCulture);

    private static int Limit(AiToolCall call) => call.GetInteger("limit", 1, MaxListLimit) ?? DefaultListLimit;

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)] + "…";

    // ===================== Esquemas =====================

    private static JsonObject Schema(params (string Name, JsonObject Schema, bool Required)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema, _) in properties) props[name] = schema;
        var schemaObject = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        var required = properties.Where(p => p.Required).Select(p => (JsonNode)JsonValue.Create(p.Name)).ToArray();
        if (required.Length > 0) schemaObject["required"] = new JsonArray(required);
        return schemaObject;
    }

    private static (string, JsonObject, bool) Req(string name, JsonObject schema) => (name, schema, true);
    private static (string, JsonObject, bool) Opt(string name, JsonObject schema) => (name, schema, false);

    private static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Number(string description) => new() { ["type"] = "integer", ["description"] = description };
    private static JsonObject Flag(string description) => new() { ["type"] = "boolean", ["description"] = description };
    private static JsonObject NullableText(string description) =>
        new() { ["type"] = new JsonArray("string", "null"), ["description"] = description };

    private static JsonObject Choice(string description, params string[] values) =>
        new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()), ["description"] = description };

    private static JsonObject TextList(string description) =>
        new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description };

    private static JsonObject ObjectList(string description, JsonObject itemSchema) =>
        new() { ["type"] = "array", ["items"] = itemSchema, ["description"] = description };

    private static (string, JsonObject, bool) WorkspaceParam(string? description = null) => Opt("workspace", Text(description
        ?? "Workspace: omit for this chat's own workspace, \"vault\" for the vault root, or a session name or id."));

    private static (string, JsonObject, bool) LimitParam() => Opt("limit", Number($"Maximum items to return (default {DefaultListLimit}, max {MaxListLimit})."));

    private const string DateDescription = "Date as 2026-10-24 (whole day) or local date-time as 2026-10-24T18:30.";
}
