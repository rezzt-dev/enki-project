using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Lectores de Logseq, Evernote, Google Keep y TickTick y el informe de importación (MED-03).
/// Todo ocurre en carpetas temporales propias: ninguna prueba toca un vault ni unos datos reales.</summary>
public sealed class ExternalImportMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enki-migration-" + Guid.NewGuid().ToString("N"));

    public ExternalImportMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ── Logseq ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConvertLogseq_PropertiesTasksAndNamespaces_BecomeEnkiMarkdown()
    {
        var converted = ExternalImportService.ConvertLogseq(
            "tags:: [[estudio]], mates\nalias:: Cálculo\n\n- TODO Repasar límites\n- DONE Entregar práctica\n  id:: 6512a3b4-aaaa-bbbb-cccc-1234567890ab\n- Ver [[Mates/Cálculo]] y [[Otra]]\n");

        Assert.StartsWith("---\n", converted);
        Assert.Contains("tags:", converted);
        Assert.Contains("estudio", converted);
        Assert.Equal("Cálculo", NotePropertyHelper.Extract(converted)["alias"]);
        Assert.Contains("- [ ] Repasar límites", converted);
        Assert.Contains("- [x] Entregar práctica", converted);
        Assert.DoesNotContain("id::", converted);
        Assert.Contains("[[Cálculo|Mates/Cálculo]]", converted);
        Assert.Contains("[[Otra]]", converted);
        Assert.DoesNotContain("tags::", converted);
    }

    [Theory]
    [InlineData("pages/Proyecto.md", "pages/Proyecto.md")]
    [InlineData("journals/2026_10_15.md", "journals/2026-10-15.md")]
    [InlineData("pages/Mates___Cálculo.md", "pages/Mates/Cálculo.md")]
    [InlineData("pages/Mates%2FÁlgebra.md", "pages/Mates/Álgebra.md")]
    public void LogseqRelativePath_JournalsAndNamespaces(string input, string expected) =>
        Assert.Equal(expected.Replace('/', Path.DirectorySeparatorChar),
            ExternalImportService.LogseqRelativePath(input.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void ReadLogseq_ReadsPagesJournalsAssets_AndReportsWhatItSkips()
    {
        Write("graph/pages/Proyecto.md", "- TODO Primera tarea\n");
        Write("graph/pages/Mates___Cálculo.md", "- Apuntes\n");
        Write("graph/pages/Vacia.md", "-\n");
        Write("graph/journals/2026_10_15.md", "- Hoy\n");
        Write("graph/logseq/config.edn", "{}");
        Write("graph/whiteboards/idea.edn", "{}");
        Write("graph/pages/legacy.org", "* org");
        File.WriteAllBytes(Path.Combine(_root, "graph", "assets", "foto.png"), CreateParent("graph/assets/foto.png"));

        var plan = ExternalImportService.ReadLogseq(Path.Combine(_root, "graph"));

        Assert.Equal("Logseq", plan.Source);
        Assert.Equal(3, plan.Notes.Count);
        Assert.Contains(plan.Notes, n => n.RelativePath == Path.Combine("journals", "2026-10-15.md"));
        Assert.Contains(plan.Notes, n => n.RelativePath == Path.Combine("pages", "Mates", "Cálculo.md"));
        Assert.Single(plan.Assets);
        Assert.Contains(plan.Issues, i => i.Kind == ExternalImportService.ImportIssueKind.Empty && i.Name.EndsWith("Vacia.md", StringComparison.Ordinal));
        Assert.Contains(plan.Issues, i => i.Kind == ExternalImportService.ImportIssueKind.UnsupportedItem && i.Name.EndsWith("idea.edn", StringComparison.Ordinal));
        Assert.Contains(plan.Issues, i => i.Kind == ExternalImportService.ImportIssueKind.UnsupportedItem && i.Name.EndsWith("legacy.org", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Issues, i => i.Name.Contains("config.edn", StringComparison.Ordinal));
    }

    private byte[] CreateParent(string relative)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, relative))!);
        return [1, 2, 3];
    }

    // ── Evernote ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadEvernote_ConvertsTodosMediaTagsAndDate_AndReportsUnsupportedParts()
    {
        var png = new byte[] { 137, 80, 78, 71, 1, 2, 3 };
        var doc = new byte[] { 9, 9, 9 };
        var enex = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE en-export SYSTEM "http://xml.evernote.com/pub/evernote-export4.dtd">
            <en-export export-date="20260101T000000Z" application="Evernote" version="10">
            <note>
              <title>Compras</title>
              <created>20260105T101500Z</created>
              <tag>casa</tag>
              <content><![CDATA[<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE en-note SYSTEM "http://xml.evernote.com/pub/enml2.dtd"><en-note><div>Hola <b>mundo</b></div><div><en-todo checked="false"/>Pan</div><div><en-todo checked="true"/>Leche</div><div><en-media hash="{Md5(png)}" type="image/png"/></div><div><en-media hash="{Md5(doc)}" type="application/msword"/></div><div><en-crypt hint="x">AAAA</en-crypt></div></en-note>]]></content>
              <resource><data encoding="base64">{Convert.ToBase64String(png)}</data><mime>image/png</mime><resource-attributes><file-name>foto.png</file-name></resource-attributes></resource>
              <resource><data encoding="base64">{Convert.ToBase64String(doc)}</data><mime>application/msword</mime><resource-attributes><file-name>contrato.doc</file-name></resource-attributes></resource>
            </note>
            <note><title>Vacía</title><content><![CDATA[<en-note><div></div></en-note>]]></content></note>
            </en-export>
            """;
        var file = Write("mis-notas.enex", enex);

        var plan = ExternalImportService.ReadEvernote(file);

        var note = Assert.Single(plan.Notes);
        Assert.Equal("Compras.md", note.RelativePath);
        Assert.Contains("Hola **mundo**", note.Content);
        Assert.Contains("- [ ] Pan", note.Content);
        Assert.Contains("- [x] Leche", note.Content);
        Assert.Contains("![foto.png](attachments/foto.png)", note.Content);
        Assert.Contains("casa", note.Content);
        Assert.Contains("created: \"2026-01-05\"", note.Content);
        var asset = Assert.Single(plan.Assets);
        Assert.Equal(png, asset.Bytes);
        Assert.Equal(Path.Combine("attachments", "foto.png"), asset.RelativePath);
        Assert.Contains(plan.Issues, i => i.Kind == ExternalImportService.ImportIssueKind.UnsupportedAttachment && i.Name.Contains("contrato.doc", StringComparison.Ordinal));
        Assert.Contains(plan.Issues, i => i.Kind == ExternalImportService.ImportIssueKind.EncryptedContent);
        Assert.Contains(plan.Issues, i => i.Kind == ExternalImportService.ImportIssueKind.Empty && i.Name == "Vacía");
    }

    [Fact]
    public void ReadEvernote_NotAnEnex_Throws()
    {
        var file = Write("raro.enex", "<otra-cosa/>");
        Assert.Throws<InvalidDataException>(() => ExternalImportService.ReadEvernote(file));
    }

    [Fact]
    public void ReadEvernote_ExternalEntityInDoctype_IsNotResolved()
    {
        var secret = Write("secreto.txt", "CONTENIDO-SECRETO");
        var file = Write("xxe.enex",
            $"<?xml version=\"1.0\"?><!DOCTYPE en-export [<!ENTITY x SYSTEM \"file://{secret}\">]><en-export><note><title>T</title><content><![CDATA[<en-note><div>&x;</div></en-note>]]></content></note></en-export>");

        var plan = ExternalImportService.ReadEvernote(file);

        Assert.DoesNotContain(plan.Notes, n => n.Content.Contains("CONTENIDO-SECRETO", StringComparison.Ordinal));
    }

    private static string Md5(byte[] bytes)
    {
#pragma warning disable CA5351
        return Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
#pragma warning restore CA5351
    }

    // ── Google Keep ──────────────────────────────────────────────────────────────────────

    private string KeepZip()
    {
        var zipPath = Path.Combine(_root, "takeout.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        Entry(zip, "Takeout/Keep/Receta.json", """{"title":"Receta","textContent":"Mezclar todo","labels":[{"name":"Cocina"}],"createdTimestampUsec":1767606900000000,"attachments":[{"filePath":"imagen1.png","mimetype":"image/png"}]}""");
        Entry(zip, "Takeout/Keep/Compra.json", """{"title":"","listContent":[{"text":"Pan","isChecked":false},{"text":"Leche","isChecked":true}]}""");
        Entry(zip, "Takeout/Keep/Vieja.json", """{"title":"Vieja","textContent":"x","isTrashed":true}""");
        Entry(zip, "Takeout/Keep/Rota.json", "{no es json");
        Entry(zip, "Takeout/Keep/Receta.html", "<html></html>");
        Entry(zip, "Takeout/Keep/imagen1.png", "PNG");
        return zipPath;
    }

    private static void Entry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(content);
    }

    [Fact]
    public void ReadGoogleKeep_Zip_ReadsNotesListsLabelsAttachments_AndReportsTrashAndBrokenFiles()
    {
        var plan = ExternalImportService.ReadGoogleKeep(KeepZip());

        Assert.Equal(2, plan.Notes.Count);
        var recipe = plan.Notes.Single(n => n.RelativePath == "Receta.md");
        Assert.Contains("Mezclar todo", recipe.Content);
        Assert.Contains("cocina", recipe.Content);
        Assert.Contains("![imagen1.png](imagen1.png)", recipe.Content);
        Assert.Contains("created: \"2026-01-05\"", recipe.Content);
        var list = plan.Notes.Single(n => n.RelativePath != "Receta.md");
        Assert.Contains("- [ ] Pan", list.Content);
        Assert.Contains("- [x] Leche", list.Content);
        Assert.Single(plan.Assets);
        Assert.Contains(plan.Issues, i => i.Kind == ExternalImportService.ImportIssueKind.Trashed && i.Name == "Vieja");
        Assert.Contains(plan.Issues, i => i.Kind == ExternalImportService.ImportIssueKind.InvalidFormat);
    }

    [Fact]
    public void ReadGoogleKeep_Directory_ReadsTheSameExport()
    {
        var extracted = Path.Combine(_root, "extracted");
        ZipFile.ExtractToDirectory(KeepZip(), extracted);

        var plan = ExternalImportService.ReadGoogleKeep(extracted);

        Assert.Equal(2, plan.Notes.Count);
        var asset = Assert.Single(plan.Assets);
        Assert.NotNull(asset.SourcePath);
    }

    [Fact]
    public void ReadGoogleKeep_MissingAttachment_KeepsTheNoteAndReportsIt()
    {
        var zipPath = Path.Combine(_root, "sin-imagen.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            Entry(zip, "Keep/N.json", """{"title":"N","textContent":"t","attachments":[{"filePath":"falta.png"}]}""");

        var plan = ExternalImportService.ReadGoogleKeep(zipPath);

        Assert.Single(plan.Notes);
        Assert.Empty(plan.Assets);
        Assert.Contains(plan.Issues, i => i.Kind == ExternalImportService.ImportIssueKind.MissingAttachment);
    }

    // ── TickTick ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadTickTick_SkipsPrefaceAndMapsFields()
    {
        var csv = Write("ticktick.csv",
            "\"Date: 2026-10-01+0000\"\n\"Version: 6.4\"\n\"Status: 0 Normal 1 Completed 2 Archived\"\n" +
            "\"Folder Name\",\"List Name\",\"Title\",\"Kind\",\"Tags\",\"Content\",\"Is Check list\",\"Start Date\",\"Due Date\",\"Reminder\",\"Repeat\",\"Priority\",\"Status\"\n" +
            "\"Casa\",\"Inbox\",\"Pagar luz\",\"TEXT\",\"\",\"Antes del lunes\",\"N\",\"\",\"2026-10-15T08:00:00+0000\",\"\",\"FREQ=MONTHLY;INTERVAL=1\",\"5\",\"0\"\n" +
            "\"Casa\",\"Inbox\",\"Hecha\",\"TEXT\",\"\",\"\",\"N\",\"\",\"\",\"\",\"\",\"1\",\"2\"\n" +
            "\"Casa\",\"Inbox\",\"Idea\",\"NOTE\",\"\",\"Texto de la nota\",\"N\",\"\",\"\",\"\",\"\",\"0\",\"0\"\n" +
            "\"Casa\",\"Inbox\",\"Lista\",\"CHECKLIST\",\"\",\"▫Uno\n▪Dos\",\"Y\",\"\",\"\",\"\",\"\",\"3\",\"0\"\n");

        var plan = ExternalImportService.ReadTickTick(csv);

        Assert.Equal("TickTick", plan.Source);
        Assert.Equal(3, plan.Tasks.Count);
        var first = plan.Tasks[0];
        Assert.Equal("Pagar luz", first.Title);
        Assert.Equal(KanbanPriority.High, first.Priority);
        Assert.Equal(new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc), first.DueUtc);
        Assert.False(first.Completed);
        Assert.True(plan.Tasks[1].Completed);
        Assert.Equal(KanbanPriority.Low, plan.Tasks[1].Priority);
        Assert.Equal("- [ ] Uno\n- [x] Dos", plan.Tasks[2].Description);
        var note = Assert.Single(plan.Notes);
        Assert.Equal("Idea.md", note.RelativePath);
        Assert.Equal("Texto de la nota", note.Content);
        Assert.Contains(plan.Issues, i => i.Kind == ExternalImportService.ImportIssueKind.RecurrenceDropped && i.Name == "Pagar luz");
    }

    [Fact]
    public void ReadTickTick_WithoutTitleColumn_Throws()
    {
        var csv = Write("malo.csv", "a,b,c\n1,2,3\n");
        Assert.Throws<InvalidDataException>(() => ExternalImportService.ReadTickTick(csv));
    }

    // ── Lectores existentes: lo omitido ahora se anota ───────────────────────────────────

    [Fact]
    public void ExistingReaders_RecordWhatTheySkip()
    {
        var obsidian = Path.Combine(_root, "obsidian");
        Write("obsidian/Nota.md", "# Nota");
        Write("obsidian/hoja.xlsx", "x");
        var plan = ExternalImportService.ReadObsidian(obsidian);
        Assert.Contains(plan.Issues, i => i.Kind == ExternalImportService.ImportIssueKind.UnsupportedItem && i.Name == "hoja.xlsx");

        var todoist = Write("todoist.csv", "TYPE,CONTENT\ntask,Una\nsection,Sección\ntask,\n");
        var todoistPlan = ExternalImportService.ReadTodoist(todoist);
        Assert.Single(todoistPlan.Tasks);
        Assert.Contains(todoistPlan.Issues, i => i.Name == "Sección");
        Assert.Contains(todoistPlan.Issues, i => i.Kind == ExternalImportService.ImportIssueKind.Empty);

        var ics = Write("cal.ics", "BEGIN:VEVENT\nSTATUS:CANCELLED\nDTSTART:20261015T100000Z\nSUMMARY:Cancelado\nEND:VEVENT\nBEGIN:VEVENT\nSUMMARY:Sin fecha\nEND:VEVENT\n");
        var icsPlan = ExternalImportService.ReadGoogleCalendar(ics);
        Assert.Empty(icsPlan.Events);
        Assert.Contains(icsPlan.Issues, i => i.Kind == ExternalImportService.ImportIssueKind.Cancelled);
        Assert.Contains(icsPlan.Issues, i => i.Kind == ExternalImportService.ImportIssueKind.NoDate);
    }

    // ── Informe y escritura aislada ──────────────────────────────────────────────────────

    [Fact]
    public void ImportWithReport_WritesOnlyInsideTheVaultAndTheTemporaryEntities()
    {
        var storage = Path.Combine(_root, "storage");
        var vaultDir = Path.Combine(_root, "vault");
        Directory.CreateDirectory(storage);
        var settings = new SettingsService(storage);
        var vault = new VaultService(settings);
        vault.InitializeVault(vaultDir, seedWelcomeNote: false);
        var kanban = new KanbanService(vault);
        var reminders = new ReminderService(vault);
        var importer = new ExternalImportService(vault, kanban, reminders);

        Write("graph/pages/Proyecto.md", "- TODO Primera tarea\n");
        Write("graph/pages/legacy.org", "* org");
        var sourceBefore = Directory.EnumerateFiles(Path.Combine(_root, "graph"), "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllText);
        var plan = ExternalImportService.ReadLogseq(Path.Combine(_root, "graph"));

        var result = importer.Apply(plan);
        var reportPath = importer.WriteReport(plan, result);

        Assert.NotNull(reportPath);
        Assert.StartsWith(vault.VaultPath, reportPath, StringComparison.Ordinal);
        var report = File.ReadAllText(reportPath!);
        Assert.Contains("Logseq", report, StringComparison.Ordinal);
        Assert.Contains("legacy.org", report, StringComparison.Ordinal);
        Assert.Contains(vault.GetAllNotesAcrossVault(), n => n.Title == "Proyecto");
        // La carpeta de origen queda intacta y todo lo nuevo vive bajo el vault.
        Assert.Equal(sourceBefore, Directory.EnumerateFiles(Path.Combine(_root, "graph"), "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllText));
        var created = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(Path.Combine(_root, "graph"), StringComparison.Ordinal)
                && !f.StartsWith(storage, StringComparison.Ordinal)
                && !f.StartsWith(vaultDir, StringComparison.Ordinal));
        Assert.Empty(created);
    }

    [Fact]
    public void ImportWithReport_TasksOnly_CreatesTheFolderForTheReport()
    {
        var storage = Path.Combine(_root, "storage");
        Directory.CreateDirectory(storage);
        var vault = new VaultService(new SettingsService(storage));
        vault.InitializeVault(Path.Combine(_root, "vault"), seedWelcomeNote: false);
        var importer = new ExternalImportService(vault, new KanbanService(vault), new ReminderService(vault));
        var csv = Write("t.csv", "Title,Kind,Status,Repeat\nA,TEXT,0,FREQ=DAILY\n");
        var plan = ExternalImportService.ReadTickTick(csv);

        var result = importer.Apply(plan);
        var reportPath = importer.WriteReport(plan, result);

        Assert.Equal(new ExternalImportService.Result(0, 1, 0, 0), result);
        Assert.Contains("Importado de TickTick", reportPath, StringComparison.Ordinal);
        Assert.Contains("A", File.ReadAllText(reportPath!), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildReport_ListsImportedAndSkipped_AndCapsHugeLists()
    {
        var issues = Enumerable.Range(0, 10).Select(i => new ExternalImportService.ImportIssue($"f{i}`\n.bin", ExternalImportService.ImportIssueKind.UnsupportedItem)).ToList();
        var plan = new ExternalImportService.Plan("Prueba", [], [], [], []) { Issues = issues, IssuesOmitted = 7 };

        var report = ExternalImportService.BuildReport(plan, new ExternalImportService.Result(0, 0, 0, 0), interrupted: true);

        Assert.Contains("# ", report, StringComparison.Ordinal);
        Assert.Contains("- `f0'", report, StringComparison.Ordinal);
        Assert.DoesNotContain("`\n.bin", report, StringComparison.Ordinal);
        Assert.Contains("7", report, StringComparison.Ordinal);

        var many = new ExternalImportService.Plan("X", [], [], [], []);
        Assert.Contains("##", ExternalImportService.BuildReport(many, new ExternalImportService.Result(0, 0, 0, 0)), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_Interrupted_ReportsWhatWasCompleted()
    {
        var storage = Path.Combine(_root, "storage");
        Directory.CreateDirectory(storage);
        var vault = new VaultService(new SettingsService(storage));
        // Sin vault abierto el lote de notas falla antes de crear nada: ImportInterruptedException con progreso 0.
        var importer = new ExternalImportService(vault, new KanbanService(vault), new ReminderService(vault));
        var plan = new ExternalImportService.Plan("Prueba", [new ExternalImportService.Note("a.md", "x")], [], [], []);

        var ex = Assert.Throws<ExternalImportService.ImportInterruptedException>(() => importer.Apply(plan));

        Assert.Equal(new ExternalImportService.Result(0, 0, 0, 0), ex.Completed);
        Assert.Null(importer.WriteReport(plan, ex.Completed, interrupted: true));
    }
}
