using System;
using System.IO;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>
/// Escribe una idea de captura rápida directamente como <see cref="NoteFile"/> o
/// <see cref="KanbanCard"/>. No conserva un almacén propio: el borrador pertenece al viewmodel y el
/// resultado vive desde el primer momento en el vault o el tablero reales.
/// </summary>
public sealed class QuickCaptureService(VaultService vaultService, KanbanService kanbanService)
{
    public const int MaximumTitleLength = 100;

    /// <summary>Crea la entidad definitiva. La carpeta <c>Inbox/</c> se crea dentro del vault cuando no
    /// existe; al ir a Kanban, se rechaza un destino explícito que ya no exista.</summary>
    public QuickCaptureResult Capture(string text, QuickCaptureDestination destination, string? kanbanColumnId = null, string? kanbanBoardId = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("La captura no puede estar vacía.", nameof(text));
        if (!vaultService.IsVaultConfigured) throw new VaultUnavailableException(vaultService.VaultPath);

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var lines = normalized.Split('\n');
        var firstContentLine = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
        var title = BuildTitle(lines[firstContentLine]);

        if (destination == QuickCaptureDestination.InboxNote)
        {
            var inbox = Path.Combine(vaultService.VaultPath, "Inbox");
            Directory.CreateDirectory(inbox);
            var body = string.Join('\n', lines.Skip(firstContentLine + 1)).Trim();
            var markdown = body.Length == 0 ? $"# {title}\n" : $"# {title}\n\n{body}\n";
            var note = vaultService.CreateNote(title, inbox, markdown);
            return new QuickCaptureResult(destination, note.Title, note.FullPath, null);
        }

        var board = kanbanBoardId is null ? kanbanService.DefaultBoard
            : kanbanService.Boards.FirstOrDefault(b => b.Id == kanbanBoardId)
                ?? throw new InvalidOperationException("El tablero destino ya no existe.");
        var column = kanbanColumnId is null ? board.Columns.FirstOrDefault()
            : board.Columns.FirstOrDefault(c => c.Id == kanbanColumnId);
        if (column is null) throw new InvalidOperationException("La columna destino ya no existe.");
        var description = string.Join('\n', lines.Skip(firstContentLine + 1)).Trim();
        var card = new KanbanCard
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            Description = description
        };
        column.Cards.Add(card);
        try { kanbanService.Save(); }
        catch { column.Cards.Remove(card); throw; }
        return new QuickCaptureResult(destination, title, null, card.Id);
    }

    internal static string BuildTitle(string firstLine)
    {
        var title = firstLine.Trim().TrimStart('#').Trim();
        if (title.Length == 0) title = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        return title.Length <= MaximumTitleLength ? title : title[..MaximumTitleLength].TrimEnd();
    }
}
