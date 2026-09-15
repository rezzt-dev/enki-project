using System;
using System.Collections.Generic;
using enki.Models;

namespace enki.Services;

/// <summary>Paleta de colores del Calendario (docs/calendar-feature/07-filtros-busqueda-y-capas.md §1b)
/// — el catálogo de colores por defecto que diferencia visualmente de dónde viene cada item del
/// calendario. Estático y puro: no lee ajustes ni persiste nada (de eso se encarga
/// <see cref="CalendarLayerColorService"/>), así que es directamente testeable.
///
/// Todos los tonos están calculados con la misma luminancia relativa (~0.22) para que el mismo hex
/// funcione en los dos temas integrados y en los personalizados: ~3.2:1 de contraste sobre el fondo
/// claro (<c>#EAEAEA</c>) y ~4.4:1 sobre el oscuro (<c>#1B1B1B</c>). Un tono "bonito" en oscuro elegido
/// a ojo suele ser ilegible en claro (y al revés), y las pastillas del calendario pintan el color a
/// opacidad plena en icono/texto, no solo como relleno tenue.</summary>
public static class CalendarLayerPalette
{
    /// <summary>Rojo/ámbar/verde reservados para los tres roles core del tablero Kanban — pendientes,
    /// en proceso y realizadas. <see cref="AccentPool"/> los excluye a propósito para que un color de
    /// lista de Polar nunca se confunda con un estado de Kanban.</summary>
    public const string BacklogColorHex = "#D5585C";
    public const string InProgressColorHex = "#B28426";
    public const string DoneColorHex = "#3E9767";

    /// <summary>Colores por defecto de las listas de Polar y de las columnas que crea el usuario: ni
    /// rojos, ni amarillos, ni verdes (esos son los tres roles core de arriba). Ordenados saltando por la
    /// rueda de tonos, no recorriéndola, para que dos capas consecutivas no acaben con dos azules
    /// contiguos.
    ///
    /// Son ocho y no más a propósito. Excluir rojos, amarillos y verdes deja poco más de 130° de rueda
    /// (cian → azul → violeta → magenta), y meter doce tonos ahí los dejaba a ~11° unos de otros: en una
    /// pastilla del mes, donde el color se pinta al 8% de relleno y 40% de borde, dos listas contiguas se
    /// volvían indistinguibles justo en el tema claro. Con ocho, la distancia perceptual mínima entre dos
    /// acentos es del doble, y cada uno está además a más del doble de esa distancia de
    /// <see cref="BacklogColorHex"/> (el caso peligroso: un rosa que de lejos parece el rojo de
    /// "pendientes"). Con más de ocho capas la paleta se repite, que es preferible a colarse en las
    /// franjas reservadas o fuera del rango de luminancia validado para los dos temas.</summary>
    public static readonly IReadOnlyList<string> AccentPool =
    [
        "#328BA7", // cian
        "#5980CD", // azul
        "#C552C5", // magenta
        "#5B86A7", // acero
        "#8D76B5", // malva
        "#B963A5", // ciruela
        "#3785CA", // azul cielo
        "#AB61D5"  // violeta
    ];


    /// <summary>Los 15 colores que ofrece la rejilla del selector: los tres roles de Kanban, el índigo de
    /// recordatorios y los once acentos. El usuario no está limitado a estos (el campo hex acepta
    /// cualquier color), son solo los que se proponen con un clic.</summary>
    public static readonly IReadOnlyList<string> PickerSwatches =
    [
        BacklogColorHex, InProgressColorHex, DoneColorHex, ViewModels.CalendarItemViewModel.ReminderLayerColorHex,
        .. AccentPool
    ];

    /// <summary>Color por defecto de una columna core (rol explícito) — <c>null</c> para una columna
    /// creada por el usuario, que toma su color de <see cref="AssignAccents"/>.</summary>
    public static string? DefaultForRole(CoreColumnRole role) => role switch
    {
        CoreColumnRole.Backlog => BacklogColorHex,
        CoreColumnRole.InProgress => InProgressColorHex,
        CoreColumnRole.Done => DoneColorHex,
        _ => null
    };

    /// <summary>Reparte <see cref="AccentPool"/> entre <paramref name="keys"/> de forma determinista:
    /// el índice de partida sale de un hash FNV-1a del propio identificador (estable entre arranques,
    /// a diferencia de <c>string.GetHashCode</c>, que .NET aleatoriza por proceso) y a partir de ahí se
    /// prueba el siguiente hueco libre. Así un color "aleatorio" no cambia al reiniciar ni se repite
    /// mientras haya colores disponibles, que es lo que realmente pide el usuario ("colores diferentes"),
    /// y no depende de la posición en la lista: renombrar o borrar otra capa no le cambia el color a las
    /// demás.</summary>
    /// <param name="reservedColors">Colores ya ocupados por capas con color elegido a mano — se apartan
    /// del reparto para que un color automático no acabe siendo idéntico a uno que el usuario ya fijó.</param>
    public static Dictionary<string, string> AssignAccents(IEnumerable<string> keys, IEnumerable<string>? reservedColors = null)
    {
        var assigned = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<int>();

        if (reservedColors is not null)
        {
            foreach (var color in reservedColors)
            {
                var reserved = IndexOfPoolColor(color);
                // Si el usuario fijó a mano *todos* los colores del pool no queda nada que repartir, así
                // que la reserva se ignora en ese caso y los automáticos vuelven a poder repetirlos.
                if (reserved >= 0 && used.Count < AccentPool.Count - 1) used.Add(reserved);
            }
        }

        foreach (var key in keys)
        {
            if (string.IsNullOrEmpty(key) || assigned.ContainsKey(key)) continue;

            var start = (int)(StableHash(key) % (uint)AccentPool.Count);
            var index = start;
            // Solo se sondea una vuelta completa: con más capas que colores la paleta se repite, que es
            // preferible a inventar tonos fuera del rango de luminancia validado para ambos temas.
            for (var offset = 0; offset < AccentPool.Count && !used.Add(index); offset++)
                index = (start + offset + 1) % AccentPool.Count;

            assigned[key] = AccentPool[index];
        }

        return assigned;
    }

    private static int IndexOfPoolColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return -1;
        for (var i = 0; i < AccentPool.Count; i++)
        {
            if (string.Equals(AccentPool[i], hex, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>FNV-1a de 32 bits sobre las unidades UTF-16 de la cadena.</summary>
    private static uint StableHash(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var c in value)
        {
            hash = (hash ^ (byte)(c & 0xFF)) * prime;
            hash = (hash ^ (byte)(c >> 8)) * prime;
        }
        return hash;
    }
}
