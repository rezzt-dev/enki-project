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

/// <summary>Personalidad del asistente: el nombre en la interfaz y el contexto por capas (general, sesión y chat)
/// que viaja en cada turno, editable entre turnos. Contra el CLI falso, así que no se ejecutan en Windows.</summary>
public sealed class AssistantViewModelPersonaTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly FakeClaudeCli _fake = new();
    private SettingsService _settings = null!;
    private VaultService _vault = null!;
    private AiChatService _chats = null!;
    private SessionService _sessions = null!;

    public void Dispose()
    {
        _fake.Dispose();
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private async Task<AssistantViewModel> CreateAsync()
    {
        _settings = new SettingsService(_storageDirectory);
        _settings.Settings.Ai.ClaudeCliPath = _fake.ExecutablePath;
        _settings.Settings.Ai.ConsentAcceptedUtc = DateTime.UtcNow;
        _settings.Settings.Ai.Enabled = true;
        _vault = new VaultService(_settings);
        _vault.InitializeVault(Path.Combine(_storageDirectory, "vault"), seedWelcomeNote: false);
        _sessions = new SessionService(_vault);
        _chats = new AiChatService(_storageDirectory);
        var vm = new AssistantViewModel(new ClaudeCliService(_settings, _storageDirectory), _chats, _settings, _vault,
            new NotificationService(_settings, Path.Combine(_storageDirectory, "notifications")),
            sessionService: _sessions, dispatch: action => action(), vaultIdentity: _ => "vault-uno");
        await vm.Cli.RefreshAsync();
        return vm;
    }

    private static async Task SendAsync(AssistantViewModel vm, string text)
    {
        vm.DraftText = text;
        await vm.SendCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task WithoutPersona_TheTurnIsSentAsBefore_AndTheUiUsesTheGenericName()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();

        await SendAsync(vm, "Hola");

        Assert.Equal("Hola", _fake.Stdin(0));
        Assert.False(vm.HasCustomName);
        Assert.Equal(LocalizationService.Instance["Assistant_Message_Author"], vm.AssistantName);
        Assert.Equal(LocalizationService.Instance["Assistant_Welcome_Title"], vm.WelcomeTitle);
        Assert.False(vm.HasPersonaContext);
    }

    [Fact]
    public async Task NameAndGeneralContext_ReachTheModel_AndRenameTheUiLive()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        var notified = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AssistantViewModel.AssistantName)) notified++;
        };

        _chats.Persona.SetIdentity("Ada", "Uso Enki como segundo cerebro.");

        Assert.True(notified > 0);
        Assert.Equal("Ada", vm.AssistantName);
        Assert.Contains("Ada", vm.WelcomeTitle);
        Assert.Contains("Ada", vm.ComposerPlaceholder);
        Assert.True(vm.HasPersonaContext);

        await SendAsync(vm, "Hola");

        var stdin = _fake.Stdin(0);
        Assert.StartsWith("<enki_persona>\nYour name: Ada\n", stdin);
        Assert.Contains("Uso Enki como segundo cerebro.", stdin);
        Assert.EndsWith("Hola", stdin);
        Assert.Contains("<enki_persona>", _fake.SystemPrompt(0));
        // El historial guarda lo que escribió el usuario, no el bloque.
        Assert.Equal("Hola", _chats.Sessions.Single().Messages[0].Text);
    }

    [Fact]
    public async Task ChatContext_WrittenBeforeTheFirstMessage_MovesIntoTheChat_AndCanChangeBetweenTurns()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        vm.TogglePersonaEditorCommand.Execute(null);
        Assert.True(vm.IsPersonaEditorOpen);
        Assert.False(vm.SavePersonaCommand.CanExecute(null));

        vm.PersonaChatContextDraft = "  Responde como un tutor paciente.  ";
        Assert.True(vm.HasPersonaChanges);
        vm.SavePersonaCommand.Execute(null);
        Assert.False(vm.IsPersonaEditorOpen);
        Assert.True(vm.HasPersonaContext);

        await SendAsync(vm, "uno");
        Assert.Contains("<chat_context>\nResponde como un tutor paciente.\n</chat_context>", _fake.Stdin(0));
        var saved = new AiChatService(_storageDirectory).Sessions.Single();
        Assert.Equal("Responde como un tutor paciente.", saved.Context);

        vm.TogglePersonaEditorCommand.Execute(null);
        Assert.Equal("Responde como un tutor paciente.", vm.PersonaChatContextDraft);
        vm.PersonaChatContextDraft = "Ahora sé breve.";
        vm.SavePersonaCommand.Execute(null);
        await SendAsync(vm, "dos");
        Assert.Contains("Ahora sé breve.", _fake.Stdin(1));
        Assert.DoesNotContain("tutor paciente", _fake.Stdin(1));

        // Un chat nuevo no hereda el contexto del anterior.
        vm.NewChatCommand.Execute(null);
        Assert.False(vm.HasPersonaContext);
        vm.TogglePersonaEditorCommand.Execute(null);
        Assert.Equal(string.Empty, vm.PersonaChatContextDraft);
    }

    [Fact]
    public async Task ChatContextOfANewChat_SurvivesAsPartOfTheDraft()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        vm.TogglePersonaEditorCommand.Execute(null);
        vm.PersonaChatContextDraft = "Contexto pendiente";
        vm.SavePersonaCommand.Execute(null);

        var reopened = new AssistantViewModel(new ClaudeCliService(_settings, _storageDirectory), new AiChatService(_storageDirectory),
            _settings, _vault, new NotificationService(_settings, Path.Combine(_storageDirectory, "notifications")),
            sessionService: _sessions, dispatch: action => action(), vaultIdentity: _ => "vault-uno");

        reopened.TogglePersonaEditorCommand.Execute(null);
        Assert.Equal("Contexto pendiente", reopened.PersonaChatContextDraft);
    }

    [Fact]
    public async Task SessionContext_IsSharedByTheSessionChats_AndNotByVaultChats()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        var session = _sessions.Create("Tesis");
        vm.StartChatInSession(session);
        Assert.True(vm.CanEditSessionContext);
        Assert.Contains("Tesis", vm.PersonaSessionTitle);

        vm.TogglePersonaEditorCommand.Execute(null);
        vm.PersonaSessionContextDraft = "Tesis sobre ecología de ríos.";
        vm.SavePersonaCommand.Execute(null);
        await SendAsync(vm, "uno");

        Assert.Contains("<session_context session=\"Tesis\">\nTesis sobre ecología de ríos.", _fake.Stdin(0));
        Assert.Equal("Tesis sobre ecología de ríos.", new AiPersonaService(_storageDirectory).GetSessionContext("vault-uno", session.Id));

        // Otro chat de la misma sesión lo recibe sin volver a escribirlo.
        vm.NewChatCommand.Execute(null);
        vm.StartChatInSession(session);
        await SendAsync(vm, "dos");
        Assert.Contains("Tesis sobre ecología de ríos.", _fake.Stdin(1));

        // Un chat del vault no.
        vm.NewChatCommand.Execute(null);
        Assert.False(vm.CanEditSessionContext);
        await SendAsync(vm, "tres");
        Assert.Equal("tres", _fake.Stdin(2));
    }

    [Fact]
    public async Task DiscardingTheEditor_KeepsWhatWasSaved()
    {
        if (OperatingSystem.IsWindows()) return;
        var vm = await CreateAsync();
        vm.TogglePersonaEditorCommand.Execute(null);
        vm.PersonaChatContextDraft = "Algo que no se guarda";

        vm.DiscardPersonaChangesCommand.Execute(null);

        Assert.False(vm.IsPersonaEditorOpen);
        Assert.False(vm.HasPersonaContext);
        vm.TogglePersonaEditorCommand.Execute(null);
        Assert.Equal(string.Empty, vm.PersonaChatContextDraft);
        Assert.False(vm.HasPersonaChanges);
    }
}
