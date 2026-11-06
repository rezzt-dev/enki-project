using System.Text.Json;

namespace enki.Models;

/// <summary>Texto de sistema persistible: conserva la clave y sus argumentos sin traducir
/// el contenido del usuario. Los registros antiguos mantienen sus cadenas originales.</summary>
public sealed class LocalizedText
{
    public string? Key { get; set; }
    public string? Literal { get; set; }
    public int? Count { get; set; }
    public string? Separator { get; set; }
    public JsonElement[] Arguments { get; set; } = [];
}
