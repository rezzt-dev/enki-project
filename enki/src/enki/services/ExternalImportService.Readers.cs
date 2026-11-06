using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>Lectores de Logseq, Evernote, Google Keep y TickTick (MED-03). Todos devuelven un
/// <see cref="Plan"/> —el mismo que ya consumen <see cref="Apply"/> y el informe— y no escriben nada: lo
/// que no se puede importar se anota como <see cref="ImportIssue"/> con su motivo en vez de perderse.</summary>
public sealed partial class ExternalImportService
{
    // ───────────────────────────── Logseq ─────────────────────────────

    private static readonly Regex LogseqBlockNoise = new(@"^[ \t]*(?:- )?(?:id|collapsed)::[ \t]*\S*[ \t]*\r?\n?", RegexOptions.Multiline | RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex LogseqProperty = new(@"^(?:- )?([A-Za-z][A-Za-z0-9_-]*)::[ \t]*(.*)$", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex LogseqTask = new(@"^([ \t]*)- (TODO|LATER|NOW|DOING|IN-PROGRESS|WAITING|DONE|CANCELED|CANCELLED)[ \t]+", RegexOptions.Multiline | RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex LogseqNamespaceLink = new(@"\[\[([^\]|#\n]+?/[^\]|#\n]+?)\]\]", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex LogseqJournalName = new(@"^(\d{4})_(\d{2})_(\d{2})$", RegexOptions.Compiled, RegexSafety.MatchTimeout);

    /// <summary>Lee una carpeta de grafo de Logseq: <c>pages/</c>, <c>journals/</c> y <c>assets/</c>. Convierte
    /// las propiedades de página (<c>clave:: valor</c>) en frontmatter, los marcadores <c>TODO</c>/<c>DONE</c>
    /// en casillas, y los nombres con espacios de nombres (<c>a___b.md</c>) en carpetas. La configuración
    /// (<c>logseq/</c>), las pizarras y los archivos <c>.org</c> se omiten y se anotan en el informe.</summary>
    public static Plan ReadLogseq(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var issues = new IssueLog();
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
                // La configuración y las copias de seguridad de Logseq no son contenido del usuario.
                if (folder == root && name.Equals("logseq", StringComparison.OrdinalIgnoreCase)) continue;
                pending.Push(child);
            }
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                var fileName = Path.GetFileName(path);
                if (fileName.StartsWith('.')) continue;
                var relative = Path.GetRelativePath(root, path);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { issues.Add(relative, ImportIssueKind.UnsafePath); continue; }
                var size = new FileInfo(path).Length;
                total += size;
                if (size > MaxInputBytes || total > MaxInputBytes) throw new InvalidDataException("El contenido supera 100 MB.");
                if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    var content = ReadLimited(path);
                    if (IsBlankLogseqPage(content)) { issues.Add(relative, ImportIssueKind.Empty); continue; }
                    AddNote(notes, LogseqRelativePath(relative), ConvertLogseq(content));
                }
                else if (IsAsset(path))
                {
                    if (assets.Count >= MaxEntries) throw new InvalidDataException("Hay demasiados adjuntos.");
                    assets.Add(new Asset(relative, path, null));
                }
                else issues.Add(relative, ImportIssueKind.UnsupportedItem);
            }
        }
        return new Plan("Logseq", notes, [], [], assets) { Issues = issues.Items, IssuesOmitted = issues.Overflow };
    }

    private static bool IsBlankLogseqPage(string content) =>
        content.Replace("-", string.Empty, StringComparison.Ordinal).Trim().Length == 0;

