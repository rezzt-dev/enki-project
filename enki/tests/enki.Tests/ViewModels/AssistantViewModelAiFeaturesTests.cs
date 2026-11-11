using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using enki.Localization;
using enki.Models;
using enki.Services;
using enki.Tests.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Exportar la conversación (AIA-09), el destino de las revisiones que se quedaron a medias (AIA-07) y el aviso
/// de qué proveedor recibe una imagen adjunta (AIA-15), sobre el CLI de pruebas y un vault desechable.</summary>
[Collection("LocalizationMutatingTests")]
public sealed class AssistantViewModelAiFeaturesTests : IDisposable
{
    private readonly string _storageDirectory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClaudeCli _fake = new();
    private readonly SettingsService _settings;
    private readonly VaultService _vault;
    private AiChatService _chats = null!;
    private NoteFile? _opened;

    public AssistantViewModelAiFeaturesTests()
    {
        _settings = new SettingsService(_storageDirectory);
        _settings.Settings.Ai.ClaudeCliPath = _fake.ExecutablePath;
        _settings.Settings.Ai.ConsentAcceptedUtc = DateTime.UtcNow;
        _settings.Settings.Ai.Enabled = true;
        _settings.Settings.Ai.ConfirmBeforeSendingContext = false;
        _vault = new VaultService(_settings);
        _vault.InitializeVault(Path.Combine(_storageDirectory, "vault"), seedWelcomeNote: false);
    }

    public void Dispose()
    {
        _fake.Dispose();
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private async Task<AssistantViewModel> CreateAsync(Action<AiChatService>? seed = null)
    {
        _chats = new AiChatService(_storageDirectory);
        seed?.Invoke(_chats);
        var vm = new AssistantViewModel(new ClaudeCliService(_settings, _storageDirectory), _chats, _settings, _vault,
            new NotificationService(_settings, Path.Combine(_storageDirectory, "notifications")),
            dispatch: action => action(), vaultIdentity: _ => "vault-uno", openNote: note => _opened = note);
        await vm.Cli.RefreshAsync();
        return vm;
    }

    private static AiChatSession SeededChat()
    {
        var session = new AiChatSession { Title = "Plan", VaultId = "vault-uno" };
        session.Messages.Add(new AiChatMessage { Role = AiChatRole.User, Text = "Prepara el plan" });
        session.Messages.Add(new AiChatMessage
        {
            Role = AiChatRole.Assistant,
            Text = "Hecho.",
            Activities = [new AiToolActivity { Tool = "edit_note", Target = "Plan", State = AiToolActivityState.Pending }]
        });
        return session;
    }

    [Fact]
    public async Task ExportConversation_CreatesANoteWithTheWholeChat_AndOpensIt()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync(chats => chats.Save(SeededChat()));
        Assert.False(vm.CanExportConversation);
        vm.SelectSessionCommand.Execute(vm.Sessions.Single());
        Assert.True(vm.CanExportConversation);

        vm.ExportConversationCommand.Execute(null);

        var note = Assert.Single(_vault.GetAllNotesRecursive(_vault.VaultPath));
        Assert.Equal(note.FullPath, _opened?.FullPath);
        var content = File.ReadAllText(note.FullPath);
        Assert.Contains("Prepara el plan", content);
        Assert.Contains("Hecho.", content);
        Assert.Equal(AiChatExporter.SourceValue, NotePropertyHelper.Extract(content)["source"]);
    }

    [Fact]
    public async Task ExportConversation_InASessionChat_SavesInsideTheSession()
    {
        if (OperatingSystem.IsWindows()) return;
        var sessions = new SessionService(_vault);
        var session = sessions.Create("Proyecto");
        var chat = SeededChat();
        chat.SessionId = session.Id;
        chat.SessionName = session.Name;
        var vm = new AssistantViewModel(new ClaudeCliService(_settings, _storageDirectory), _chats = new AiChatService(_storageDirectory),
            _settings, _vault, new NotificationService(_settings, Path.Combine(_storageDirectory, "notifications")), sessionService: sessions,
            dispatch: action => action(), vaultIdentity: _ => "vault-uno", openNote: note => _opened = note);
        _chats.Save(chat);
        await vm.Cli.RefreshAsync();
        vm.SelectSessionCommand.Execute(vm.Sessions.Single());

        vm.ExportConversationCommand.Execute(null);

        Assert.NotNull(_opened);
        Assert.True(VaultService.IsWithinFolder(_opened!.FullPath, session.RootPath));
    }

    [Fact]
    public async Task PendingActionsWithoutALiveReview_AreMarkedDiscardedOnOpen()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync(chats => chats.Save(SeededChat()));

        vm.SelectSessionCommand.Execute(vm.Sessions.Single());

        var activity = vm.Messages.Last().Activities.Single();
        Assert.True(activity.IsDiscarded);
        Assert.Equal(LocalizationService.Instance["Assistant_Review_State_Discarded"], activity.RevertNote);
        Assert.Equal(AiToolActivityState.Discarded,
            new AiChatService(_storageDirectory).Sessions.Single().Messages.Last().Activities.Single().State);
    }

    [Fact]
    public async Task ImageAttachment_TellsWhatTheProviderWillReceive()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        Assert.False(vm.HasAttachmentImageNotice);
        var image = Path.Combine(_vault.VaultPath, "captura.png");
        File.WriteAllBytes(image, [0x89, 0x50, 0x4E, 0x47]);

        vm.PendingAttachments.Add(new AiAttachmentChipViewModel(AiFileReader.Describe(image), "Vault"));

        // Sin herramientas del vault el modelo no puede abrir la imagen: se dice antes de enviar nada.
        Assert.True(vm.HasAttachmentImageNotice);
        Assert.True(vm.AttachmentImageNoticeIsWarning);
        Assert.Equal(LocalizationService.Instance["Assistant_Attachment_ImageNotice_TextOnly"], vm.AttachmentImageNotice);

        vm.PendingAttachments.Clear();
        Assert.False(vm.HasAttachmentImageNotice);
    }
}
