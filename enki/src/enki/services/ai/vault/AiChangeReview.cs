using System;
using System.Collections.Generic;
using System.Linq;
using enki.Models;

namespace enki.Services;

public enum AiReviewChangeKind
{
    /// <summary>Una nota nueva: se crea al aprobarla.</summary>
    Create,
    /// <summary>Un cambio de contenido de una nota existente (reescribir, editar, añadir, etiquetas o propiedades).</summary>
    Edit
}

public enum AiReviewChangeState
{
    Pending,
    Applied,
    Discarded,
    /// <summary>Estaba aprobado, pero no se pudo aplicar sin pisar nada: <see cref="AiReviewChange.Problem"/> dice por qué.</summary>
    NotApplied
}

/// <summary>Por qué un cambio aprobado no se aplicó. En ningún caso se escribe nada.</summary>
public enum AiReviewProblem
{
    /// <summary>La nota cambió desde que el asistente preparó el cambio (el usuario, un sincronizador…).</summary>
    ChangedSince,
    /// <summary>La nota, o la carpeta donde iba a crearse, ya no existe.</summary>
    Missing,
    /// <summary>Pertenece a una sesión protegida que está bloqueada.</summary>
    Locked,
    /// <summary>La nota está abierta con cambios sin guardar o un conflicto pendiente.</summary>
    Busy,
    /// <summary>Se preparó sobre otro cambio de la misma nota que no se aplicó.</summary>
    Dependency,
    Failed
}

/// <summary>Un cambio de una nota preparado por el asistente y pendiente de la aprobación del usuario. Guarda lo
/// que había y lo que habría, para enseñar el diff, y la huella de partida, para no aplicarlo si la nota cambió
/// mientras tanto. Vive solo en memoria: el contenido de una nota (quizá de una sesión) no se copia al historial
/// del chat.</summary>
public sealed class AiReviewChange
{
    public required AiReviewChangeKind Kind { get; init; }

    /// <summary>La acción del chat que lo pidió. Al aplicarse pasa a "hecha" con su forma de deshacerla; al
    /// descartarse, a <see cref="AiToolActivityState.Discarded"/>.</summary>
    public required AiToolActivity Activity { get; init; }

    /// <summary>Ruta completa de la nota (edición) o de la carpeta donde se creará (creación).</summary>
    public required string FullPath { get; init; }

    public required string Title { get; init; }

    /// <summary>Espacio del ámbito (vault o sesión) y ruta relativa a él, para enseñarlo sin rutas absolutas.</summary>
    public required string WorkspaceName { get; init; }

    public required string RelativePath { get; init; }

    /// <summary>Contenido del que parte el cambio: el del disco, o el que propuso el cambio anterior de la misma
    /// nota en este turno (<see cref="DependsOn"/>). Vacío en una creación.</summary>
    public required string OriginalContent { get; init; }

    public required string ProposedContent { get; init; }

    /// <summary>Hash del archivo cuando se preparó el primer cambio de la nota. Solo en ese primero: los
    /// siguientes comprueban que el anterior se aplicó.</summary>
    public string? BaselineHash { get; init; }

    /// <summary>Cambio anterior de la misma nota en el mismo turno, sobre cuyo resultado se preparó este.</summary>
    public AiReviewChange? DependsOn { get; init; }

    public AiReviewChangeState State { get; internal set; } = AiReviewChangeState.Pending;

    public AiReviewProblem? Problem { get; internal set; }

    public bool IsPending => State == AiReviewChangeState.Pending;
}

/// <summary>Resultado de resolver una revisión: cuántos cambios se aplicaron, cuántos aprobados no se pudieron
/// aplicar y cuántos se descartaron.</summary>
public sealed record AiReviewReport(int Applied, int NotApplied, int Discarded);

/// <summary>Cola de revisión de un turno (AIA-07). Con <see cref="AiSettings.ReviewChangesBeforeApplying"/> encendido,
/// las herramientas que cambian el contenido de una nota preparan aquí el cambio en vez de escribirlo, y el
/// usuario los aprueba o descarta juntos al terminar la respuesta (<see cref="AiVaultToolbox.ResolveReview"/>).
/// Se usa siempre desde el hilo de interfaz, igual que las herramientas.</summary>
public sealed class AiChangeReview
{
    private readonly List<AiReviewChange> _changes = [];

    public IReadOnlyList<AiReviewChange> Changes => _changes;

    public bool HasPending => _changes.Any(c => c.IsPending);

    /// <summary>Se añadió un cambio a la cola.</summary>
    public event Action? Changed;

    /// <summary>El último cambio pendiente de una nota en este turno: lo que verá el modelo al releerla y la base
    /// del siguiente cambio que pida sobre ella.</summary>
    public AiReviewChange? LatestFor(string fullPath) =>
        _changes.LastOrDefault(c => c.Kind == AiReviewChangeKind.Edit && c.IsPending && VaultService.PathsEqual(c.FullPath, fullPath));

    internal void Stage(AiReviewChange change)
    {
        _changes.Add(change);
        Changed?.Invoke();
    }
}
