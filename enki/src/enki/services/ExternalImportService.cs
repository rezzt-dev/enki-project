using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using enki.Models;

namespace enki.Services;

/// <summary>Lee exportaciones locales antes de escribir en las entidades reales de Enki. Los límites
/// evitan que un ZIP o un archivo accidentalmente enorme bloquee la interfaz o agote el disco.</summary>
public sealed class ExternalImportService(VaultService vault, KanbanService kanban, ReminderService reminders)
{
    private const long MaxInputBytes = 100L * 1024 * 1024;
    private const int MaxEntries = 10000;
    private readonly VaultService _vault = vault;
    private readonly KanbanService _kanban = kanban;
    private readonly ReminderService _reminders = reminders;

    public sealed record Note(string RelativePath, string Content);
    public sealed record Asset(string RelativePath, string? SourcePath, byte[]? Bytes);
    public sealed record Task(string Title, string Description, DateTime? DueUtc, bool Completed, KanbanPriority Priority);
    public sealed record CalendarEvent(string Title, string Description, DateTime StartUtc, int? DurationMinutes);
    public sealed record Plan(string Source, IReadOnlyList<Note> Notes, IReadOnlyList<Task> Tasks, IReadOnlyList<CalendarEvent> Events, IReadOnlyList<Asset> Assets)
    {
        public int RecurringSeries { get; init; }
    }
    public sealed record Result(int Notes, int Tasks, int Events, int Assets);

    /// <summary>Conserva el progreso cuando falla una escritura: el usuario puede saber qué quedó creado.</summary>
    public sealed class ImportInterruptedException(Result completed, Exception inner)
        : IOException("La importación se interrumpió.", inner)
    {
        public Result Completed { get; } = completed;
    }

