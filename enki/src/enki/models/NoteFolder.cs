using System.Collections.Generic;
using System.IO;

namespace enki.Models;

public sealed class NoteFolder
{
    public required string FullPath { get; set; }
    public List<NoteFolder> Children { get; set; } = [];

    public string Name => Path.GetFileName(FullPath);
}
