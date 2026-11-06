using System.IO;

namespace enki.Services;

/// <summary>Thrown by <see cref="VaultService"/> write operations when <see cref="VaultService.VaultPath"/>
/// doesn't currently resolve to an existing directory — e.g. an external drive was unplugged, or the vault
/// folder was moved/deleted out-of-band while Enki kept running. Deriving from <see cref="IOException"/>
/// lets every existing <c>catch (IOException)</c> handler in the app (autosave, note create/rename/move)
/// keep working unchanged while still allowing callers that want a more specific message
/// (docs/analisis-proyecto/roadmap.md, Fase 1) to catch this type first.</summary>
public sealed class VaultUnavailableException : IOException
{
    public VaultUnavailableException(string? vaultPath)
        : base($"El vault '{vaultPath}' no está disponible (la carpeta no existe o no es accesible).")
    {
    }
}
