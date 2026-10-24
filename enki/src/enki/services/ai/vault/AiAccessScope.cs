using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using enki.Models;

namespace enki.Services;

/// <summary>Hasta dónde llega el asistente en un chat. Un chat del vault (<see cref="SessionId"/> nulo, el
/// caso por defecto) ve la raíz del vault y sus módulos —Kanban, Polar, recordatorios, agenda y papelera—,
/// pero ninguna sesión. Un chat abierto dentro de una sesión ve solo el contenido de esa sesión.
/// <see cref="FullVaultAccess"/> amplía cualquiera de los dos al vault entero: "acceso a las sesiones" en un
/// chat del vault, "modo auto" en uno de sesión.
///
/// <para>Las sesiones protegidas con contraseña son la excepción: ni con acceso ampliado entran en el ámbito
/// de un chat que no se abrió dentro de ellas. Una contraseña dice "esto es sensible", y su contenido
/// descifrado no debe acabar en un proveedor de IA sin que el usuario lo pida expresamente.</para></summary>
public sealed record AiAccessScope(string? SessionId, bool FullVaultAccess)
{
    public static AiAccessScope Vault { get; } = new(null, false);

    public bool IsSessionChat => SessionId is not null;

    /// <summary>La raíz del vault y los módulos que cuelgan de él.</summary>
    public bool IncludesVault => !IsSessionChat || FullVaultAccess;

    /// <summary>Si <paramref name="session"/> está dentro del ámbito (aunque esté bloqueada: eso se resuelve
    /// al usarla, con su propio error).</summary>
    public bool Includes(Session session) =>
        string.Equals(session.Id, SessionId, StringComparison.Ordinal) || (FullVaultAccess && !session.IsEncrypted);
}

/// <summary>Un espacio de notas al que puede llegar el asistente: la raíz del vault o una sesión. Las rutas
/// que maneja el modelo son siempre relativas a <see cref="RootPath"/>.</summary>
public sealed record AiWorkspace(string Id, string Name, string RootPath, Session? Session)
{
    public const string VaultId = "vault";

    public bool IsVault => Session is null;
}

/// <summary>Una acción que necesita la confirmación del usuario antes de ejecutarse (borrar algo para
/// siempre, sacar contenido de una sesión protegida). <see cref="Items"/> son las etiquetas de lo afectado.</summary>
public sealed record AiApprovalRequest(string Tool, LocalizedText Title, IReadOnlyList<string> Items);

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

    /// <summary>Mantiene vivo el turno (ver <see cref="CliProcessStartInfo.Activity"/>) mientras se ejecuta una
    /// herramienta o se espera una confirmación.</summary>
    public CliActivitySignal? Activity { get; init; }

    /// <summary>Se cancela al detener la respuesta: corta una confirmación pendiente.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>Huella de cada nota que el modelo leyó entera en este turno, para que reescribirla entera
    /// solo pueda hacerse sobre lo que de verdad vio (ver <c>write_note</c>).</summary>
    internal Dictionary<string, NoteDiskStamp> CompleteReads { get; } = new(VaultService.PathComparison ==
        StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
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

    /// <summary>Tras cualquier acción que cambie algo, para refrescar las pantallas afectadas.</summary>
    public Action VaultChanged { get; init; } = () => { };
}
