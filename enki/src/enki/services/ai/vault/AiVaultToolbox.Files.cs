using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using enki.Models;

namespace enki.Services;

/// <summary>Carpetas y archivos que no son notas (la sección Archivos): listar, leer los de texto,
/// renombrar, mover o copiar y enviar a la papelera. Las carpetas pasan por las operaciones gestionadas de
/// <see cref="VaultService"/> (<see cref="VaultService.CreateManagedFolder"/>…), cuyos eventos ya mantienen
/// al día las pestañas abiertas y los enlaces de Kanban.</summary>
public sealed partial class AiVaultToolbox
{
    /// <summary>Tamaño máximo de un archivo de texto que se lee entero para el modelo.</summary>
    private const long MaxTextFileBytes = 1_000_000;

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
            "Lists the non-note files of a workspace (images, PDFs, audio, video, code, documents…) with kind, size and date.",
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

        Register("read_text_file", AiToolScope.Content, AiToolEffect.Read,
            $"Reads a text file that is not a note (code, CSV, JSON, plain text…), up to {MaxNoteChars} characters. "
            + "Binary files (images, PDFs, media) cannot be read.",
            Schema(WorkspaceParam(), Req("path", Text("File path relative to the workspace."))),
            call =>
            {
                var (workspace, file) = ExistingFile(call);
                call.Target(AiToolTargetKind.File, file.FileName, file.FullPath);
                if (file.Kind != VaultFileKind.Code)
                    throw AiToolCall.Invalid($"\"{file.FileName}\" is a {file.Kind.ToString().ToLowerInvariant()} file; only text files can be read.");
                if (file.SizeBytes > MaxTextFileBytes)
                    throw AiToolCall.Invalid($"\"{file.FileName}\" is too large to read ({file.SizeBytes} bytes).");
                var text = Encoding.UTF8.GetString(_vault.ReadFileBytes(file.FullPath));
                if (text.Contains('\0', StringComparison.Ordinal))
                    throw AiToolCall.Invalid($"\"{file.FileName}\" is not a text file.");
                return new
                {
                    Workspace = WorkspaceKey(workspace),
                    Path = RelativePath(workspace, file.FullPath),
                    Truncated = text.Length > MaxNoteChars,
                    Content = Truncate(text, MaxNoteChars)
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
        var info = new FileInfo(path);
        return (workspace, new VaultFile
        {
            FullPath = path,
            ModifiedUtc = info.LastWriteTimeUtc,
            SizeBytes = info.Length,
            Kind = VaultFile.KindFromPath(path)
        });
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
