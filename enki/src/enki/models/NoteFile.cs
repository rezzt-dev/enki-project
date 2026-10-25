using System;
using System.Collections.Generic;
using enki.Localization;

namespace enki.Models;

public sealed class NoteFile
{
    public required string FullPath { get; set; }
    public required string Title { get; set; }
    public string Preview { get; set; } = string.Empty;
    public DateTime ModifiedUtc { get; set; }
    public int WordCount { get; set; }

    /// <summary>Etiquetas persistidas en el frontmatter YAML del archivo <c>.md</c>. Se leen y escriben
    /// a través de <see cref="Services.NoteTagHelper"/>; el frontmatter visible forma parte del texto
    /// plano de la nota.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Propiedades de primer nivel del frontmatter YAML; la nota Markdown es la fuente real.</summary>
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string FileName => System.IO.Path.GetFileName(FullPath);

    public string FileNameWithoutExtension => System.IO.Path.GetFileNameWithoutExtension(FullPath);

    public string ModifiedLabel => RelativeTimeFormatter.Format(ModifiedUtc);

    /// <summary>A shallow copy with its own <see cref="Tags"/> list — <see cref="Services.VaultNoteCache"/>
    /// hands one of these out on every read instead of the same shared instance, so a caller that mutates
    /// its own copy in place (<c>VaultService.RenameNote</c>/<c>MoveNote</c> both reassign
    /// <see cref="FullPath"/> directly on the <see cref="NoteFile"/> they were handed) can never corrupt
    /// the cached entry, or a snapshot some other caller took moments earlier
    /// (docs/analisis-proyecto/roadmap.md, Fase 6).</summary>
    public NoteFile Clone() => new()
    {
        FullPath = FullPath,
        Title = Title,
        Preview = Preview,
        ModifiedUtc = ModifiedUtc,
        WordCount = WordCount,
        Tags = [..Tags],
        Properties = new Dictionary<string, string>(Properties, StringComparer.OrdinalIgnoreCase)
    };
}
