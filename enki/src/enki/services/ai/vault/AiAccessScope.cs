using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using enki.Models;

namespace enki.Services;

/// <summary>Hasta dónde llega el asistente en un chat. Un chat del vault (<see cref="SessionId"/> nulo, el
/// caso por defecto) ve la raíz del vault y sus módulos —Kanban, Polar, recordatorios, agenda y papelera—,
/// pero ninguna sesión. Un chat abierto dentro de una sesión ve solo el contenido de esa sesión.
/// <see cref="FullVaultAccess"/> permite a un chat del vault consultar las sesiones sin protección.
/// En un chat de sesión se ignora siempre, incluso si viene activado en un historial antiguo: su ámbito
/// no puede ampliarse a otros espacios.
///
/// <para>Las sesiones protegidas con contraseña son la excepción: ni con acceso ampliado entran en el ámbito
/// de un chat que no se abrió dentro de ellas. Una contraseña dice "esto es sensible", y su contenido
/// descifrado no debe acabar en un proveedor de IA sin que el usuario lo pida expresamente.</para></summary>
public sealed record AiAccessScope(string? SessionId, bool FullVaultAccess)
{
    public static AiAccessScope Vault { get; } = new(null, false);

    public bool IsSessionChat => SessionId is not null;

    /// <summary>La raíz del vault y los módulos que cuelgan de él.</summary>
    public bool IncludesVault => !IsSessionChat;

    /// <summary>Si <paramref name="session"/> está dentro del ámbito (aunque esté bloqueada: eso se resuelve
    /// al usarla, con su propio error).</summary>
    public bool Includes(Session session) =>
        string.Equals(session.Id, SessionId, StringComparison.Ordinal) || (!IsSessionChat && FullVaultAccess && !session.IsEncrypted);
}

/// <summary>Un espacio de notas al que puede llegar el asistente: la raíz del vault o una sesión. Las rutas
/// que maneja el modelo son siempre relativas a <see cref="RootPath"/>.</summary>
public sealed record AiWorkspace(string Id, string Name, string RootPath, Session? Session)
{
    public const string VaultId = "vault";

    public bool IsVault => Session is null;
}

/// <summary>Una acción que necesita la confirmación del usuario antes de ejecutarse (borrar algo para
/// siempre, sacar contenido de una sesión protegida, abrir una dirección web desconocida). <see cref="Items"/> son
/// las etiquetas de lo afectado; <see cref="Hint"/>, una explicación propia en vez de la genérica de la tarjeta.</summary>
public sealed record AiApprovalRequest(string Tool, LocalizedText Title, IReadOnlyList<string> Items, LocalizedText? Hint = null);

/// <summary>Lo que el servidor MCP sabe de un turno: el ámbito del chat y cómo avisar a la conversación. Se
/// registra al empezar el turno con un token propio (ver <see cref="EnkiMcpServer.Register"/>) y se retira al
/// terminar, así que un CLI que siguiera vivo después no podría hacer nada más.</summary>
public sealed class AiToolContext
{
    public required AiAccessScope Scope { get; init; }

    /// <summary>Se invoca al empezar cada acción y cada vez que cambia su estado, siempre con la misma
    /// instancia de <see cref="AiToolActivity"/>, desde el hilo en el que se ejecutan las herramientas.</summary>
    public Action<AiToolActivity>? ActivityReported { get; init; }

    /// <summary>Pide confirmación al usuario. Sin él, las acciones que la necesitan se deniegan.</summary>
    public Func<AiApprovalRequest, CancellationToken, Task<bool>>? ApprovalRequested { get; init; }

    /// <summary>Si el chat permite buscar y leer contenido por su cuenta. El asistente siempre pasa uno ligado
    /// al permiso del chat; <see cref="AiReadAccess.Unrestricted"/> queda para quien compone el servidor sin
    /// chat detrás (pruebas de las herramientas).</summary>
    public AiReadAccess ReadAccess { get; init; } = AiReadAccess.Unrestricted;

    /// <summary>Mantiene vivo el turno (ver <see cref="CliProcessStartInfo.Activity"/>) mientras se ejecuta una
    /// herramienta o se espera una confirmación.</summary>
    public CliActivitySignal? Activity { get; init; }