    /// <summary>Ruta de la nota importada: los diarios <c>2026_10_15.md</c> pasan a <c>2026-10-15.md</c> y
    /// los espacios de nombres <c>a___b.md</c> (o <c>a%2Fb.md</c>) se convierten en la carpeta <c>a/</c>.</summary>
    internal static string LogseqRelativePath(string relative)
    {
        var directory = Path.GetDirectoryName(relative) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(relative);
        if (LogseqJournalName.Match(name) is { Success: true } journal)
            name = $"{journal.Groups[1].Value}-{journal.Groups[2].Value}-{journal.Groups[3].Value}";
        else
        {
            if (name.Contains('%', StringComparison.Ordinal)) name = Uri.UnescapeDataString(name);
            name = name.Replace("___", "/", StringComparison.Ordinal);
        }
        var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0 && s != "." && s != "..").ToList();
        if (segments.Count == 0) segments.Add("Sin nombre");
        return Path.Combine(directory, string.Join(Path.DirectorySeparatorChar, segments) + ".md");
    }

    /// <summary>Markdown de Logseq a Markdown de Enki: quita los ids de bloque, convierte las propiedades de
    /// la primera página en frontmatter, los marcadores de tarea en casillas y los enlaces con espacio de
    /// nombres (<c>[[a/b]]</c>) en enlaces que Enki resuelve por nombre de archivo (<c>[[b|a/b]]</c>).</summary>
    internal static string ConvertLogseq(string content)
    {
        var text = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = LogseqBlockNoise.Replace(text, string.Empty);

        var lines = text.Split('\n').ToList();
        var properties = new List<(string Key, string Value)>();
        var consumed = 0;
        while (consumed < lines.Count && LogseqProperty.Match(lines[consumed]) is { Success: true } property)
        {
            properties.Add((property.Groups[1].Value, property.Groups[2].Value.Trim()));
            consumed++;
        }
        if (properties.Count > 0)
        {
            lines.RemoveRange(0, consumed);
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
        }

        var body = string.Join('\n', lines);
        body = LogseqTask.Replace(body, m =>
            m.Groups[1].Value + (m.Groups[2].Value is "DONE" or "CANCELED" or "CANCELLED" ? "- [x] " : "- [ ] "));
        body = LogseqNamespaceLink.Replace(body, m =>
        {
            var full = m.Groups[1].Value.Trim();
            var leaf = full[(full.LastIndexOf('/') + 1)..].Trim();
            return leaf.Length == 0 ? m.Value : $"[[{leaf}|{full}]]";
        });

        foreach (var (key, value) in properties)
        {
            if (value.Length == 0) continue;
            if (key.Equals("tags", StringComparison.OrdinalIgnoreCase))
            {
                var tags = value.Split(',').Select(t => t.Trim().Trim('#').Trim('[', ']').Trim())
                    .Where(t => t.Length > 0).ToList();
                if (tags.Count > 0) body = NoteTagHelper.SetTags(body, tags);
            }
            else body = TrySetProperty(body, key.ToLowerInvariant(), value.Replace("[[", string.Empty, StringComparison.Ordinal).Replace("]]", string.Empty, StringComparison.Ordinal));
        }
        return body;
    }

    /// <summary>Escribe una propiedad de frontmatter sin que una clave o un YAML ya roto detengan la
    /// importación entera: en ese caso la nota se importa sin esa propiedad.</summary>
    private static string TrySetProperty(string content, string key, string value)
    {
        try { return NotePropertyHelper.Set(content, key, value); }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { return content; }
    }

    // ───────────────────────────── Evernote ─────────────────────────────

    private static readonly Regex EnmlHeader = new(@"<\?xml[^>]*\?>|<!DOCTYPE[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex EnmlTodo = new(@"<en-todo\b([^>]*?)/?>(?:</en-todo>)?", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex EnmlMedia = new(@"<en-media\b([^>]*?)/?>(?:</en-media>)?", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex EnmlCrypt = new(@"<en-crypt\b[^>]*>.*?</en-crypt>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex MediaHash = new(@"hash\s*=\s*""([0-9a-fA-F]+)""", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex MediaToken = new(@"ENKIMEDIA(\d+)END", RegexOptions.Compiled, RegexSafety.MatchTimeout);
    private static readonly Regex TodoLine = new(@"^([ \t]*)(?:- )?ENKITODO(ON|OFF)[ \t]*", RegexOptions.Multiline | RegexOptions.Compiled, RegexSafety.MatchTimeout);

    /// <summary>Lee un archivo <c>.enex</c> de Evernote. Cada <c>&lt;note&gt;</c> pasa a una nota Markdown
    /// con sus etiquetas y su fecha de creación como propiedades; los adjuntos reconocidos se guardan en
    /// <c>attachments/</c> y se enlazan donde estaban. El texto cifrado (<c>en-crypt</c>) y los adjuntos de
    /// tipos que Enki no importa se omiten y se anotan.</summary>
    public static Plan ReadEvernote(string enexPath)
    {
        if (new FileInfo(enexPath).Length > MaxInputBytes) throw new InvalidDataException("El archivo supera 100 MB.");
        var issues = new IssueLog();
        var notes = new List<Note>();
        var assets = new List<Asset>();
        var usedAssetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Sin DTD ni resolutor externo: un .enex descargado no puede pedir recursos ni expandir entidades.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, CloseInput = true };
        try
        {
            using var reader = XmlReader.Create(File.OpenRead(enexPath), settings);
            reader.MoveToContent();
            if (reader.LocalName != "en-export") throw new InvalidDataException("El archivo no es una exportación de Evernote.");
            var index = 0;
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "note")
                {
                    index++;
                    if (notes.Count >= MaxEntries) throw new InvalidDataException("Hay demasiadas notas.");
                    // ReadFrom consume el elemento entero y deja el lector en el nodo siguiente.
                    var element = (XElement)XNode.ReadFrom(reader);
                    AddEvernoteNote(element, index, notes, assets, usedAssetNames, issues);
                }
                else reader.Read();
            }
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("El archivo de Evernote no es un XML válido.", ex);
        }
        return new Plan("Evernote", notes, [], [], assets) { Issues = issues.Items, IssuesOmitted = issues.Overflow };
    }

    private static void AddEvernoteNote(XElement element, int index, List<Note> notes, List<Asset> assets,
        HashSet<string> usedAssetNames, IssueLog issues)
    {
        var title = (element.Element("title")?.Value ?? string.Empty).Trim();
        var label = title.Length > 0 ? title : "#" + index.ToString(CultureInfo.InvariantCulture);
        var enml = element.Element("content")?.Value ?? string.Empty;

        // Recursos por hash MD5 del contenido: es como <en-media> señala a cada adjunto.
        var resources = new Dictionary<string, (string FileName, string Mime, byte[] Bytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in element.Elements("resource"))
        {
            var data = resource.Element("data")?.Value;
            if (string.IsNullOrWhiteSpace(data)) continue;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(data); }
            catch (FormatException) { issues.Add(label, ImportIssueKind.InvalidFormat); continue; }
            if (bytes.LongLength > MaxInputBytes) { issues.Add(label, ImportIssueKind.TooLarge); continue; }
            var mime = resource.Element("mime")?.Value ?? string.Empty;
            var fileName = resource.Element("resource-attributes")?.Element("file-name")?.Value;
            resources[Md5Hex(bytes)] = (fileName ?? string.Empty, mime, bytes);
        }

        var attachments = new List<(string Markdown, bool IsImage)>();
        var html = EnmlHeader.Replace(enml, string.Empty);
        var encrypted = false;
        html = EnmlCrypt.Replace(html, _ => { encrypted = true; return string.Empty; });
        if (encrypted) issues.Add(label, ImportIssueKind.EncryptedContent);
        // Marcas de texto plano: los corchetes de «[ ]» los escaparía el render de Markdown.
        html = EnmlTodo.Replace(html, m => m.Groups[1].Value.Contains("true", StringComparison.OrdinalIgnoreCase) ? " ENKITODOON " : " ENKITODOOFF ");
        html = EnmlMedia.Replace(html, m =>
        {
            var hash = MediaHash.Match(m.Groups[1].Value);
            if (!hash.Success || !resources.TryGetValue(hash.Groups[1].Value, out var resource))
            {
                issues.Add(label, ImportIssueKind.MissingAttachment);
                return string.Empty;
            }
            var safeName = AttachmentName(resource.FileName, resource.Mime, usedAssetNames);
            if (!IsAsset(safeName))
            {
                issues.Add(label + " / " + safeName, ImportIssueKind.UnsupportedAttachment);
                return string.Empty;
            }
            if (assets.Count >= MaxEntries) throw new InvalidDataException("Hay demasiados adjuntos.");
            var relative = "attachments/" + safeName;
            assets.Add(new Asset(relative.Replace('/', Path.DirectorySeparatorChar), null, resource.Bytes));
            var isImage = resource.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            attachments.Add(((isImage ? "![" : "[") + safeName + "](" + relative + ")", isImage));
            return " ENKIMEDIA" + (attachments.Count - 1).ToString(CultureInfo.InvariantCulture) + "END ";
        });

        var markdown = HtmlReadability.ToMarkdown(html);
        markdown = MediaToken.Replace(markdown, m =>
            int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is var i && i < attachments.Count ? attachments[i].Markdown : string.Empty);
        markdown = TodoLine.Replace(markdown, m => m.Groups[1].Value + (m.Groups[2].Value == "ON" ? "- [x] " : "- [ ] "));
        markdown = markdown.Replace("ENKITODOON", "[x]", StringComparison.Ordinal).Replace("ENKITODOOFF", "[ ]", StringComparison.Ordinal);

        if (title.Length == 0) title = FirstLine(markdown) ?? LocalizationService.Instance["Vault_DefaultNoteTitle"] + " " + index.ToString(CultureInfo.InvariantCulture);
        if (markdown.Trim().Length == 0 && attachments.Count == 0) { issues.Add(label, ImportIssueKind.Empty); return; }

        var tags = element.Elements("tag").Select(t => t.Value.Trim()).Where(t => t.Length > 0).ToList();
        if (tags.Count > 0) markdown = NoteTagHelper.SetTags(markdown, tags);
        if (EnexDate(element.Element("created")?.Value) is { } created)
            markdown = TrySetProperty(markdown, "created", created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        AddNote(notes, SafeFileName(title) + ".md", markdown);
    }

    private static string Md5Hex(byte[] bytes)
    {
        // MD5 solo identifica el adjunto: es el hash que Evernote escribe en <en-media>, no protege nada.
#pragma warning disable CA5351
        return Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
#pragma warning restore CA5351
    }

    private static DateTime? EnexDate(string? text) =>
        DateTime.TryParseExact(text?.Trim(), "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;

    /// <summary>Nombre de archivo del adjunto sin separadores ni caracteres problemáticos, único dentro de
    /// la importación, con la extensión que corresponda al tipo MIME si el original no la traía.</summary>
    private static string AttachmentName(string fileName, string mime, HashSet<string> used)
    {
        var name = SafeFileName(Path.GetFileName(fileName ?? string.Empty));
        if (Path.GetExtension(name).Length == 0)
        {
            var extension = mime.ToLowerInvariant() switch
            {
                "image/png" => ".png", "image/jpeg" or "image/jpg" => ".jpg", "image/gif" => ".gif",
                "image/svg+xml" => ".svg", "image/webp" => ".webp", "application/pdf" => ".pdf",
                "audio/mpeg" => ".mp3", "audio/wav" or "audio/x-wav" => ".wav", "audio/ogg" => ".ogg",
                "video/mp4" => ".mp4", _ => string.Empty
            };
            name = (name.Length == 0 || name == "Sin nombre" ? "adjunto" : name) + extension;
        }
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++)
            candidate = Path.GetFileNameWithoutExtension(name) + "-" + n.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(name);
        return candidate;
    }

    /// <summary>Quita lo que no puede ir en un nombre de archivo ni en la ruta relativa de un enlace
    /// Markdown (separadores, caracteres reservados, espacios repetidos), conservando letras con tilde y
    /// la eñe.</summary>
    internal static string SafeFileName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
            builder.Append(char.IsControl(ch) || "<>:\"/\\|?*#%[]()".Contains(ch, StringComparison.Ordinal) ? ' ' : ch);
        var cleaned = Regex.Replace(builder.ToString(), @"\s+", " ", RegexOptions.None, RegexSafety.MatchTimeout).Trim().TrimEnd('.');
        if (cleaned.Length > 120) cleaned = cleaned[..120].TrimEnd();
        return cleaned.Length == 0 ? "Sin nombre" : cleaned;
    }

    private static string? FirstLine(string markdown)
    {
        var line = markdown.Split('\n').Select(l => l.Trim().TrimStart('#', '-', '*', '>', ' ')).FirstOrDefault(l => l.Length > 0);
        return line is null ? null : (line.Length > 60 ? line[..60] : line);
    }

    // ───────────────────────────── Google Keep ─────────────────────────────

    private sealed record SourceEntry(string Path, long Length, Func<Stream> Open, string? DiskPath);

    /// <summary>Lee una exportación de Google Keep de Takeout: el ZIP descargado, o la carpeta ya extraída
    /// (<c>Takeout/Keep</c>). Cada <c>.json</c> es una nota o una lista; las etiquetas pasan a etiquetas de
    /// Enki, las listas a casillas y las imágenes adjuntas se importan junto a la nota. Las notas de la
    /// papelera se omiten y se anotan.</summary>
    public static Plan ReadGoogleKeep(string path)
    {
        var entries = Directory.Exists(path) ? KeepEntriesFromDirectory(path) : KeepEntriesFromZip(path);
        var issues = new IssueLog();
        var notes = new List<Note>();
        var assets = new List<Asset>();
        var usedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byLeaf = new Dictionary<string, SourceEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) byLeaf.TryAdd(System.IO.Path.GetFileName(entry.Path), entry);

        var index = 0;
        foreach (var entry in entries.Where(e => e.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            index++;
            JsonDocument document;
            try
            {
                using var stream = entry.Open();
                document = JsonDocument.Parse(stream);
            }
            catch (JsonException) { issues.Add(entry.Path, ImportIssueKind.InvalidFormat); continue; }

            using (document)
            {
                var note = document.RootElement;
                if (note.ValueKind != JsonValueKind.Object
                    || !(note.TryGetProperty("textContent", out _) || note.TryGetProperty("listContent", out _) || note.TryGetProperty("title", out _)))
                {
                    issues.Add(entry.Path, ImportIssueKind.UnsupportedItem);
                    continue;
                }
                AddKeepNote(note, entry, index, notes, assets, usedAssets, byLeaf, issues);
            }
        }
        return new Plan("Google Keep", notes, [], [], assets) { Issues = issues.Items, IssuesOmitted = issues.Overflow };
    }

    private static void AddKeepNote(JsonElement note, SourceEntry entry, int index, List<Note> notes, List<Asset> assets,
        HashSet<string> usedAssets, Dictionary<string, SourceEntry> byLeaf, IssueLog issues)
    {
        string Text(string name) => note.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        bool Flag(string name) => note.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        var title = Text("title").Trim();
        var label = title.Length > 0 ? title : entry.Path;
        if (Flag("isTrashed")) { issues.Add(label, ImportIssueKind.Trashed); return; }

        var body = new StringBuilder();
        if (note.TryGetProperty("listContent", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var text = item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(text)) continue;
                var done = item.TryGetProperty("isChecked", out var c) && c.ValueKind == JsonValueKind.True;
                body.Append(done ? "- [x] " : "- [ ] ").AppendLine(text.Replace("\n", " ", StringComparison.Ordinal));
            }
        }
        else body.Append(Text("textContent").Trim());

        if (note.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
        {
            foreach (var attachment in attachments.EnumerateArray())
            {
                var filePath = attachment.TryGetProperty("filePath", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? string.Empty : string.Empty;
                var leaf = System.IO.Path.GetFileName(filePath);
                if (leaf.Length == 0 || !byLeaf.TryGetValue(leaf, out var source)) { issues.Add(label, ImportIssueKind.MissingAttachment); continue; }
                if (!IsAsset(leaf)) { issues.Add(label + " / " + leaf, ImportIssueKind.UnsupportedAttachment); continue; }
                var name = AttachmentName(leaf, string.Empty, usedAssets);
                if (assets.Count >= MaxEntries) throw new InvalidDataException("Hay demasiados adjuntos.");
                if (source.DiskPath is { } disk) assets.Add(new Asset(name, disk, null));
                else
                {
                    using var stream = source.Open();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    assets.Add(new Asset(name, null, buffer.ToArray()));
                }
                if (body.Length > 0) body.AppendLine().AppendLine();
                body.Append("![").Append(name).Append("](").Append(name).Append(')');
            }
        }

        var markdown = body.ToString().TrimEnd();
        if (markdown.Length == 0 && title.Length == 0) { issues.Add(label, ImportIssueKind.Empty); return; }

        if (title.Length == 0)
            title = FirstLine(markdown) ?? LocalizationService.Instance["Vault_DefaultNoteTitle"] + " " + index.ToString(CultureInfo.InvariantCulture);
        var labels = new List<string>();
        if (note.TryGetProperty("labels", out var labelList) && labelList.ValueKind == JsonValueKind.Array)
            labels.AddRange(labelList.EnumerateArray()
                .Select(l => l.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()?.Trim() : null)
                .Where(n => !string.IsNullOrEmpty(n))!);
        if (labels.Count > 0) markdown = NoteTagHelper.SetTags(markdown, labels);
        if (note.TryGetProperty("createdTimestampUsec", out var created) && created.TryGetInt64(out var micros) && micros > 0)
        {
            var date = DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000).UtcDateTime;
            markdown = TrySetProperty(markdown, "created", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        AddNote(notes, SafeFileName(title) + ".md", markdown);
    }

    private static List<SourceEntry> KeepEntriesFromDirectory(string directory)
    {
        var root = System.IO.Path.GetFullPath(directory);
        var entries = new List<SourceEntry>();
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(root, file);
            if (relative.Split(System.IO.Path.DirectorySeparatorChar).Any(p => p.StartsWith('.'))) continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            var length = new FileInfo(file).Length;
            total += length;
            if (length > MaxInputBytes || total > MaxInputBytes) throw new InvalidDataException("El contenido supera 100 MB.");
            if (entries.Count >= MaxEntries) throw new InvalidDataException("El archivo contiene demasiadas entradas.");
            var captured = file;
            entries.Add(new SourceEntry(relative, length, () => File.OpenRead(captured), file));
        }
        return entries;
    }

    private static List<SourceEntry> KeepEntriesFromZip(string zipPath)
    {
        if (new FileInfo(zipPath).Length > MaxInputBytes) throw new InvalidDataException("El archivo supera 100 MB.");
        // El ZIP se lee entero a memoria y se cierra aquí: las entradas conservan sus bytes, no el archivo.
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("El archivo contiene demasiadas entradas.");
        var entries = new List<SourceEntry>();
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Name.Length == 0 || !SafeRelativePath(entry.FullName)) continue;
            total += entry.Length;
            if (entry.Length > MaxInputBytes || total > MaxInputBytes) throw new InvalidDataException("El contenido supera 100 MB.");
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            entries.Add(new SourceEntry(entry.FullName, bytes.LongLength, () => new MemoryStream(bytes, writable: false), null));
        }
        return entries;
    }

    // ───────────────────────────── TickTick ─────────────────────────────

    /// <summary>Lee la copia de seguridad CSV de TickTick: las tareas pasan al Kanban con su fecha límite,
    /// prioridad y estado; los elementos de tipo nota pasan a notas del vault. La repetición no tiene
    /// equivalente en las tarjetas y se anota en el informe; las etiquetas y la lista de origen no se
    /// conservan.</summary>
    public static Plan ReadTickTick(string csvPath)
    {
        var rows = ParseCsv(ReadLimited(csvPath));
        // La copia de seguridad antepone unas líneas de metadatos («Date:», «Version:», «Status:»).
        var headerIndex = rows.FindIndex(r => r.Any(c => c.Trim().Equals("Title", StringComparison.OrdinalIgnoreCase)) && r.Count > 3);
        if (headerIndex < 0) throw new InvalidDataException("Falta la columna Title de TickTick.");
        var headers = rows[headerIndex].Select(h => h.Trim().TrimStart('﻿').ToUpperInvariant()).ToList();
        var title = headers.IndexOf("TITLE");
        var content = headers.FindIndex(h => h is "CONTENT" or "DESC");
        var kind = headers.IndexOf("KIND");
        var due = headers.FindIndex(h => h is "DUE DATE" or "DUE_DATE");
        var priority = headers.IndexOf("PRIORITY");
        var status = headers.IndexOf("STATUS");
        var repeat = headers.IndexOf("REPEAT");

        var issues = new IssueLog();
        var tasks = new List<Task>();
        var notes = new List<Note>();
        foreach (var row in rows.Skip(headerIndex + 1))
        {
            var name = Get(row, title).Trim();
            var details = Get(row, content).Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
            if (name.Length == 0) { issues.Add("(" + (tasks.Count + notes.Count + 1).ToString(CultureInfo.InvariantCulture) + ")", ImportIssueKind.Empty); continue; }

            if (Get(row, kind).Trim().Equals("NOTE", StringComparison.OrdinalIgnoreCase))
            {
                AddNote(notes, SafeFileName(name) + ".md", details.Length == 0 ? "# " + name : details);
                continue;
            }

            DateTime? dueUtc = null;
            var dueText = Regex.Replace(Get(row, due).Trim(), @"([+-]\d{2})(\d{2})$", "$1:$2", RegexOptions.None, RegexSafety.MatchTimeout);
            if (dueText.Length > 0 && DateTimeOffset.TryParse(dueText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
                dueUtc = parsed.UtcDateTime;
            var level = Get(row, priority).Trim() switch { "5" => KanbanPriority.High, "1" => KanbanPriority.Low, _ => KanbanPriority.Medium };
            var done = Get(row, status).Trim() is "1" or "2";
            if (Get(row, repeat).Trim().Length > 0) issues.Add(name, ImportIssueKind.RecurrenceDropped);
            if (tasks.Count >= MaxEntries) throw new InvalidDataException("Hay demasiadas tareas.");
            tasks.Add(new Task(name, TickTickChecklist(details), dueUtc, done, level));
        }
        return new Plan("TickTick", notes, tasks, [], []) { Issues = issues.Items, IssuesOmitted = issues.Overflow };
    }

    /// <summary>Las listas de comprobación de TickTick marcan cada línea con un cuadrado vacío o lleno.</summary>
    private static string TickTickChecklist(string details)
    {
        if (details.Length == 0) return details;
        var lines = details.Split('\n').Select(l =>
        {
            var line = l.TrimStart();
            if (line.StartsWith('▫') || line.StartsWith('□') || line.StartsWith('☐')) return "- [ ] " + line[1..].TrimStart();
            if (line.StartsWith('▪') || line.StartsWith('■') || line.StartsWith('☑') || line.StartsWith('✓')) return "- [x] " + line[1..].TrimStart();
            return l;
        });
        return string.Join('\n', lines);
    }
}
