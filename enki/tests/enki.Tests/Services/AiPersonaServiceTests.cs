using System;
using System.IO;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public sealed class AiPersonaServiceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    [Fact]
    public void Identity_IsCleanedPersistedAndAnnounced()
    {
        var service = new AiPersonaService(_storageDirectory);
        var changes = 0;
        service.PersonaChanged += () => changes++;

        service.SetIdentity("  Ada \n Lovelace\t ", "  Soy investigadora.\r\nPrefiero listas.  ");

        Assert.Equal(1, changes);
        var reloaded = new AiPersonaService(_storageDirectory);
        Assert.Equal("Ada Lovelace", reloaded.Name);
        Assert.Equal("Soy investigadora.\nPrefiero listas.", reloaded.Persona.Context);
        Assert.True(File.Exists(Path.Combine(_storageDirectory, "ai", "persona.json")));
    }

    [Fact]
    public void SameValues_DoNotRewriteNorAnnounce()
    {
        var service = new AiPersonaService(_storageDirectory);
        service.SetIdentity("Ada", "Contexto");
        var changes = 0;
        service.PersonaChanged += () => changes++;

        service.SetIdentity(" Ada ", "Contexto  ");
        service.SetSessionContext("vault", "s1", "   ");

        Assert.Equal(0, changes);
    }

    [Fact]
    public void Limits_AreEnforced_WithoutSplittingAnEmoji()
    {
        var service = new AiPersonaService(_storageDirectory);
        var longName = new string('a', AiPersona.MaxNameLength - 1) + "😀";

        service.SetIdentity(longName, new string('x', AiPersona.MaxContextLength + 50));

        Assert.Equal(AiPersona.MaxNameLength - 1, service.Persona.Name.Length);
        Assert.Equal(AiPersona.MaxContextLength, service.Persona.Context.Length);
        Assert.Null(new AiPersonaService(Path.Combine(_storageDirectory, "otra")).Name);
    }

    [Fact]
    public void SessionContexts_AreKeptPerVaultAndSession_AndEmptyRemovesThem()
    {
        var service = new AiPersonaService(_storageDirectory);
        service.SetSessionContext("vault-a", "s1", "Tesis doctoral");
        service.SetSessionContext("vault-b", "s1", "Mudanza");

        var reloaded = new AiPersonaService(_storageDirectory);
        Assert.Equal("Tesis doctoral", reloaded.GetSessionContext("vault-a", "s1"));
        Assert.Equal("Mudanza", reloaded.GetSessionContext("vault-b", "s1"));
        Assert.Equal(string.Empty, reloaded.GetSessionContext("vault-a", "s2"));

        reloaded.SetSessionContext("vault-a", "s1", "");
        Assert.Equal(string.Empty, new AiPersonaService(_storageDirectory).GetSessionContext("vault-a", "s1"));
    }

    [Fact]
    public void CorruptFile_FallsBackToAnEmptyPersona()
    {
        var path = Path.Combine(_storageDirectory, "ai", "persona.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ no es json");

        var service = new AiPersonaService(_storageDirectory);

        Assert.True(service.Persona.IsEmpty);
        Assert.Null(service.Name);
    }

    [Fact]
    public void FailedSave_RollsBackTheInMemoryState()
    {
        var service = new AiPersonaService(_storageDirectory);
        service.SetIdentity("Ada", "Contexto");
        BlockPersonaFile(_storageDirectory);

        Assert.ThrowsAny<IOException>(() => service.SetIdentity("Otra", "Nuevo"));
        Assert.ThrowsAny<IOException>(() => service.SetSessionContext("v", "s", "Algo"));

        Assert.Equal("Ada", service.Name);
        Assert.Equal("Contexto", service.Persona.Context);
        Assert.Equal(string.Empty, service.GetSessionContext("v", "s"));
    }

    /// <summary>Sustituye <c>persona.json</c> por una carpeta con el mismo nombre: el reemplazo atómico falla
    /// igual en todos los sistemas, a diferencia de quitar permisos (Enki los restaura en su carpeta privada).</summary>
    internal static void BlockPersonaFile(string storageDirectory)
    {
        var path = Path.Combine(storageDirectory, "ai", "persona.json");
        if (File.Exists(path)) File.Delete(path);
        Directory.CreateDirectory(Path.Combine(path, "bloqueo"));
    }

    [Fact]
    public void ClearingChatHistory_KeepsThePersona()
    {
        var chats = new AiChatService(_storageDirectory);
        chats.Persona.SetIdentity("Ada", "Contexto");

        chats.DeleteAll();

        Assert.Equal("Ada", new AiChatService(_storageDirectory).Persona.Name);
    }
}
