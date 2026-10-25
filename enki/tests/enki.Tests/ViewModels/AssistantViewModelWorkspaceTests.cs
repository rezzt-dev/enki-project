using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using enki.Tests.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Lo que rodea a la conversación del asistente: historial separado por vault y asociación de los
/// chats antiguos, borradores por chat y por ámbito, adjuntos de archivos de texto, "Añadir a la nota activa",
/// el indicador de actividad y el permiso de consulta. Contra el CLI falso, así que no se ejecutan en Windows.</summary>
public sealed class AssistantViewModelWorkspaceTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly FakeClaudeCli _fake = new();
    private SettingsService _settings = null!;
    private VaultService _vault = null!;
    private AiChatService _chats = null!;
    private string _vaultId = "vault-uno";

    public void Dispose()
    {
        _fake.Dispose();
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private async Task<AssistantViewModel> CreateAsync(NoteFile? activeNote = null, Func<bool>? isVisible = null,
        Func<string, string, NoteDiskStamp, NoteFile>? appendToNote = null, EnkiMcpServer? vaultTools = null,
        Action<AiChatService>? seed = null)
    {
        _settings ??= new SettingsService(_storageDirectory);
        _settings.Settings.Ai.ClaudeCliPath = _fake.ExecutablePath;
        _settings.Settings.Ai.ConsentAcceptedUtc = DateTime.UtcNow;
        _settings.Settings.Ai.Enabled = true;
        _settings.Settings.Ai.ConfirmBeforeSendingContext = false;
        if (_vault is null)
        {
            _vault = new VaultService(_settings);
            _vault.InitializeVault(Path.Combine(_storageDirectory, "vault"), seedWelcomeNote: false);
        }
        _chats = new AiChatService(_storageDirectory);
        seed?.Invoke(_chats);
        var vm = new AssistantViewModel(new ClaudeCliService(_settings, _storageDirectory), _chats, _settings, _vault,
            new NotificationService(_settings, Path.Combine(_storageDirectory, "notifications")),
            activeNoteProvider: () => activeNote, dispatch: action => action(), isVisible: isVisible,
            vaultIdentity: _ => _vaultId, appendToNote: appendToNote, vaultTools: vaultTools);
        await vm.Cli.RefreshAsync();
        return vm;
    }

    private static async Task SendAsync(AssistantViewModel vm, string text)
    {
        vm.DraftText = text;
        await vm.SendCommand.ExecuteAsync(null);
    }

    // ===================== Historial por vault =====================

    [Fact]
    public async Task DefaultReadAccess_AppliesLive_AndChatRevocationSurvivesReload()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        Assert.False(vm.ReadAccessGranted);
        _settings.Settings.Ai.AllowReadAccessByDefault = true;
        _settings.Save();
        Assert.True(vm.ReadAccessGranted);
        await SendAsync(vm, "Hola");
        Assert.True(vm.ReadAccessGranted);
        _settings.Settings.Ai.AllowReadAccessByDefault = false;
        _settings.Save();
        Assert.False(vm.ReadAccessGranted);
        _settings.Settings.Ai.AllowReadAccessByDefault = true;
        _settings.Save();
        vm.RevokeReadAccessCommand.Execute(null);
        Assert.False(vm.ReadAccessGranted);
        Assert.True(new AiChatService(_storageDirectory).Sessions.Single().ReadAccessRevoked);
        var reloaded = await CreateAsync();
        reloaded.SelectSessionCommand.Execute(reloaded.Sessions.Single());
        Assert.False(reloaded.ReadAccessGranted);
        vm.NewChatCommand.Execute(null);
        Assert.True(vm.ReadAccessGranted);
    }

    [Fact]
    public async Task NewChats_BelongToTheOpenVault_AndOtherVaultsChatsAreHidden()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync(seed: chats =>
        {
            chats.Save(new AiChatSession { Title = "De otro vault", VaultId = "vault-dos" });
            chats.Save(new AiChatSession { Title = "Antiguo" });
        });

        await SendAsync(vm, "Hola");

        var saved = _chats.Sessions.Single(s => s.Title == "Hola");
        Assert.Equal("vault-uno", saved.VaultId);
        Assert.DoesNotContain(vm.Sessions, item => item.Title == "De otro vault");
        Assert.Contains(vm.Sessions, item => item.Title == "Antiguo" && item.IsUnassigned);
        Assert.True(vm.ChatGroups.Last().IsUnassigned);
        Assert.False(vm.ChatGroups.First().IsUnassigned);
    }

    [Fact]
    public async Task UnassignedChat_CanBeReadButNotContinued_UntilLinkedToTheVault()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync(seed: chats =>
        {
            var old = new AiChatSession { Title = "Antiguo" };
            old.Messages.Add(new AiChatMessage { Role = AiChatRole.User, Text = "Pregunta" });
            old.Messages.Add(new AiChatMessage { Role = AiChatRole.Assistant, Text = "Respuesta" });
            chats.Save(old);
        });
        vm.SelectSessionCommand.Execute(vm.Sessions.Single());
        vm.DraftText = "Seguimos";

        Assert.True(vm.IsUnassignedChat);
        Assert.Equal(2, vm.Messages.Count);
        Assert.False(vm.CanSend);

        vm.AssignActiveChatToVaultCommand.Execute(null);

        Assert.False(vm.IsUnassignedChat);
        Assert.True(vm.CanSend);
        Assert.Equal("vault-uno", new AiChatService(_storageDirectory).Sessions.Single().VaultId);
    }

    [Fact]
    public async Task ChangingVault_ClosesTheChat_AndComingBackRestoresItsLastChat()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        await SendAsync(vm, "En el primero");

        _vaultId = "vault-dos";
        vm.CloseForVaultChange();
        vm.ReloadForVault();
        Assert.False(vm.HasMessages);
        Assert.Empty(vm.Sessions);

        _vaultId = "vault-uno";
        vm.CloseForVaultChange();
        vm.ReloadForVault();
        vm.OnActivated();
        Assert.Equal("En el primero", vm.ChatTitle);
    }

    // ===================== Borradores =====================

    [Fact]
    public async Task Drafts_AreKeptPerChat_AndForTheNewChat_AcrossSwitchesAndRestarts()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        await SendAsync(vm, "Chat A");
        vm.DraftText = "borrador de A";

        vm.NewChatCommand.Execute(null);
        Assert.Equal(string.Empty, vm.DraftText);
        vm.DraftText = "idea para un chat nuevo";

        vm.SelectSessionCommand.Execute(vm.Sessions.Single());
        Assert.Equal("borrador de A", vm.DraftText);

        vm.NewChatCommand.Execute(null);
        Assert.Equal("idea para un chat nuevo", vm.DraftText);

        // Al cerrar Enki se guarda lo que hubiera; al volver se recupera.
        vm.CancelAll();
        var reopened = await CreateAsync();
        Assert.Equal("idea para un chat nuevo", reopened.DraftText);
        reopened.SelectSessionCommand.Execute(reopened.Sessions.Single());
        Assert.Equal("borrador de A", reopened.DraftText);
    }

    [Fact]
    public async Task RestoredDraft_DropsAttachmentsThatAreGone_AndSaysSo()
    {
        if (OperatingSystem.IsWindows()) return;
        _settings = new SettingsService(_storageDirectory);
        _vault = new VaultService(_settings);
        _vault.InitializeVault(Path.Combine(_storageDirectory, "vault"), seedWelcomeNote: false);
        var note = _vault.CreateNote("Reunión", initialContent: "# Reunión\n");
        var vm = await CreateAsync(activeNote: note);
        vm.DraftText = "Resume esto";
        vm.AttachActiveNoteCommand.Execute(null);
        Assert.Single(vm.PendingAttachments);
        vm.SaveDraft();

        File.Delete(note.FullPath);
        var reopened = await CreateAsync();

        Assert.Equal("Resume esto", reopened.DraftText);
        Assert.Empty(reopened.PendingAttachments);
        Assert.Equal(1, reopened.DraftMissingAttachments);
        Assert.True(reopened.HasDraftNotice);
    }

    // ===================== Adjuntos de archivos =====================

    [Fact]
    public async Task TextFiles_AreSentAsFileBlocks_AndBinaryFilesCannotBePicked()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        var json = Path.Combine(_vault.VaultPath, "datos.json");
        File.WriteAllText(json, "{\"total\": 42}");
        var image = Path.Combine(_vault.VaultPath, "foto.png");
        File.WriteAllBytes(image, [0x89, 0x50, 0x4E, 0x47]);

        var imageItem = new AiAttachmentPickerItemViewModel(AiTextFiles.Describe(image), "Vault");
        Assert.False(imageItem.IsEnabled);
        vm.PickNoteCommand.Execute(imageItem);
        Assert.Empty(vm.PendingAttachments);

        vm.PickNoteCommand.Execute(new AiAttachmentPickerItemViewModel(AiTextFiles.Describe(json), "Vault"));
        Assert.True(Assert.Single(vm.PendingAttachments).IsFile);
        await SendAsync(vm, "¿Cuál es el total?");

        Assert.Contains("<file title=\"datos.json\" path=\"datos.json\">", _fake.Stdin(0), StringComparison.Ordinal);
        Assert.Contains("\"total\": 42", _fake.Stdin(0), StringComparison.Ordinal);
        Assert.Equal(AiAttachmentKind.File, vm.Messages[0].Model.Attachments.Single().Kind);
    }

    [Fact]
    public async Task AttachedFile_ThatIsNoLongerText_StopsTheSend_AndSaysWhy()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        var path = Path.Combine(_vault.VaultPath, "datos.json");
        File.WriteAllText(path, "{}");
        vm.PickNoteCommand.Execute(new AiAttachmentPickerItemViewModel(AiTextFiles.Describe(path), "Vault"));
        File.WriteAllBytes(path, [0x7B, 0x00, 0x7D]);

        await SendAsync(vm, "Lee esto");

        Assert.Equal(0, _fake.TurnCount);
        Assert.True(vm.PendingAttachments.Single().HasProblem);
        Assert.Equal("Lee esto", vm.DraftText);
    }

    // ===================== Añadir a la nota activa =====================

    [Fact]
    public async Task AppendToActiveNote_PreviewsTheAddedLines_AndWritesAgainstWhatWasPreviewed()
    {
        if (OperatingSystem.IsWindows()) return;
        _settings = new SettingsService(_storageDirectory);
        _vault = new VaultService(_settings);
        _vault.InitializeVault(Path.Combine(_storageDirectory, "vault"), seedWelcomeNote: false);
        var note = _vault.CreateNote("Diario", initialContent: "# Diario\n\nHoy.\n");
        (string Path, string Text, NoteDiskStamp Baseline)? written = null;
        var vm = await CreateAsync(activeNote: note, appendToNote: (path, text, baseline) =>
        {
            written = (path, text, baseline);
            return note;
        });
        await SendAsync(vm, "Dame una frase");

        vm.RequestAppendToNoteCommand.Execute(vm.Messages.Last());

        Assert.True(vm.IsAppendPreviewOpen);
        Assert.Equal("Diario", vm.AppendPreviewTitle);
        Assert.Contains(vm.AppendPreviewLines, line => line.Kind == DiffLineKind.Added && line.Text == "Hola mundo");
        Assert.DoesNotContain(vm.AppendPreviewLines, line => line.Kind == DiffLineKind.Removed);

        vm.ConfirmAppendToNoteCommand.Execute(null);

        Assert.False(vm.IsAppendPreviewOpen);
        Assert.Equal(note.FullPath, written!.Value.Path);
        Assert.Equal("Hola mundo", written.Value.Text);
        Assert.Equal(_vault.ReadFromDisk(note.FullPath)!.Value.Stamp.ContentHash, written.Value.Baseline.ContentHash);
    }

    // ===================== Indicador de actividad =====================

    [Fact]
    public async Task ReplyFinishedOutOfView_StaysReady_UntilTheConversationIsSeen()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync(isVisible: () => false);
        Assert.Equal(AssistantActivityState.Idle, vm.ActivityState);

        await SendAsync(vm, "Hola");

        Assert.Equal(AssistantActivityState.ResponseReady, vm.ActivityState);
        Assert.True(vm.HasActivity);
        vm.MarkConversationSeen();
        Assert.Equal(AssistantActivityState.Idle, vm.ActivityState);
    }

    // ===================== Permiso de consulta =====================

    [Fact]
    public async Task ReadAccess_IsGrantedPerChat_AndWideningToSessionsNeedsANewGrant()
    {
        if (OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(_fake.Root, "help.txt"), FakeClaudeCli.HelpText
            + "  --mcp-config <configs...>\n  --allowedTools <tools...>\n  --setting-sources <sources>\n  --permission-mode <mode>\n");
        _settings = new SettingsService(_storageDirectory);
        _vault = new VaultService(_settings);
        _vault.InitializeVault(Path.Combine(_storageDirectory, "vault"), seedWelcomeNote: false);
        var sessions = new SessionService(_vault);
        var toolbox = new AiVaultToolbox(_vault, sessions, new KanbanService(_storageDirectory), new PolarTaskService(_storageDirectory),
            new PolarAuthService(_settings, baseUrl: "https://test.example", anonKey: "test"), new ReminderService(_storageDirectory), _settings);
        using var server = new EnkiMcpServer(toolbox);
        var vm = await CreateAsync(vaultTools: server);
        Assert.True(vm.VaultToolsAvailable);
        Assert.False(vm.ReadAccessGranted);

        vm.AllowReadAccessCommand.Execute(null);
        Assert.True(vm.ReadAccessGranted);

        // El permiso de la raíz no cubre en silencio el acceso a las sesiones.
        vm.FullVaultAccess = true;
        Assert.False(vm.ReadAccessGranted);
        vm.AllowReadAccessCommand.Execute(null);
        Assert.True(vm.ReadAccessGranted);

        vm.RevokeReadAccessCommand.Execute(null);
        Assert.False(vm.ReadAccessGranted);
        vm.AllowReadAccessCommand.Execute(null);
        await SendAsync(vm, "Hola");

        var saved = new AiChatService(_storageDirectory).Sessions.Single();
        Assert.True(saved.ReadAccess!.IncludesSessions);
        // Otro chat no hereda el permiso.
        vm.NewChatCommand.Execute(null);
        Assert.False(vm.ReadAccessGranted);
    }
}