    /// <summary>Se cancela al detener la respuesta: corta una confirmación pendiente.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>Si el chat tiene encendido el acceso a internet, leído en vivo: apagarlo a mitad de una
    /// respuesta ya impide la búsqueda o la lectura siguiente. Apagado por defecto.</summary>
    public Func<bool> WebAccess { get; init; } = () => false;

    /// <summary>Direcciones que el modelo puede abrir sin preguntar: las que el usuario escribió en el chat y las
    /// que salieron de resultados de búsqueda o de páginas ya leídas. Cualquier otra pide confirmación, para que
    /// una instrucción escondida en una página no pueda sacar datos del vault metiéndolos en una URL inventada.</summary>
    public AiWebUrlSet KnownWebUrls { get; init; } = new();

    /// <summary>Rutas absolutas de los archivos que el usuario adjuntó al mensaje de este turno. Ya los compartió
    /// él, así que <c>read_file</c> los puede abrir (por ejemplo, para ver una imagen adjunta) sin el permiso de
    /// consulta del chat; cualquier otro archivo sigue necesitándolo.</summary>
    public IReadOnlySet<string> AttachedFiles { get; init; } = new HashSet<string>();

    /// <summary>Cola de revisión del turno cuando el usuario quiere aprobar los cambios antes de aplicarlos
    /// (<see cref="AiSettings.ReviewChangesBeforeApplying"/>): los cambios de contenido de notas se preparan en
    /// ella en vez de escribirse. <see langword="null"/>, se aplican al momento.</summary>
    public AiChangeReview? Review { get; init; }

    /// <summary>Skills leídas completas en este turno: su contenido original permite detectar conflictos.</summary>
    internal Dictionary<string, SkillFile> SkillReads { get; } = new(StringComparer.Ordinal);

    /// <summary>Huella de cada nota que el modelo leyó entera en este turno, para que reescribirla entera
    /// solo pueda hacerse sobre lo que de verdad vio (ver <c>write_note</c>).</summary>
    internal Dictionary<string, NoteDiskStamp> CompleteReads { get; } = new(VaultService.PathComparison ==
        StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}

/// <summary>Permiso de un chat para que el asistente busque y lea contenido del ámbito más allá de lo adjuntado.
/// Lo comprueba <see cref="AiVaultToolbox"/> antes de cada herramienta de lectura, no el prompt: un modelo que
/// lo ignore no lee nada. Si no está concedido se pide una vez —las lecturas simultáneas comparten la misma
/// petición— y una negativa vale para el resto del turno, para no repetir la pregunta en bucle.</summary>
public sealed class AiReadAccess(Func<bool> isGranted, Func<CancellationToken, Task<bool>>? request)
{
    public static AiReadAccess Unrestricted => new(() => true, null);

    private Task<bool>? _pending;

    /// <summary>El permiso, leído en vivo: revocarlo a mitad de un turno ya impide la lectura siguiente.</summary>
    public bool IsGranted => isGranted();

    /// <summary>El usuario dijo que no (o dejó pasar la pregunta) en este turno.</summary>
    public bool DeniedThisTurn { get; private set; }

    /// <summary>Espera a que el permiso esté concedido, preguntando si hace falta mediante
    /// <paramref name="wait"/>, que envuelve la petición con el tiempo máximo y el latido del turno.</summary>
    internal async Task<bool> EnsureAsync(Func<Func<CancellationToken, Task<bool>>, Task<bool>> wait)
    {
        if (IsGranted) return true;
        if (DeniedThisTurn || request is null) return false;
        var pending = _pending ??= wait(request);
        bool granted;
        try
        {
            granted = await pending;
        }
        finally
        {
            // La primera lectura que termina retira la petición; las demás que la compartían ya tienen su copia.
            if (ReferenceEquals(_pending, pending)) _pending = null;
        }
        if (!granted || !IsGranted) DeniedThisTurn = true;
        return !DeniedThisTurn;
    }
}

/// <summary>Lo que la interfaz necesita saber cuando el asistente toca una nota, para que una pestaña abierta
/// del editor no se quede atrás ni pise el cambio. Lo implementa <c>NotesViewModel</c>; las mismas reglas que
/// aplica al renombrar, mover o borrar desde la propia app.</summary>
public sealed class AiVaultHost
{
    /// <summary>Antes de modificar, mover o borrar la nota de esa ruta: vuelca lo que su pestaña tenga sin
    /// guardar y devuelve <see langword="false"/> si hay un conflicto sin resolver, en cuyo caso no se toca.</summary>
    public Func<string, bool> PrepareNoteChange { get; init; } = _ => true;

