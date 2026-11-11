using System;
using System.Collections.Generic;

namespace enki.Models;

/// <summary>Preferencias del asistente de IA (Ajustes › Inteligencia artificial). Viven en el
/// <c>settings.json</c> de la instalación —<see cref="Services.SettingsService"/> la trata como clave de
/// instalación—, nunca en el <c>.enki/settings.json</c> del vault: la ruta del CLI, el consentimiento y el
/// historial dependen del equipo, y un vault sincronizado no debe arrastrarlos a otra máquina. Ver
/// <c>.docs/project-info/asistente-ia.md</c>.</summary>
public sealed class AiSettings
{
    /// <summary>Proveedor con el que se abren los chats nuevos. Cada chat guarda el suyo
    /// (<see cref="AiChatSession.Provider"/>) y lo restaura al reabrirse.</summary>
    public AiProvider Provider { get; set; }

    /// <summary>Claude se usa con la clave de API propia del usuario en vez de con la sesión de su suscripción.
    /// El CLI oficial sigue siendo el mismo: solo cambia la credencial con la que se lanza, que se lee del
    /// almacén seguro del sistema (<see cref="Services.AiApiKeyStore"/>) y nunca se guarda aquí.</summary>
    public bool ClaudeUseApi { get; set; }

    public string CodexCliPath { get; set; } = string.Empty;
    public string OpenAiModel { get; set; } = string.Empty;
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
    public string LocalModel { get; set; } = string.Empty;

    /// <summary>Modelo de Gemini para los chats nuevos (<c>gemini-flash-latest</c>, <c>gemini-2.5-pro</c>…). Vacío
    /// para el Flash más reciente que permita la clave. La clave en sí vive en el almacén seguro
    /// (<see cref="Services.AiApiKeyStore"/>), nunca aquí.</summary>
    public string GeminiModel { get; set; } = string.Empty;

    public const double DefaultPanelWidth = 420;
    public const double MinPanelWidth = 340;
    public const double MaxPanelWidth = 720;

    /// <summary>Ancho del panel lateral del asistente, el que se abre junto al contenido sin salir de la
    /// sección en la que se está.</summary>
    public double PanelWidth { get; set; } = DefaultPanelWidth;

    public string ModelFor(AiProvider provider) => provider switch
    {
        AiProvider.OpenAi => OpenAiModel,
        AiProvider.Ollama => LocalModel,
        AiProvider.Gemini => GeminiModel,
        _ => DefaultModel
    };

    public void SetModel(AiProvider provider, string model)
    {
        switch (provider)
        {
            case AiProvider.OpenAi: OpenAiModel = model; break;
            case AiProvider.Ollama: LocalModel = model; break;
            case AiProvider.Gemini: GeminiModel = model; break;
            default: DefaultModel = model; break;
        }
    }
    public const int DefaultIdleTimeoutSeconds = 180;
    public const int MinIdleTimeoutSeconds = 30;
    public const int MaxIdleTimeoutSeconds = 1800;

    public const int DefaultMaxContextChars = 24_000;
    public const int MinMaxContextChars = 2_000;
    public const int MaxMaxContextChars = 200_000;

    /// <summary>Interruptor maestro. Nace apagado: ningún flujo básico depende de la IA y nada sale del
    /// equipo hasta que el usuario lo enciende y acepta el aviso de <see cref="ConsentAcceptedUtc"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Momento en que el usuario aceptó el aviso de privacidad y términos al activar la IA por
    /// primera vez. <see langword="null"/> mientras no lo haya aceptado: encender el interruptor sin él
    /// abre el aviso en vez de activar nada.</summary>
    public DateTime? ConsentAcceptedUtc { get; set; }

    /// <summary>Ruta al ejecutable <c>claude</c> elegida a mano. Vacía para buscarlo en el <c>PATH</c> y en
    /// las ubicaciones de instalación habituales (ver <c>ClaudeCliLocator</c>).</summary>
    public string ClaudeCliPath { get; set; } = string.Empty;

    /// <summary>Alias de modelo por defecto para los chats nuevos (<c>opus</c>, <c>sonnet</c>, <c>haiku</c>…).
    /// Vacío para usar el que tenga configurado el propio CLI.</summary>
    public string DefaultModel { get; set; } = "claude-sonnet-5-5";

    /// <summary>Segundos sin recibir nada del CLI antes de darlo por colgado y cancelar la respuesta. Es un
    /// límite de inactividad, no de duración total: una respuesta larga que sigue llegando no se corta.</summary>
    public int IdleTimeoutSeconds { get; set; } = DefaultIdleTimeoutSeconds;

    /// <summary>Caracteres máximos de contexto (notas adjuntas) por mensaje. Lo que exceda se recorta y el
    /// chip de la nota lo indica.</summary>
    public int MaxContextChars { get; set; } = DefaultMaxContextChars;

    /// <summary>Pide confirmación con un resumen de lo que se va a enviar cuando un mensaje lleva notas
    /// adjuntas. Los mensajes sin contexto se envían directamente.</summary>
    public bool ConfirmBeforeSendingContext { get; set; } = true;

    /// <summary>Deja que el asistente cree, modifique, mueva y envíe a la papelera contenido del vault dentro
    /// del ámbito de cada chat. Apagado, el asistente solo puede consultar: las herramientas que cambian algo
    /// ni siquiera se le ofrecen. Borrar algo para siempre pide además confirmación en el chat, siempre.</summary>
    public bool AllowVaultChanges { get; set; } = true;

