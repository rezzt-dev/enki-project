using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Carpetas y archivos que no son notas (la sección Archivos): listar, leer los de texto,
/// renombrar, mover o copiar y enviar a la papelera. Las carpetas pasan por las operaciones gestionadas de
/// <see cref="VaultService"/> (<see cref="VaultService.CreateManagedFolder"/>…), cuyos eventos ya mantienen
/// al día las pestañas abiertas y los enlaces de Kanban.</summary>
public sealed partial class AiVaultToolbox
{
    private void RegisterFolderAndFileTools()
    {
        Register("list_folders", AiToolScope.Content, AiToolEffect.Read,
            "Lists every folder of a workspace as paths relative to its root.",
            Schema(WorkspaceParam()),
            call =>
            {
                var workspace = Workspace(call);
                var folders = new List<string>();
                void Collect(IEnumerable<NoteFolder> level)
                {
                    foreach (var folder in level)
                    {
                        folders.Add(RelativePath(workspace, folder.FullPath));
                        Collect(folder.Children);
                    }
                }
                Collect(_vault.GetFolderTree(workspace.RootPath));
                return new { Workspace = WorkspaceKey(workspace), Folders = folders };
            });

        Register("create_folder", AiToolScope.Content, AiToolEffect.Change,
            "Creates a folder. 'name' is a single folder name; create nested folders one level at a time. "
            + "If the name is taken, a numeric suffix is added.",
            Schema(WorkspaceParam(), Req("name", Text("Folder name.")),
                Opt("parent", Text("Parent folder relative to the workspace (omit for the root)."))),
            call =>
            {
                var name = call.RequireText("name").Trim();
                call.Target(AiToolTargetKind.Folder, name);
                var workspace = Workspace(call);
                var folder = _vault.CreateManagedFolder(ExistingFolder(workspace, call.GetText("parent")), name);
                call.Target(AiToolTargetKind.Folder, folder.Name, folder.FullPath);
                return new { Workspace = WorkspaceKey(workspace), Path = RelativePath(workspace, folder.FullPath) };
            });

        Register("rename_folder", AiToolScope.Content, AiToolEffect.Change,
            "Renames a folder. Notes inside keep working: open tabs and linked Kanban cards follow the new path.",
            Schema(WorkspaceParam(), Req("path", Text("Folder path relative to the workspace.")), Req("new_name", Text("New folder name."))),
            call =>
            {
                var (workspace, path) = ExistingSubfolder(call);
                var folder = _vault.RenameManagedFolder(path, call.RequireText("new_name").Trim());
                call.Target(AiToolTargetKind.Folder, folder.Name, folder.FullPath);
                return new { Workspace = WorkspaceKey(workspace), Path = RelativePath(workspace, folder.FullPath) };
            });

        RegisterAsync("move_folder", AiToolScope.Content, AiToolEffect.Change,
            "Moves (or copies, with copy=true) a folder with everything inside into another folder, possibly in another workspace of this chat.",
            Schema(WorkspaceParam(), Req("path", Text("Folder path relative to the workspace.")),
                Opt("destination_parent", Text("Folder that will contain it, relative to the destination workspace (omit for its root).")),
                Opt("destination_workspace", Text("Destination workspace (omit to stay in the same one).")),
                Opt("copy", Flag("Copy instead of moving (default false)."))),
            async call =>
            {
                var (workspace, path) = ExistingSubfolder(call);
                var destinationWorkspace = call.Has("destination_workspace") ? Workspace(call, "destination_workspace") : workspace;
                var parent = ExistingFolder(destinationWorkspace, call.GetText("destination_parent"));
                await RequireApprovalToLeaveProtectedSessionAsync(call, path, parent, Path.GetFileName(path));
                var folder = _vault.TransferFolder(path, parent, cut: call.GetFlag("copy") != true);
                call.Target(AiToolTargetKind.Folder, folder.Name, folder.FullPath);
                return new { Workspace = WorkspaceKey(destinationWorkspace), Path = RelativePath(destinationWorkspace, folder.FullPath) };
            });

        Register("delete_folder", AiToolScope.Content, AiToolEffect.Change,
            "Moves a folder, with every note and file inside, to the trash.",
            Schema(WorkspaceParam(), Req("path", Text("Folder path relative to the workspace."))),
            call =>
            {
                var (_, path) = ExistingSubfolder(call);
                _vault.DeleteFolder(new NoteFolder { FullPath = path });
                return new { Trashed = true, Name = Path.GetFileName(path) };
            });

        Register("list_files", AiToolScope.Content, AiToolEffect.Read,
            "Lists the non-note files of a workspace (images, PDFs, audio, video, code, documents…) with kind, size and date. Read them with read_file.",
            Schema(WorkspaceParam(), Opt("folder", Text("Folder relative to the workspace (omit for the root).")),
                Opt("recursive", Flag("Include subfolders (default true).")),
                Opt("kind", Choice("Only files of this kind.", "image", "pdf", "audio", "video", "code", "other")), LimitParam()),
            call =>
            {
                var workspace = Workspace(call);
                var folder = ExistingFolder(workspace, call.GetText("folder"));
                var kind = call.GetText("kind");
                var files = (call.GetFlag("recursive") ?? true ? _vault.GetFilesRecursive(folder) : _vault.GetFiles(folder))
                    .Where(f => kind is null || string.Equals(f.Kind.ToString(), kind, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.ModifiedUtc)
                    .ToList();
                var limit = Limit(call);
                return new
                {
                    Workspace = WorkspaceKey(workspace),
                    Total = files.Count,
                    Truncated = files.Count > limit,
                    Files = files.Take(limit).Select(f => FileSummary(workspace, f))
                };
            });

        Register(ReadFileTool, AiToolScope.Content, AiToolEffect.Read,
            "Reads a non-note file. Text and code come back as is; PDFs as text per page ([page N] markers); Word, PowerPoint "
            + "and OpenDocument as plain text; Excel/ODS as one section per sheet with tab-separated rows (first field = row number); "
            + "ZIP archives as a file listing; images as the image itself (downscaled) for you to look at. Audio and video cannot be read. "
            + $"Long text is returned in chunks of max_chars (default {AiFileReader.DefaultMaxChars}): if next_offset is present, call again "
            + "with that offset only if you need the rest. For long PDFs, read only the pages you need with 'pages'. Pages without a text "
            + $"layer (scans) are rendered as images automatically when none of the requested pages has text, or with as_images=true (max {AiFileReader.MaxRenderedPages} pages per call).",
            Schema(WorkspaceParam(), Req("path", Text("File path relative to the workspace.")),
                Opt("offset", Number("Character offset to continue from (the next_offset of a previous call).")),
                Opt("max_chars", Number($"Maximum characters of text to return (default {AiFileReader.DefaultMaxChars}, max {AiFileReader.MaxCharsLimit}).")),
                Opt("pages", Text("PDF only: pages to read, e.g. \"3\", \"2-5\" or \"1,4,7-9\" (default: all).")),
                Opt("as_images", Flag($"PDF only: also render the requested pages as images, to see charts, tables or scans (max {AiFileReader.MaxRenderedPages}).")),
                Opt("detail", Choice("Image resolution: low (512 px), standard (1024 px, default) or high (1568 px). Use high only for small print.",
                    "low", "standard", "high"))),
            call =>
            {
                var (workspace, file) = ExistingFile(call);
                call.Target(AiToolTargetKind.File, file.FileName, file.FullPath);
                var format = AiFileReader.FormatOf(file);
                switch (AiFileReader.Check(file, allowSniffing: true))
                {
                    case AiFileProblem.Unsupported:
                        throw AiToolCall.Invalid(format == AiFileFormat.Media
                            ? $"\"{file.FileName}\" is {file.Kind.ToString().ToLowerInvariant()}: its content cannot be read (there is no transcription). Use its name, size and date."
                            : $"\"{file.FileName}\" is a binary file in a format Enki cannot read.");
                    case AiFileProblem.TooLarge:
                        throw AiToolCall.Invalid($"\"{file.FileName}\" is too large to read ({file.SizeBytes} bytes; the limit for {AiFileReader.Describe(format)} is {AiFileReader.MaxBytes(format)}).");
                }
                var options = new AiFileReadOptions
                {
                    Offset = call.GetInteger("offset", 0) ?? 0,
                    MaxChars = call.GetInteger("max_chars", 500, AiFileReader.MaxCharsLimit) ?? AiFileReader.DefaultMaxChars,
                    Pages = call.GetText("pages"),
                    PagesAsImages = call.GetFlag("as_images") == true,
                    ImageSide = call.GetText("detail") switch
                    {
                        "low" => AiFileReader.LowImageSide,
                        "high" => AiFileReader.HighImageSide,
                        _ => AiFileReader.StandardImageSide
                    }
                };
                if (format != AiFileFormat.Pdf && (options.Pages is not null || options.PagesAsImages))
                    throw AiToolCall.Invalid("'pages' and 'as_images' only apply to PDF files.");
                AiFileExtract extract;
                try
                {
                    extract = AiFileReader.Read(_vault, file, options);
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
                {
                    throw AiToolCall.Invalid(ex.Message);
                }
                call.Images.AddRange(extract.Images);
                return new
                {
                    Workspace = WorkspaceKey(workspace),
                    Path = RelativePath(workspace, file.FullPath),
                    Format = extract.Format.ToString().ToLowerInvariant(),
                    file.SizeBytes,
                    extract.PageCount,
                    PagesWithoutText = extract.PagesWithoutText.Count > 0 ? AiFileReader.FormatPages(extract.PagesWithoutText) : null,
                    RenderedPages = extract.RenderedPages.Count > 0 ? AiFileReader.FormatPages(extract.RenderedPages) : null,
                    Image = extract.Format == AiFileFormat.Image
                        ? new { Width = extract.OriginalWidth, Height = extract.OriginalHeight, SentWidth = extract.ImageWidth, SentHeight = extract.ImageHeight, extract.Taken }
                        : null,
                    TotalChars = extract.Format == AiFileFormat.Image ? (int?)null : extract.TotalChars,
                    Offset = extract.Offset > 0 ? extract.Offset : (int?)null,
                    extract.NextOffset,
                    ExtractionLimited = extract.ExtractionLimited ? true : (bool?)null,
                    Content = extract.Format == AiFileFormat.Image ? null : extract.Text
                };
            });

        Register("rename_file", AiToolScope.Content, AiToolEffect.Change,
            "Renames a non-note file. Include the extension in 'new_name'; if the name is taken, a numeric suffix is added.",
            Schema(WorkspaceParam(), Req("path", Text("File path relative to the workspace.")), Req("new_name", Text("New file name with extension."))),
            call =>
            {
                var (workspace, file) = ExistingFile(call);
                call.Target(AiToolTargetKind.File, file.FileName, file.FullPath);
                var renamed = _vault.RenameFile(file, call.RequireText("new_name").Trim());
                call.Target(AiToolTargetKind.File, renamed.FileName, renamed.FullPath);
                return FileSummary(workspace, renamed);
            });

        RegisterAsync("move_file", AiToolScope.Content, AiToolEffect.Change,
            "Moves (or copies, with copy=true) a non-note file to another folder, possibly in another workspace of this chat.",
            Schema(WorkspaceParam(), Req("path", Text("File path relative to the workspace.")),
                Opt("destination_folder", Text("Destination folder relative to the destination workspace (omit for its root).")),
                Opt("destination_workspace", Text("Destination workspace (omit to stay in the same one).")),
                Opt("copy", Flag("Copy instead of moving (default false)."))),
            async call =>
            {
                var (_, file) = ExistingFile(call);
                call.Target(AiToolTargetKind.File, file.FileName, file.FullPath);
                var destinationWorkspace = call.Has("destination_workspace") ? Workspace(call, "destination_workspace") : Workspace(call);
                var destination = ExistingFolder(destinationWorkspace, call.GetText("destination_folder"));
                await RequireApprovalToLeaveProtectedSessionAsync(call, file.FullPath, destination, file.FileName);
                if (call.GetFlag("copy") == true)
                {
                    var copied = _vault.CopyFileToFolder(file.FullPath, destination);
                    call.Target(AiToolTargetKind.File, Path.GetFileName(copied), copied);
                    return new { Workspace = WorkspaceKey(destinationWorkspace), Path = RelativePath(destinationWorkspace, copied) };
                }
                var moved = _vault.MoveFile(file, destination);
                call.Target(AiToolTargetKind.File, moved.FileName, moved.FullPath);
                return FileSummary(destinationWorkspace, moved);
            });

        Register("delete_file", AiToolScope.Content, AiToolEffect.Change,
            "Moves a non-note file to the trash.",
            Schema(WorkspaceParam(), Req("path", Text("File path relative to the workspace."))),
            call =>
            {
                var (_, file) = ExistingFile(call);
                call.Target(AiToolTargetKind.File, file.FileName);
                _vault.DeleteFile(file);
                return new { Trashed = true, Name = file.FileName };
            });
    }

    /// <summary>Una carpeta existente que no es la raíz del espacio (esa no se renombra, mueve ni borra).</summary>
    private (AiWorkspace Workspace, string Path) ExistingSubfolder(AiToolCall call)
    {
        var workspace = Workspace(call);
        var requested = call.RequireText("path");
        var path = ResolvePath(workspace, requested);
        call.Target(AiToolTargetKind.Folder, Path.GetFileName(path), path);
        return Directory.Exists(path)
            ? (workspace, path)
            : throw new AiToolException(AiToolErrorKind.NotFound, $"There is no folder \"{requested}\". Use list_folders.");
    }

    private (AiWorkspace Workspace, VaultFile File) ExistingFile(AiToolCall call)
    {
        var workspace = Workspace(call);
        var requested = call.RequireText("path");
        var path = ResolvePath(workspace, requested);
        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw AiToolCall.Invalid($"\"{requested}\" is a note: use the note tools (read_note, move_note…).");
        if (!File.Exists(path))
            throw new AiToolException(AiToolErrorKind.NotFound, $"There is no file \"{requested}\". Use list_files.");
        return (workspace, AiFileReader.Describe(path));
    }

    private const string ReadFileTool = "read_file";

    /// <summary><c>read_file</c> sobre uno de los archivos que el usuario adjuntó a este turno
    /// (<see cref="AiToolContext.AttachedFiles"/>). Cualquier argumento que no se pueda resolver cuenta como
    /// "no": la herramienta pedirá entonces el permiso de consulta como siempre.</summary>
    private bool IsAttachedFileRead(string tool, System.Text.Json.JsonElement arguments, AiToolContext context)
    {
        if (tool != ReadFileTool || context.AttachedFiles.Count == 0) return false;
        try
        {
            var call = new AiToolCall(context, arguments, new AiToolActivity { Tool = tool });
            return context.AttachedFiles.Contains(ResolvePath(Workspace(call), call.RequireText("path")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static object FileSummary(AiWorkspace workspace, VaultFile file) => new
    {
        Workspace = WorkspaceKey(workspace),
        Path = RelativePath(workspace, file.FullPath),
        Kind = file.Kind.ToString().ToLowerInvariant(),
        file.SizeBytes,
        Modified = FormatInstant(file.ModifiedUtc)
    };
}
