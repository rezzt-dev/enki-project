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

/// <summary>El ciclo completo de un turno del asistente contra un CLI de Claude falso
/// (<see cref="FakeClaudeCli"/>): qué se envía, qué se guarda en el historial, cómo se retoma la
/// conversación, cómo se recupera una sesión perdida y cómo se presentan los fallos. Como el CLI falso es
/// un script de shell, estos tests no se ejecutan en Windows.</summary>
public sealed class AssistantViewModelTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly FakeClaudeCli _fake = new();
    private SettingsService _settings = null!;
    private VaultService _vault = null!;
    private AiChatService _chats = null!;
    private NotificationService _notifications = null!;

    public void Dispose()
    {
        _fake.Dispose();
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private async Task<AssistantViewModel> CreateAsync(NoteFile? activeNote = null)
    {
        _settings = new SettingsService(_storageDirectory);
        _settings.Settings.Ai.ClaudeCliPath = _fake.ExecutablePath;
        _settings.Settings.Ai.ConsentAcceptedUtc = DateTime.UtcNow;
        _settings.Settings.Ai.Enabled = true;
        _vault = new VaultService(_settings);
        _vault.InitializeVault(Path.Combine(_storageDirectory, "vault"), seedWelcomeNote: false);
        _chats = new AiChatService(_storageDirectory);
        _notifications = new NotificationService(_settings, Path.Combine(_storageDirectory, "notifications"));
        var vm = new AssistantViewModel(new ClaudeCliService(_settings, _storageDirectory), _chats, _settings, _vault,
            _notifications, activeNoteProvider: () => activeNote, dispatch: action => action());
        await vm.Cli.RefreshAsync();
        return vm;
    }

    private static async Task SendAsync(AssistantViewModel vm, string text)
    {
        vm.DraftText = text;
        await vm.SendCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task FirstTurn_StreamsReply_AndSavesTheConversation()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        Assert.True(vm.ShowWelcome);

        await SendAsync(vm, "Hola, ¿qué tal?");

        Assert.Equal(2, vm.Messages.Count);
        Assert.True(vm.Messages[0].IsUser);
        Assert.Equal("Hola mundo", vm.Messages[1].Text);
        Assert.False(vm.Messages[1].IsStreaming);
        Assert.True(vm.Messages[1].CanAct);
        Assert.Equal(string.Empty, vm.DraftText);
        Assert.False(vm.IsSending);
        Assert.False(vm.ShowWelcome);
        Assert.False(vm.HasError);

        var saved = Assert.Single(new AiChatService(_storageDirectory).Sessions);
        Assert.Equal("Hola, ¿qué tal?", saved.Title);
        Assert.Equal("sess-1", saved.CliSessionId);
        Assert.Equal([AiChatRole.User, AiChatRole.Assistant], saved.Messages.Select(m => m.Role));
        Assert.Equal(saved.Id, _settings.Settings.Ai.LastChatId);
        Assert.Single(vm.Sessions);
        Assert.True(vm.Sessions[0].IsActive);
    }

    [Fact]
    public async Task NextTurn_ResumesTheSameCliSession()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        await SendAsync(vm, "uno");

        await SendAsync(vm, "dos");

        Assert.DoesNotContain("--resume", _fake.Arguments(0));
        var second = _fake.Arguments(1);
        Assert.Equal("sess-1", second[Array.IndexOf(second, "--resume") + 1]);
        Assert.Equal("dos", _fake.Stdin(1));
        Assert.Equal(4, vm.Messages.Count);
    }

    [Fact]
    public async Task LostCliSession_IsRebuiltFromEnkiHistory_WithoutBotheringTheUser()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        await SendAsync(vm, "Mi color favorito es el verde");
        _fake.ReplyToResume(FakeClaudeCli.SessionNotFoundLines("sess-1"));
        _fake.Reply("sess-2", "Verde");

        await SendAsync(vm, "¿Cuál es mi color favorito?");

        Assert.False(vm.HasError);
        Assert.Equal("Verde", vm.Messages.Last().Text);
        Assert.Equal(3, _fake.TurnCount);
        var recovery = _fake.Stdin(2);
        Assert.Contains("<enki_previous_conversation>", recovery);
        Assert.Contains("Mi color favorito es el verde", recovery);
        Assert.Contains("Hola mundo", recovery);
        Assert.EndsWith("¿Cuál es mi color favorito?", recovery);
        Assert.DoesNotContain("--resume", _fake.Arguments(2));
        Assert.Equal("sess-2", _chats.Sessions.Single().CliSessionId);
    }

    [Fact]
    public async Task AttachedNote_AsksForConfirmation_ThenSendsItsContent()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        var note = _vault.CreateNote("Reunión", initialContent: "# Reunión\n\nDecidimos lanzar el viernes.");
        vm.StartChatAboutNote(note);
        Assert.Single(vm.PendingAttachments);

        vm.DraftText = "Resume la nota";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.True(vm.IsConfirmSendOpen);
        Assert.Equal(0, _fake.TurnCount);
        Assert.Contains(vm.ConfirmSendItems, item => item.Title == "Reunión");

        await vm.ConfirmSendCommand.ExecuteAsync(null);

        Assert.False(vm.IsConfirmSendOpen);
        var stdin = _fake.Stdin(0);
        Assert.Contains("Decidimos lanzar el viernes.", stdin);
        Assert.Contains("path=\"Reunión.md\"", stdin);
        Assert.DoesNotContain(_storageDirectory, stdin);
        Assert.EndsWith("Resume la nota", stdin);
        Assert.Empty(vm.PendingAttachments);
        var attachment = Assert.Single(vm.Messages[0].Attachments);
        Assert.Equal("Reunión", attachment.Title);
        Assert.False(attachment.WasTruncated);
    }

    [Fact]
    public async Task ConfirmationCanBeTurnedOff()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        _settings.Settings.Ai.ConfirmBeforeSendingContext = false;
        vm.StartChatAboutNote(_vault.CreateNote("Breve", initialContent: "texto"));

        await SendAsync(vm, "¿Qué dice?");

        Assert.False(vm.IsConfirmSendOpen);
        Assert.Equal(1, _fake.TurnCount);
    }

    [Fact]
    public async Task ActiveNote_CanBeAttachedOnce()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync(activeNote: null);
        var note = _vault.CreateNote("Abierta");
        vm = await CreateAsync(activeNote: note);
        vm.OnActivated();
        Assert.True(vm.CanAttachActiveNote);

        vm.AttachActiveNoteCommand.Execute(null);
        vm.AttachActiveNoteCommand.Execute(null);

        Assert.Single(vm.PendingAttachments);
        Assert.False(vm.CanAttachActiveNote);
    }

    [Fact]
    public async Task Failure_KeepsTheQuestion_ShowsTheError_AndRetrySucceeds()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        _fake.Output(
        [
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1792888200}}""",
            """{"type":"result","subtype":"success","is_error":true,"result":"Claude AI usage limit reached","session_id":"s"}"""
        ]);
        _fake.ExitCode = 1;

        await SendAsync(vm, "¿Sigues ahí?");

        Assert.True(vm.HasError);
        Assert.True(vm.CanRetry);
        var userOnly = Assert.Single(vm.Messages);
        Assert.True(userOnly.IsUser);
        Assert.Contains(_notifications.Notifications, n => n.Severity == NotificationSeverity.Warning);

        _fake.ExitCode = 0;
        _fake.Reply("sess-9", "Sí");
        await vm.RetryCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.False(vm.CanRetry);
        Assert.Equal("Sí", vm.Messages.Last().Text);
        Assert.Equal("¿Sigues ahí?", _fake.Stdin(1));
    }

    [Fact]
    public async Task RepeatedFailuresOfTheSameKind_NotifyOnlyOnce()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        _fake.Output(["""{"type":"result","subtype":"success","is_error":true,"result":"Overloaded","api_error_status":529}"""]);
        _fake.ExitCode = 1;

        await SendAsync(vm, "uno");
        await vm.RetryCommand.ExecuteAsync(null);

        Assert.Single(_notifications.Notifications, n => n.Severity == NotificationSeverity.Error);
    }

    [Fact]
    public async Task Stop_CancelsTheTurn_AndDropsTheEmptyReply()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        _fake.Hangs = true;
        vm.DraftText = "Escribe algo largo";

        var turn = vm.SendCommand.ExecuteAsync(null);
        for (var i = 0; i < 100 && _fake.TurnCount == 0; i++) await Task.Delay(50);
        Assert.True(vm.IsSending);
        vm.StopCommand.Execute(null);
        await turn;

        Assert.False(vm.IsSending);
        Assert.False(vm.HasError);
        Assert.True(Assert.Single(vm.Messages).IsUser);
    }

    [Fact]
    public async Task Skill_IsSentAsSystemPrompt_AndCannotChangeAfterFirstMessage()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        vm.StartChatWithSkill(new SkillFile
        {
            Name = "Redactor",
            Content = "---\nname: Redactor\n---\nEscribe siempre en frases cortas."
        });
        Assert.True(vm.HasActiveSkill);
        Assert.True(vm.CanPickSkill);

        await SendAsync(vm, "Presenta Enki");

        var system = _fake.SystemPrompt(0);
        Assert.Contains("## Skill: Redactor", system);
        Assert.Contains("Escribe siempre en frases cortas.", system);
        Assert.DoesNotContain("name: Redactor", system);
        Assert.False(vm.CanPickSkill);
        Assert.Equal("Redactor", _chats.Sessions.Single().SkillName);
    }

    [Fact]
    public async Task ModelChoice_IsSentAndRememberedPerChat()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        vm.SelectedModel = vm.ModelOptions.Single(m => m.Alias == "haiku");

        await SendAsync(vm, "hola");

        var arguments = _fake.Arguments(0);
        Assert.Equal("haiku", arguments[Array.IndexOf(arguments, "--model") + 1]);
        Assert.Equal("haiku", _chats.Sessions.Single().Model);
    }

    [Fact]
    public async Task History_SelectNewChatAndDelete()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        await SendAsync(vm, "primera");
        vm.NewChatCommand.Execute(null);
        Assert.Empty(vm.Messages);
        Assert.True(vm.IsDraft);
        await SendAsync(vm, "segunda");
        Assert.Equal(2, vm.Sessions.Count);

        vm.SelectSessionCommand.Execute(vm.Sessions.Single(s => s.Title == "primera"));
        Assert.Equal("primera", vm.ChatTitle);
        Assert.Equal(2, vm.Messages.Count);

        vm.SessionSearchText = "segunda";
        Assert.Single(vm.Sessions);
        vm.SessionSearchText = string.Empty;

        vm.RequestDeleteSessionCommand.Execute(vm.Sessions.Single(s => s.Title == "primera"));
        vm.ConfirmDeleteSessionCommand.Execute(null);

        Assert.True(vm.IsDraft);
        Assert.Empty(vm.Messages);
        Assert.Equal(["segunda"], _chats.Sessions.Select(s => s.Title));
    }

    [Fact]
    public async Task Rename_UpdatesTitleAndPersists()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        await SendAsync(vm, "título automático");

        vm.BeginRenameCommand.Execute(null);
        vm.TitleDraft = "  Plan de lanzamiento  ";
        vm.CommitRenameCommand.Execute(null);

        Assert.Equal("Plan de lanzamiento", vm.ChatTitle);
        Assert.Equal("Plan de lanzamiento", new AiChatService(_storageDirectory).Sessions.Single().Title);
    }

    [Fact]
    public async Task WithoutCli_SetupPanelIsShown_AndNothingCanBeSent()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        _settings.Settings.Ai.ClaudeCliPath = Path.Combine(_storageDirectory, "no-existe");
        await vm.Cli.RefreshAsync();

        vm.DraftText = "hola";

        Assert.True(vm.Cli.IsNotInstalled);
        Assert.True(vm.ShowSetupPanel);
        Assert.False(vm.CanSend);
        Assert.False(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task RestoresLastChatOnFirstActivation()
    {
        if (OperatingSystem.IsWindows()) return;
        var first = await CreateAsync();
        await SendAsync(first, "recuérdame");

        var vm = await CreateAsync();
        vm.OnActivated();

        Assert.Equal("recuérdame", vm.ChatTitle);
        Assert.Equal(2, vm.Messages.Count);
    }
}
