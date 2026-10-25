using System;
using System.IO;
using System.Linq;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

[Collection("LocalizationMutatingTests")]
public sealed class EditorAttachmentsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-attachment-editor-" + Guid.NewGuid().ToString("N"));
    private readonly VaultService _vault;

    public EditorAttachmentsTests()
    {
        _vault = new VaultService(new SettingsService(_directory));
        _vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private EditorViewModel Editor(string text)
    {
        var note = _vault.CreateNote("Proyecto");
        _vault.WriteContent(note, text);
        return new EditorViewModel(_vault, note, () => { }, () => { });
    }

    [Fact]
    public void List_UsesCurrentBuffer_ExcludesCodeAndRemoteUrls()
    {
        var vm = Editor("![Informe](file.pdf)\n![Audio](audio.mp3)\n![[video.mp4]]\n`![fake](code.pdf)`\n```md\n![fake](fenced.pdf)\n```\n[web](https://example.org/doc.pdf)");
        vm.ShowAttachmentsCommand.Execute(null);
        Assert.Equal(new[] { "file.pdf", "audio.mp3", "video.mp4" }, vm.Attachments.Select(a => a.Target));
        vm.Close();
    }

    [Fact]
    public void Import_FileNameWithBrackets_RemainsARecognizableAttachment()
    {
        var source = Path.Combine(_directory, "Informe [final].pdf");
        File.WriteAllText(source, "pdf");
        var vm = Editor("");
        vm.Document.Text = vm.AttachFile(source)!;
        vm.RefreshAttachments();
        Assert.Equal("Informe [final].pdf", Assert.Single(vm.Attachments).FileName);
        Assert.Contains("%5Bfinal%5D.pdf", vm.Attachments[0].Target);
        vm.Close();
    }

    [Fact]
    public void RemoveReference_PreservesFile_AndSupportsUndo()
    {
        var path = Path.Combine(_vault.VaultPath, "file.pdf");
        File.WriteAllText(path, "pdf");
        var vm = Editor("Antes\n![Informe](file.pdf)\nDespués");
        vm.ShowAttachmentsCommand.Execute(null);
        var original = vm.Document.Text;
        vm.RemoveAttachmentReferenceCommand.Execute(Assert.Single(vm.Attachments));
        Assert.DoesNotContain("file.pdf", vm.Document.Text);
        Assert.True(File.Exists(path));
        vm.Document.UndoStack.Undo();
        Assert.Equal(original, vm.Document.Text);
        vm.Close();
    }

    [Fact]
    public void Annotation_IsMarkdown_PersistsThroughNormalCheckedSave()
    {
        File.WriteAllText(Path.Combine(_vault.VaultPath, "file.txt"), "contenido");
        var vm = Editor("![Documento](file.txt)");
        vm.OpenAttachmentTarget("file.txt");
        vm.AttachmentAnnotation = "Primera línea\nSegunda línea";
        vm.AddAttachmentAnnotationCommand.Execute(null);
        Assert.Contains("> [!note] [file.txt](file.txt)\n> Primera línea\n> Segunda línea", vm.Document.Text);
        Assert.Equal(string.Empty, vm.AttachmentAnnotation);
        vm.FlushPendingSave();
        Assert.Equal(vm.Document.Text, _vault.ReadContent(vm.Note));
        vm.Close();
    }

    [Fact]
    public void Annotation_InsideTable_PreservesTheTableRow()
    {
        File.WriteAllText(Path.Combine(_vault.VaultPath, "file.txt"), "contenido");
        const string table = "| Documento | Estado |\n| --- | --- |\n| ![Documento](file.txt) | Revisar |";
        var vm = Editor(table);
        vm.OpenAttachmentTarget("file.txt");
        vm.AttachmentAnnotation = "Comentario";
        vm.AddAttachmentAnnotationCommand.Execute(null);
        Assert.StartsWith(table, vm.Document.Text, StringComparison.Ordinal);
        Assert.Contains("> Comentario", vm.Document.Text);
        vm.Close();
    }

    [Fact]
    public void AnnotationDraft_IsRetainedPerAttachment()
    {
        File.WriteAllText(Path.Combine(_vault.VaultPath, "uno.txt"), "uno");
        File.WriteAllText(Path.Combine(_vault.VaultPath, "dos.txt"), "dos");
        var vm = Editor("![Uno](uno.txt)\n![Dos](dos.txt)");
        vm.OpenAttachmentTarget("uno.txt");
        vm.AttachmentAnnotation = "Borrador uno";
        vm.OpenAttachmentTarget("dos.txt");
        Assert.Equal(string.Empty, vm.AttachmentAnnotation);
        vm.AttachmentAnnotation = "Borrador dos";
        vm.CloseAttachmentsCommand.Execute(null);
        vm.OpenAttachmentTarget("uno.txt");
        Assert.Equal("Borrador uno", vm.AttachmentAnnotation);
        vm.Close();
    }

    [Fact]
    public void Rename_WithAttachment_UpdatesDocumentAndCanSaveAgain()
    {
        var vm = Editor("");
        var folder = NoteImageAttachments.FolderFor(vm.Note.FullPath)!;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "file.txt"), "contenido");
        vm.Document.Text = "![Documento](Proyecto.attachments/file.txt)";
        vm.OnTextChanged(vm.Document.Text);
        vm.Title = "Renombrada";
        Assert.Contains("Renombrada.attachments/file.txt", vm.Document.Text);
        vm.Document.Insert(vm.Document.TextLength, "\nOtra línea");
        vm.OnTextChanged(vm.Document.Text);
        vm.FlushPendingSave();
        Assert.Equal(vm.Document.Text, _vault.ReadContent(vm.Note));
        Assert.False(vm.IsDirty);
        vm.Close();
    }

    [Fact]
    public void Annotation_ExternalEdit_IsNotOverwritten()
    {
        File.WriteAllText(Path.Combine(_vault.VaultPath, "file.txt"), "contenido");
        var vm = Editor("![Documento](file.txt)");
        vm.OpenAttachmentTarget("file.txt");
        File.WriteAllText(vm.Note.FullPath, "Cambio externo");
        vm.AttachmentAnnotation = "Mi anotación";
        vm.AddAttachmentAnnotationCommand.Execute(null);
        vm.FlushPendingSave();
        Assert.Equal("Cambio externo", File.ReadAllText(vm.Note.FullPath));
        Assert.Contains("Mi anotación", vm.Document.Text);
        vm.Close();
    }
}