    /// <summary>Después: (ruta anterior, nota resultante). Misma ruta para un cambio de contenido, otra para un
    /// renombrado o traslado, y <see langword="null"/> si se borró.</summary>
    public Action<string, NoteFile?> NoteChanged { get; init; } = (_, _) => { };

    /// <summary>Antes de mandar una sesión a la papelera: cierra las pestañas de sus notas y devuelve
    /// <see langword="false"/> si alguna no se puede cerrar sin perder cambios.</summary>
    public Func<Session, bool> PrepareSessionRemoval { get; init; } = _ => true;

    /// <summary>Se creó, renombró, borró o restauró una sesión: los selectores de sesión de Notas y Archivos
    /// tienen que volver a leerlas.</summary>
    public Action SessionsChanged { get; init; } = () => { };

    /// <summary>Antes de un cambio que reescribe muchas notas a la vez (renombrar o fusionar una etiqueta en todo
    /// el ámbito): vuelca lo que las pestañas tengan sin guardar y devuelve <see langword="false"/> si alguna
    /// sigue con cambios que no se pudieron guardar, en cuyo caso no se toca nada. Mismo criterio que el Gestor
    /// de etiquetas.</summary>
    public Func<bool> PrepareBulkNoteChange { get; init; } = () => true;

    /// <summary>Después de ese cambio masivo: las pestañas abiertas releen sus etiquetas y las pantallas que las
    /// enseñan (Notas, Kanban, Polar) se recargan.</summary>
    public Action BulkNotesChanged { get; init; } = () => { };

    /// <summary>Tras cualquier acción que cambie algo, para refrescar las pantallas afectadas.</summary>
    public Action VaultChanged { get; init; } = () => { };

    // ----- Navegación: el asistente enseña algo en la interfaz, sin cambiar datos -----
    // Las tres devuelven false si la sección de destino está desactivada (perfil de uso) o no hay interfaz
    // a la que llevar; la herramienta lo traduce a "no disponible".

    /// <summary>Abre una nota en Documentos.</summary>
    public Func<NoteFile, bool> OpenNote { get; init; } = _ => false;

    /// <summary>Abre un tablero de Kanban (por id) y, si se indica, el detalle de una de sus tarjetas.</summary>
    public Func<string, string?, bool> OpenKanbanBoard { get; init; } = (_, _) => false;

    /// <summary>Lleva a una sección por su clave (ver <c>AiVaultToolbox.Sections</c>).</summary>
    public Func<string, bool> OpenSection { get; init; } = _ => false;
}

/// <summary>Conjunto de direcciones web comparadas sin fragmento ni barra final y con el host en minúsculas
/// (<see cref="AiToolContext.KnownWebUrls"/>). Se usa siempre desde el hilo de las herramientas.</summary>
public sealed partial class AiWebUrlSet
{
    private readonly HashSet<string> _urls = new(StringComparer.Ordinal);

    public int Count => _urls.Count;

    public void Add(string url)
    {
        if (Normalize(url) is { } key) _urls.Add(key);
    }

    public void Add(Uri url) => Add(url.AbsoluteUri);

    public void AddRange(IEnumerable<string> urls)
    {
        foreach (var url in urls) Add(url);
    }

    /// <summary>Añade las direcciones que aparezcan escritas en <paramref name="text"/> (un mensaje del usuario).</summary>
    public void AddFrom(string text)
    {
        foreach (System.Text.RegularExpressions.Match match in UrlRegex().Matches(text))
            Add(match.Value.TrimEnd('.', ',', ';', ':', '!', '?', '»', '"', '\''));
    }

    public bool Contains(Uri url) => Normalize(url.AbsoluteUri) is { } key && _urls.Contains(key);

    private static string? Normalize(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var path = uri.AbsolutePath.Length > 1 ? uri.AbsolutePath.TrimEnd('/') : string.Empty;
        return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{(uri.IsDefaultPort ? string.Empty : ":" + uri.Port)}{path}{uri.Query}";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"https?://[^\s<>()\[\]{}""'`]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex UrlRegex();
}
