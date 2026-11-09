using System;
using System.Collections.Generic;
using System.IO;
using enki.Models;

namespace enki.Services;

/// <summary>Personalidad del asistente (<see cref="AiPersona"/>): nombre, contexto general y contexto por
/// sesión, en <c>%AppData%/Enki/ai/persona.json</c> (<c>.devdata/ai/persona.json</c> en Debug), junto al
/// historial de chats y con los mismos permisos privados. Cada cambio se guarda en el momento y, si el disco
/// falla, el estado en memoria vuelve a lo que había para que la interfaz no enseñe algo que no se guardó. Ver
/// <c>.docs/project-info/asistente-ia.md</c>, sección «personalidad».</summary>
public sealed class AiPersonaService
{
    private readonly string _path;
    private AiPersona? _persona;

    /// <summary>Se dispara tras guardar cualquier cambio de la personalidad.</summary>
    public event Action? PersonaChanged;

    /// <param name="storageDirectory">Sustituye a <c>%AppData%/Enki</c> como raíz — para los tests.</param>
    public AiPersonaService(string? storageDirectory = null)
    {
        _path = Path.Combine(storageDirectory ?? AppDataDirectory.Resolve(), "ai", "persona.json");
    }

    /// <summary>Personalidad guardada. Se lee de disco la primera vez que se pide.</summary>
    public AiPersona Persona => _persona ??= Load();

    /// <summary>Nombre elegido, o <see langword="null"/> si el usuario no le ha puesto ninguno.</summary>
    public string? Name => Persona.Name.Length > 0 ? Persona.Name : null;

    private AiPersona Load()
    {
        if (!File.Exists(_path) || !JsonFileStore.TryLoad<AiPersona>(_path, out var persona) || persona is null)
            return new AiPersona();
        persona.Normalize();
        return persona;
    }

    /// <summary>Cambia el nombre y el contexto general. Lanza <see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/> o <see cref="TimeoutException"/> (otro proceso de Enki tiene el
    /// archivo bloqueado) si no se puede guardar: el llamador decide cómo avisar.</summary>
    public void SetIdentity(string? name, string? context)
    {
        var persona = Persona;
        var cleanName = AiPersona.CleanName(name);
        var cleanContext = AiPersona.CleanText(context, AiPersona.MaxContextLength);
        if (cleanName == persona.Name && cleanContext == persona.Context) return;

        var (previousName, previousContext) = (persona.Name, persona.Context);
        persona.Name = cleanName;
        persona.Context = cleanContext;
        SaveOrRollback(() =>
        {
            persona.Name = previousName;
            persona.Context = previousContext;
        });
    }

    public string GetSessionContext(string vaultId, string sessionId) =>
        Persona.SessionContexts.GetValueOrDefault(AiPersona.SessionKey(vaultId, sessionId)) ?? string.Empty;

    /// <summary>Cambia (o, vacío, borra) el contexto de una sesión. Mismas excepciones que
    /// <see cref="SetIdentity"/>.</summary>
    public void SetSessionContext(string vaultId, string sessionId, string? context)
    {
        var persona = Persona;
        var key = AiPersona.SessionKey(vaultId, sessionId);
        var clean = AiPersona.CleanText(context, AiPersona.MaxScopedContextLength);
        var hadPrevious = persona.SessionContexts.TryGetValue(key, out var previous);
        if ((previous ?? string.Empty) == clean) return;

        if (clean.Length == 0) persona.SessionContexts.Remove(key);
        else persona.SessionContexts[key] = clean;
        SaveOrRollback(() =>
        {
            if (hadPrevious) persona.SessionContexts[key] = previous!;
            else persona.SessionContexts.Remove(key);
        });
    }

    private void SaveOrRollback(Action rollback)
    {
        try
        {
            PrivateDataPermissions.EnsurePrivateDirectory(Path.GetDirectoryName(_path)!);
            JsonFileStore.Save(_path, Persona);
        }
        catch
        {
            rollback();
            throw;
        }
        PersonaChanged?.Invoke();
    }
}
