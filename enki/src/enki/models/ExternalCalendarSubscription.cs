using System;
using System.Text.Json.Serialization;

namespace enki.Models;

/// <summary>Fuente iCalendar remota de solo lectura. La URL puede contener un token privado y se
/// guarda exclusivamente en el almacén local de la instalación, nunca dentro del vault.</summary>
public sealed class ExternalCalendarSubscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string CachedIcs { get; set; } = string.Empty;
    public string? ETag { get; set; }
    public DateTime? LastUpdatedUtc { get; set; }
    [JsonIgnore]
    public string LastUpdatedLocalLabel => LastUpdatedUtc?.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "—";
}
