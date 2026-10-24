using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using enki.Models;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;

namespace enki.Services;

/// <summary>Gestiona suscripciones iCalendar locales y actualiza una instantánea de solo lectura.
/// Un fallo de red o de formato conserva la última instantánea válida.</summary>
public sealed class ExternalCalendarService : IDisposable
{
    private const int MaxFeedBytes = 5 * 1024 * 1024;
    private const int MaxEventSpanDays = 366;
    private readonly string _path;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly List<ExternalCalendarSubscription> _subscriptions;
    private readonly Dictionary<string, Calendar> _parsed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastAttemptUtc = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedFeeds = new(StringComparer.Ordinal);

    public event Action? DataChanged;
    public event Action<string>? RefreshFailed;
    public IReadOnlyList<ExternalCalendarSubscription> Subscriptions => _subscriptions;

    public ExternalCalendarService(string? storageDirectory = null, HttpClient? client = null)
    {
        var directory = storageDirectory ?? AppDataDirectory.Resolve();
        try { PrivateDataPermissions.EnsurePrivateDirectory(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning("No se pudieron restringir los permisos del almacén de calendarios externos", ex);
            Directory.CreateDirectory(directory);
        }
        _path = Path.Combine(directory, "external-calendars.json");
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _subscriptions = JsonFileStore.TryLoad<List<ExternalCalendarSubscription>>(_path, out var stored)
            ? stored!.Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Id) &&
                    !string.IsNullOrWhiteSpace(s.Name) && IsValidUrl(s.Url))
                .GroupBy(s => s.Id, StringComparer.Ordinal).Select(g => g.First()).ToList()
            : [];
        foreach (var feed in _subscriptions)
        {
            if (string.IsNullOrWhiteSpace(feed.CachedIcs)) { feed.ETag = null; continue; }
            try { _parsed[feed.Id] = Parse(feed.CachedIcs); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or InvalidDataException)
            {
                LogService.Warning("La caché de un calendario externo está dañada", ex);
                feed.ETag = null;
            }
        }
    }

    private static string NormalizeUrl(string text) => text.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)
        ? "https://" + text[9..] : text;

    public static bool IsValidUrl(string? text) => text is not null
        && Uri.TryCreate(NormalizeUrl(text), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(uri.Host)
        && uri.UserInfo.Length == 0;

    public ExternalCalendarSubscription Add(string name, string url)
    {
        name = name.Trim();
        url = NormalizeUrl(url.Trim());
        if (name.Length == 0 || name.Length > 80) throw new ArgumentException("Indica un nombre de hasta 80 caracteres.");
        if (url.Length > 2048 || !IsValidUrl(url)) throw new ArgumentException("Introduce una URL HTTPS de iCalendar válida.");
        if (_subscriptions.Any(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Este calendario ya está suscrito.");
        var feed = new ExternalCalendarSubscription { Name = name, Url = url };
        _subscriptions.Add(feed);
        try { Save(); }
        catch { _subscriptions.Remove(feed); throw; }
        return feed;
    }

    public void Remove(string id)
    {
        var index = _subscriptions.FindIndex(s => s.Id == id);
        if (index < 0) return;
        var feed = _subscriptions[index];
        _subscriptions.RemoveAt(index);
        try { Save(); }
        catch { _subscriptions.Insert(index, feed); throw; }
        _parsed.Remove(id);
        _lastAttemptUtc.Remove(id);
        _failedFeeds.Remove(id);
    }

    public async Task RefreshAllAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var feed in _subscriptions.ToArray())
            {
                if (!force && _lastAttemptUtc.TryGetValue(feed.Id, out var attempted) && DateTime.UtcNow - attempted < TimeSpan.FromMinutes(15)) continue;
                if (!force && feed.LastUpdatedUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromMinutes(15)) continue;
                _lastAttemptUtc[feed.Id] = DateTime.UtcNow;
                try
                {
                    await RefreshCoreAsync(feed, cancellationToken);
                    _failedFeeds.Remove(feed.Id);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or ArgumentException or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException or TimeoutException)
                {
                    LogService.Warning($"No se pudo actualizar el calendario externo '{feed.Name}' ({ex.GetType().Name})");
                    if (_failedFeeds.Add(feed.Id)) RefreshFailed?.Invoke(feed.Name);
                }
            }
        }
        finally { _refreshLock.Release(); }
    }

    public async Task RefreshAsync(string id, CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var feed = _subscriptions.FirstOrDefault(s => s.Id == id);
            if (feed is not null)
            {
                _lastAttemptUtc[feed.Id] = DateTime.UtcNow;
                await RefreshCoreAsync(feed, cancellationToken);
                _failedFeeds.Remove(feed.Id);
            }
        }
        finally { _refreshLock.Release(); }
    }

    private async Task RefreshCoreAsync(ExternalCalendarSubscription feed, CancellationToken token)
    {
        using var response = await SendFollowingHttpsRedirectsAsync(feed, token);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (!_parsed.ContainsKey(feed.Id)) throw new InvalidDataException("El servidor no ha enviado un calendario y no hay una copia local.");
            feed.LastUpdatedUtc = DateTime.UtcNow;
            Save();
            return;
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxFeedBytes)
            throw new InvalidDataException("El calendario supera el límite de 5 MB.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + read > MaxFeedBytes) throw new InvalidDataException("El calendario supera el límite de 5 MB.");
            memory.Write(buffer, 0, read);
        }
        var content = System.Text.Encoding.UTF8.GetString(memory.ToArray());
        var calendar = Parse(content);
        feed.CachedIcs = content;
        feed.ETag = response.Headers.ETag?.ToString();
        feed.LastUpdatedUtc = DateTime.UtcNow;
        _parsed[feed.Id] = calendar;
        Save();
    }

    private async Task<HttpResponseMessage> SendFollowingHttpsRedirectsAsync(ExternalCalendarSubscription feed, CancellationToken token)
    {
        var target = new Uri(feed.Url);
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            if (EntityTagHeaderValue.TryParse(feed.ETag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            if (response.Headers.Location is not { } location || redirects == 3)
            {
                response.Dispose();
                throw new InvalidDataException("El calendario ha redirigido demasiadas veces.");
            }
            var next = new Uri(target, location);
            response.Dispose();
            if (!IsValidUrl(next.ToString())) throw new InvalidDataException("El calendario redirige a una URL no segura.");
            target = next;
        }
        throw new InvalidDataException("El calendario ha redirigido demasiadas veces.");
    }

    private static Calendar Parse(string content)
    {
        if (!content.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("La respuesta no contiene un calendario iCalendar.");
        try { return Calendar.Load(content) ?? throw new InvalidDataException("No se pudo leer el calendario iCalendar."); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or InvalidDataException))
        {
            throw new InvalidDataException("No se pudo leer el calendario iCalendar.", ex);
        }
    }

    /// <summary>Expande recurrencias dentro de una ventana finita. Incluye eventos que empiezan antes
    /// del rango y terminan dentro de él (hasta un año de duración).</summary>
    public List<(ExternalCalendarSubscription Feed, string Uid, string Title, string Description,
        DateTime StartUtc, int? DurationMinutes, bool IsAllDay)> GetEvents(DateTime startUtc, DateTime endUtc)
    {
        var events = new List<(ExternalCalendarSubscription Feed, string Uid, string Title, string Description,
            DateTime StartUtc, int? DurationMinutes, bool IsAllDay)>();
        var from = new CalDateTime(startUtc.AddDays(-MaxEventSpanDays));
        foreach (var feed in _subscriptions)
        {
            if (!_parsed.TryGetValue(feed.Id, out var calendar)) continue;
            try
            {
                var count = 0;
                foreach (var occurrence in calendar.GetOccurrences(from,
                         new EvaluationOptions { MaxUnmatchedIncrementsLimit = 5000 }))
                {
                    var source = occurrence.Source as CalendarEvent;
                    if (source is null) continue;
                    var rawStart = occurrence.Period.StartTime;
                    var eventStart = source.IsAllDay ? rawStart.Value.Date.ToUniversalTime()
                        : rawStart.IsFloating ? rawStart.Value.ToUniversalTime() : rawStart.AsUtc;
                    if (eventStart >= endUtc || count++ >= 5000) break;
                    if (!source.IsActive) continue;
                    var rawEnd = occurrence.Period.EffectiveEndTime;
                    var eventEnd = rawEnd is null ? eventStart.AddMinutes(1) :
                        source.IsAllDay ? rawEnd.Value.Date.ToUniversalTime()
                        : rawEnd.IsFloating ? rawEnd.Value.ToUniversalTime() : rawEnd.AsUtc;
                    if (eventEnd <= startUtc || eventStart >= endUtc) continue;
                    // La vista del calendario posiciona cada item en un único día: proyectar una
                    // franja por día evita que un evento de varios días desaparezca tras el primero.
                    var firstDay = eventStart.ToLocalTime().Date;
                    var lastDay = eventEnd.AddTicks(-1).ToLocalTime().Date;
                    for (var day = firstDay; day <= lastDay && day < firstDay.AddDays(MaxEventSpanDays); day = day.AddDays(1))
                    {
                        var dayStart = day.ToUniversalTime();
                        var dayEnd = day.AddDays(1).ToUniversalTime();
                        var segmentStart = source.IsAllDay ? dayStart : eventStart > dayStart ? eventStart : dayStart;
                        var segmentEnd = eventEnd < dayEnd ? eventEnd : dayEnd;
                        if (segmentStart < startUtc || segmentStart >= endUtc || segmentEnd <= segmentStart) continue;
                        var minutes = source.IsAllDay ? null : (int?)Math.Clamp((segmentEnd - segmentStart).TotalMinutes, 1, 1440);
                        events.Add((feed, source.Uid ?? string.Empty, source.Summary ?? "Evento",
                            source.Description ?? string.Empty, segmentStart, minutes, source.IsAllDay));
                        if (events.Count >= 5000) return events;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogService.Warning($"No se pudo expandir el calendario externo '{feed.Name}' ({ex.GetType().Name})");
            }
        }
        return events;
    }

    private void Save()
    {
        JsonFileStore.Save(_path, _subscriptions);
        DataChanged?.Invoke();
    }

    public void Dispose()
    {
        _refreshLock.Dispose();
        if (_ownsClient) _client.Dispose();
    }
}
