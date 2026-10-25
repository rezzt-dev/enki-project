using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public sealed class ExternalImportServiceTests
{
    [Fact]
    public void TodoistCsv_QuotedFieldsAndCompletedTask_AreRead()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "TYPE,CONTENT,DESCRIPTION,PRIORITY,DATE,CHECKED\n" +
                "task,\"Comprar pan, leche\",\"Línea 1\nLínea 2\",4,2026-10-16,1\n");
            var plan = ExternalImportService.ReadTodoist(file);
            var task = Assert.Single(plan.Tasks);
            Assert.Equal("Comprar pan, leche", task.Title);
            Assert.Equal("Línea 1\nLínea 2", task.Description);
            Assert.True(task.Completed);
            Assert.NotNull(task.DueUtc);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void GoogleCalendar_FoldedTextTimezoneAndCancelledEvent_AreHandled()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "BEGIN:VCALENDAR\nBEGIN:VEVENT\nDTSTART;TZID=Europe/Madrid:20261015T120000\n" +
                "DTEND;TZID=Europe/Madrid:20261015T130000\nSUMMARY:Reunión \n semanal\nRRULE:FREQ=WEEKLY;COUNT=5\nEND:VEVENT\n" +
                "BEGIN:VEVENT\nSTATUS:CANCELLED\nDTSTART:20261015T100000Z\nSUMMARY:Cancelado\nEND:VEVENT\nEND:VCALENDAR\n");
            var item = Assert.Single(ExternalImportService.ReadGoogleCalendar(file).Events);
            Assert.Equal("Reunión semanal", item.Title);
            Assert.Equal(new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc), item.StartUtc);
            Assert.Equal(60, item.DurationMinutes);
            Assert.Equal(1, ExternalImportService.ReadGoogleCalendar(file).RecurringSeries);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void NotionZip_UnsafeAndHiddenPaths_AreSkipped()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            {
                Write(zip, "Workspace/Page.md", "# Página");
                Write(zip, "../outside.md", "escape");
                Write(zip, ".hidden/secret.md", "secret");
                Write(zip, "Workspace/Database.csv", "Name,State\nAlpha,Done\n");
                Write(zip, "Workspace/image.png", "asset");
            }
            var plan = ExternalImportService.ReadNotion(file);
            Assert.Equal(2, plan.Notes.Count);
            Assert.Contains(plan.Notes, n => n.RelativePath == "Workspace/Page.md" && n.Content == "# Página");
            Assert.Contains(plan.Notes, n => n.RelativePath == "Workspace/Database.md" && n.Content.Contains("| Alpha | Done |", StringComparison.Ordinal));
            Assert.Single(plan.Assets);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Apply_WritesExistingEntitiesAndKeepsImportedEventsSilent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var settings = new SettingsService(directory);
            var vault = new VaultService(settings);
            vault.InitializeVault(Path.Combine(directory, "vault"), seedWelcomeNote: false);
            var kanban = new KanbanService(vault);
            var reminders = new ReminderService(vault);
            var sourceAsset = Path.Combine(directory, "picture.png");
            File.WriteAllBytes(sourceAsset, [1, 2, 3]);
            var importer = new ExternalImportService(vault, kanban, reminders);
            var start = DateTime.UtcNow.AddDays(1);
            var plan = new ExternalImportService.Plan("Prueba",
                [new ExternalImportService.Note("Carpeta/Página.md", "# Página")],
                [new ExternalImportService.Task("Tarea", "Detalle", null, false, enki.Models.KanbanPriority.High)],
                [new ExternalImportService.CalendarEvent("Evento", "", start, 60)],
                [new ExternalImportService.Asset("Carpeta/picture.png", sourceAsset, null)]);

            var result = importer.Apply(plan);

            Assert.Equal(new ExternalImportService.Result(1, 1, 1, 1), result);
            Assert.Contains(vault.GetAllNotesAcrossVault(), note => note.Title == "Página");
            Assert.Contains(kanban.DefaultBoard.Columns.SelectMany(c => c.Cards), card => card.Title == "Tarea");
            var importedEvent = Assert.Single(reminders.ActiveReminders);
            Assert.True(importedEvent.IsTriggered);
            Assert.False(importedEvent.Dirty);
            Assert.True(importedEvent.IsImportedCalendarEvent);
            reminders.Reschedule(importedEvent.Id, DateTime.UtcNow.AddMinutes(-1));
            Assert.True(importedEvent.IsTriggered);
            Assert.Empty(reminders.PopDueReminders());
            Assert.True(File.Exists(Path.Combine(vault.VaultPath, "Importado de Prueba", "Carpeta", "picture.png")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