    /// <summary>Enseñar en el chat los tokens de cada respuesta y el total de la conversación cuando el
    /// proveedor los informa. Las cifras se guardan igual con el ajuste apagado: solo deja de mostrarlas.</summary>
    public bool ShowUsage { get; set; } = true;

    /// <summary>Revisión de cambios por lotes: los cambios de contenido de notas que pide el asistente (crear,
    /// reescribir, editar, añadir, etiquetas y propiedades) no se escriben al momento, sino que quedan en una
    /// cola con su diff bajo la respuesta para aprobarlos juntos. Apagado por defecto: el asistente aplica lo
    /// que hace y cada respuesta se puede revertir. Se lee al empezar cada turno.</summary>
    public bool ReviewChangesBeforeApplying { get; set; }

    /// <summary>Búsqueda semántica local: un índice de embeddings de las notas, generado con un modelo de
    /// Ollama en este equipo y guardado en la instalación (nunca en el vault ni en la nube). Apagado por defecto;
    /// las sesiones protegidas con contraseña no se indexan nunca. Ver <c>AiSemanticIndexService</c>.</summary>
    public bool SemanticSearchEnabled { get; set; }

    public const string DefaultEmbeddingModel = "nomic-embed-text";

    /// <summary>Modelo de embeddings de Ollama con el que se construye el índice semántico. Cambiarlo obliga a
    /// reconstruir el índice: los vectores de modelos distintos no se pueden comparar.</summary>
    public string EmbeddingModel { get; set; } = DefaultEmbeddingModel;

    /// <summary>Autoriza las consultas del asistente por defecto dentro del ámbito de cada chat, sin pedir
    /// permiso en la primera lectura. Una revocación explícita en un chat prevalece sobre este ajuste.</summary>
    public bool AllowReadAccessByDefault { get; set; }

    /// <summary>Los chats nuevos nacen con el acceso a internet encendido (<see cref="AiChatSession.WebAccess"/>).
    /// Nace apagado: buscar envía el texto de la consulta al buscador elegido y leer una página la descarga
    /// desde este equipo, así que es una decisión del usuario, no un valor por defecto. Cada chat lo cambia
    /// después con el botón del globo del compositor.</summary>
    public bool WebAccessForNewChats { get; set; }

    /// <summary>Buscador con el que el asistente busca en internet. La clave de Brave Search, si se usa, vive
    /// en el almacén seguro (<see cref="Services.AiApiKeyStore"/>), nunca aquí.</summary>
    public AiWebSearchEngine WebSearchEngine { get; set; }

    /// <summary>Último chat abierto en cada vault (clave: <see cref="AiChatSession.VaultId"/>), para volver a
    /// él al entrar de nuevo en la sección. Por vault porque el historial también lo es: el último chat de un
    /// vault no debe abrirse estando en otro.</summary>
    public Dictionary<string, string> LastChatByVault { get; set; } = [];

    /// <summary>Sanea valores imposibles de un archivo editado a mano o de otra versión. Mismos rangos que
    /// los controles de Ajustes.</summary>
    public void Normalize()
    {
        if (!Enum.IsDefined(Provider)) Provider = AiProvider.Claude;
        if (!Enum.IsDefined(WebSearchEngine)) WebSearchEngine = AiWebSearchEngine.DuckDuckGo;
        CodexCliPath = (CodexCliPath ?? string.Empty).Trim();
        OpenAiModel = (OpenAiModel ?? string.Empty).Trim();
        LocalModel = (LocalModel ?? string.Empty).Trim();
        GeminiModel = (GeminiModel ?? string.Empty).Trim();
        EmbeddingModel = string.IsNullOrWhiteSpace(EmbeddingModel) ? DefaultEmbeddingModel : EmbeddingModel.Trim();
        OllamaEndpoint = (OllamaEndpoint ?? string.Empty).Trim().TrimEnd('/');
        ClaudeCliPath = (ClaudeCliPath ?? string.Empty).Trim();
        DefaultModel = (DefaultModel ?? string.Empty).Trim();
        IdleTimeoutSeconds = IdleTimeoutSeconds is < MinIdleTimeoutSeconds or > MaxIdleTimeoutSeconds
            ? DefaultIdleTimeoutSeconds : IdleTimeoutSeconds;
        MaxContextChars = MaxContextChars is < MinMaxContextChars or > MaxMaxContextChars
            ? DefaultMaxContextChars : MaxContextChars;
        PanelWidth = double.IsFinite(PanelWidth) ? Math.Clamp(PanelWidth, MinPanelWidth, MaxPanelWidth) : DefaultPanelWidth;
        LastChatByVault ??= [];
        // Un archivo con la IA encendida pero sin consentimiento registrado no la deja encendida.
        if (ConsentAcceptedUtc is null) Enabled = false;
    }
}

/// <summary>Proveedor del chat. Cero conserva la compatibilidad con los historiales de Claude; los valores
/// nuevos se añaden al final para no reinterpretar los chats ni los ajustes ya guardados.</summary>
public enum AiProvider { Claude, OpenAi, Ollama, Gemini }

/// <summary>Buscador del acceso a internet del asistente. Mismo criterio que <see cref="AiProvider"/>: los
/// valores nuevos se añaden al final.</summary>
public enum AiWebSearchEngine
{
    /// <summary>DuckDuckGo, sin cuenta ni clave: lo que funciona nada más activarlo.</summary>
    DuckDuckGo,
    /// <summary>Brave Search API con la clave del propio usuario: resultados más estables, con nivel gratuito.</summary>
    Brave
}