    public static Plan ReadObsidian(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var notes = new List<Note>();
        var assets = new List<Asset>();
        var root = Path.GetFullPath(directory);
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                pending.Push(child);
            }
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                var size = new FileInfo(path).Length;
                total += size;
                if (size > MaxInputBytes || total > MaxInputBytes) throw new InvalidDataException("El contenido supera 100 MB.");
                var relative = Path.GetRelativePath(root, path);
                if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) AddNote(notes, relative, ReadLimited(path));
                else if (IsAsset(path))
                {
                    if (assets.Count >= MaxEntries) throw new InvalidDataException("Hay demasiados adjuntos.");
                    assets.Add(new Asset(relative, path, null));
                }
            }
        }
        return new Plan("Obsidian", notes, [], [], assets);
    }

    public static Plan ReadNotion(string zipPath)
    {
        var notes = new List<Note>();
        var assets = new List<Asset>();
        if (new FileInfo(zipPath).Length > MaxInputBytes) throw new InvalidDataException("El archivo supera 100 MB.");
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("El archivo contiene demasiadas entradas.");
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (!SafeRelativePath(entry.FullName)) continue;
            var markdown = entry.FullName.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
            var csv = entry.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            if (!markdown && !csv && !IsAsset(entry.FullName)) continue;
            total += entry.Length;
            if (entry.Length > MaxInputBytes || total > MaxInputBytes) throw new InvalidDataException("El contenido supera 100 MB.");
            using var stream = entry.Open();
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (markdown || csv)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var content = reader.ReadToEnd();
                if (csv) content = CsvToMarkdown(content);
                AddNote(notes, csv ? Path.ChangeExtension(relative, ".md") : relative, content);
            }
            else
            {
                if (assets.Count >= MaxEntries) throw new InvalidDataException("Hay demasiados adjuntos.");
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                if (buffer.Length > MaxInputBytes) throw new InvalidDataException("El adjunto supera 100 MB.");
                assets.Add(new Asset(relative, null, buffer.ToArray()));
            }
        }
        return new Plan("Notion", notes, [], [], assets);
    }

    public static Plan ReadTodoist(string csvPath)
    {
        var rows = ParseCsv(ReadLimited(csvPath));
        if (rows.Count < 2) throw new InvalidDataException("El CSV no contiene tareas.");
        var headers = rows[0].Select(h => h.Trim().TrimStart('\uFEFF').ToUpperInvariant()).ToList();
        int titleIndex = headers.FindIndex(h => h is "CONTENT" or "TASK" or "TITLE");
        if (titleIndex < 0) throw new InvalidDataException("Falta la columna CONTENT de Todoist.");
        int descriptionIndex = headers.FindIndex(h => h is "DESCRIPTION" or "NOTE");
        int dueIndex = headers.FindIndex(h => h is "DATE" or "DUE DATE" or "DUE_DATE");
        int priorityIndex = headers.FindIndex(h => h == "PRIORITY");
        int checkedIndex = headers.FindIndex(h => h is "CHECKED" or "COMPLETED");
        int typeIndex = headers.FindIndex(h => h == "TYPE");
        var tasks = new List<Task>();
        foreach (var row in rows.Skip(1))
        {
            if (typeIndex >= 0 && Get(row, typeIndex) is { Length: > 0 } type && !type.Equals("task", StringComparison.OrdinalIgnoreCase)) continue;
            var title = Get(row, titleIndex).Trim();
            if (title.Length == 0) continue;
            DateTime? due = null;
            var dueText = Get(row, dueIndex);
            if (DateTimeOffset.TryParse(dueText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)) due = parsed.UtcDateTime;
            var priority = Get(row, priorityIndex) switch { "4" => KanbanPriority.High, "3" => KanbanPriority.High, "1" => KanbanPriority.Low, _ => KanbanPriority.Medium };
            if (tasks.Count >= MaxEntries) throw new InvalidDataException("Hay demasiadas tareas.");
            tasks.Add(new Task(title, Get(row, descriptionIndex), due, Get(row, checkedIndex) is "1" or "true" or "TRUE", priority));
        }
        return new Plan("Todoist", [], tasks, [], []);
    }

    public static Plan ReadGoogleCalendar(string icsPath)
    {
        var lines = ReadLimited(icsPath).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var unfolded = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                if (unfolded.Count > 0) unfolded[^1] += line[1..];
            }
            else unfolded.Add(line.TrimEnd('\r'));
        }
        var events = new List<CalendarEvent>();
        var recurringSeries = 0;
        Dictionary<string, string>? fields = null;
        foreach (var line in unfolded)
        {
            if (line == "BEGIN:VEVENT") { fields = new(StringComparer.OrdinalIgnoreCase); continue; }
            if (line == "END:VEVENT")
            {
                if (fields is not null && !string.Equals(fields.GetValueOrDefault("STATUS"), "CANCELLED", StringComparison.OrdinalIgnoreCase) && fields.TryGetValue("DTSTART", out var startText) && TryDate(startText, fields.GetValueOrDefault("DTSTART_TZID"), out var start))
                {
                    var title = fields.GetValueOrDefault("SUMMARY", "Evento");
                    var end = fields.TryGetValue("DTEND", out var endText) && TryDate(endText, fields.GetValueOrDefault("DTEND_TZID"), out var parsedEnd) ? parsedEnd : start;
                    var minutes = end > start ? (int)Math.Min((end - start).TotalMinutes, 1440) : (int?)null;
                    if (fields.ContainsKey("RRULE")) recurringSeries++;
                    if (events.Count >= MaxEntries) throw new InvalidDataException("Hay demasiados eventos.");
                    events.Add(new CalendarEvent(UnescapeIcs(title), UnescapeIcs(fields.GetValueOrDefault("DESCRIPTION", "")), start, minutes));
                }
                fields = null;
                continue;
            }
            if (fields is null) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var property = line[..colon];
            var key = property.Split(';')[0];
            fields[key] = line[(colon + 1)..];
            foreach (var parameter in property.Split(';').Skip(1))
                if (parameter.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))
                    fields[key + "_TZID"] = parameter[5..].Trim('"');
        }
        return new Plan("Google Calendar", [], [], events, []) { RecurringSeries = recurringSeries };
    }

    /// <summary>Crea elementos en los servicios existentes; cada servicio persiste y anuncia su cambio.
    /// Se conserva lo ya importado si una operación posterior falla y se comunica el recuento.</summary>
    public Result Apply(Plan plan)
    {
        var importedNotes = 0;
        var importedTasks = 0;
        var importedEvents = 0;
        var importedAssets = 0;
        try
        {
            if (plan.Notes.Count + plan.Assets.Count > 0)
            {
                if (!_vault.IsVaultConfigured) throw new InvalidOperationException("No hay un vault abierto.");
                var root = VaultService.CreateFolder(_vault.VaultPath, "Importado de " + plan.Source).FullPath;
                var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [""] = root };
                string ParentFor(string relative)
                {
                    var parts = relative.Replace('\\', '/').Split('/');
                    var parent = root;
                    var key = "";
                    foreach (var part in parts.SkipLast(1))
                    {
                        key += "/" + part;
                        if (!folders.TryGetValue(key, out var next)) folders[key] = next = VaultService.CreateFolder(parent, part).FullPath;
                        parent = next;
                    }
                    return parent;
                }
                foreach (var note in plan.Notes)
                {
                    _vault.CreateNote(Path.GetFileNameWithoutExtension(note.RelativePath), ParentFor(note.RelativePath), note.Content);
                    importedNotes++;
                }
                foreach (var asset in plan.Assets)
                {
                    var parent = ParentFor(asset.RelativePath);
                    if (asset.SourcePath is { } source) _vault.ImportFile(source, parent);
                    else if (asset.Bytes is { } bytes)
                    {
                        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "enki-import-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(temporaryDirectory);
                        try
                        {
                            var temporaryFile = Path.Combine(temporaryDirectory, Path.GetFileName(asset.RelativePath));
                            File.WriteAllBytes(temporaryFile, bytes);
                            _vault.ImportFile(temporaryFile, parent);
                        }
                        finally { Directory.Delete(temporaryDirectory, recursive: true); }
                    }
                    importedAssets++;
                }
            }
            if (plan.Tasks.Count > 0)
            {
                var column = _kanban.DefaultBoard.Columns.First(c => c.Role == CoreColumnRole.Backlog);
                var done = _kanban.DefaultBoard.Columns.First(c => c.Role == CoreColumnRole.Done);
                foreach (var task in plan.Tasks)
                {
                    _kanban.CreateCard(_kanban.DefaultBoard.Id, (task.Completed ? done : column).Id, new KanbanCard
                    {
                        Id = Guid.NewGuid().ToString("N"), Title = task.Title, Description = task.Description,
                        DueDateUtc = task.DueUtc, Priority = task.Priority,
                        CompletedAtUtc = task.Completed ? DateTime.UtcNow : null
                    });
                    importedTasks++;
                }
            }
            foreach (var item in plan.Events)
            {
                _reminders.CreateImportedEvent(item.Title, item.Description, item.StartUtc, item.DurationMinutes);
                importedEvents++;
            }
            return new Result(importedNotes, importedTasks, importedEvents, importedAssets);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            throw new ImportInterruptedException(new Result(importedNotes, importedTasks, importedEvents, importedAssets), ex);
        }
    }

    private static bool IsAsset(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".pdf" or ".mp3" or ".mp4" or ".wav" or ".ogg";

    private static string CsvToMarkdown(string csv)
    {
        var rows = ParseCsv(csv);
        if (rows.Count == 0) return string.Empty;
        static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);
        var output = new StringBuilder();
        output.Append("| ").Append(string.Join(" | ", rows[0].Select(Cell))).AppendLine(" |");
        output.Append("| ").Append(string.Join(" | ", rows[0].Select(_ => "---"))).AppendLine(" |");
        foreach (var row in rows.Skip(1)) output.Append("| ").Append(string.Join(" | ", row.Select(Cell))).AppendLine(" |");
        return output.ToString();
    }

    private static void AddNote(List<Note> notes, string relative, string content)
    {
        if (notes.Count >= MaxEntries) throw new InvalidDataException("Hay demasiadas notas.");
        notes.Add(new Note(relative, content));
    }
    private static string ReadLimited(string path)
    {
        if (new FileInfo(path).Length > MaxInputBytes) throw new InvalidDataException("El archivo supera 100 MB.");
        return File.ReadAllText(path);
    }
    private static bool SafeRelativePath(string path) =>
        !Path.IsPathRooted(path) && path.Replace('\\', '/').Split('/').All(p => p.Length > 0 && p != "." && p != ".." && !p.StartsWith('.'));
    private static string Get(List<string> row, int index) => index >= 0 && index < row.Count ? row[index] : "";
    private static string UnescapeIcs(string text) => text.Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase).Replace("\\,", ",", StringComparison.Ordinal).Replace("\\;", ";", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
    private static bool TryDate(string text, string? timeZoneId, out DateTime utc)
    {
        if (DateTime.TryParseExact(text, ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmm'Z'"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc)) return true;
        if (DateTime.TryParseExact(text, ["yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            if (!string.IsNullOrWhiteSpace(timeZoneId))
            {
                try { utc = TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)); return true; }
                catch (TimeZoneNotFoundException ex) { throw new InvalidDataException("TZID desconocida: " + timeZoneId, ex); }
                catch (InvalidTimeZoneException ex) { throw new InvalidDataException("TZID no válida: " + timeZoneId, ex); }
                catch (ArgumentException ex) { throw new InvalidDataException("TZID no válida: " + timeZoneId, ex); }
            }
            utc = DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime(); return true;
        }
        utc = default; return false;
    }
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var field = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else quoted = !quoted; }
            else if (!quoted && ch == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (!quoted && (ch == '\n' || ch == '\r'))
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear(); if (row.Any(x => x.Length > 0)) rows.Add(row); row = [];
            }
            else field.Append(ch);
        }
        if (quoted) throw new InvalidDataException("El CSV contiene comillas sin cerrar.");
        row.Add(field.ToString()); if (row.Any(x => x.Length > 0)) rows.Add(row);
        return rows;
    }
}
