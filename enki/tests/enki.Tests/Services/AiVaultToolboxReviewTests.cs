using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Revisión de cambios por lotes (AIA-07): con la cola del turno, los cambios de contenido de notas se
/// preparan sin escribirse, se aplican juntos al aprobarlos y nunca pisan lo que cambió mientras tanto. Sobre
/// servicios reales y un vault desechable.</summary>
public sealed class AiVaultToolboxReviewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly VaultService _vault;
    private readonly AiVaultToolbox _tools;
    private readonly AiChangeReview _review = new();
    private readonly List<AiToolActivity> _turn = [];
    private readonly AiToolContext _context;
    private int _vaultChanged;

    public AiVaultToolboxReviewTests()
    {
        _settings = new SettingsService(_directory);
        _vault = new VaultService(_settings);
        _vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        _tools = new AiVaultToolbox(_vault, new SessionService(_vault), new KanbanService(_directory), new PolarTaskService(_directory),
            new PolarAuthService(_settings, baseUrl: "https://test.example", anonKey: "test"), new ReminderService(_directory), _settings,
            host: new AiVaultHost { VaultChanged = () => _vaultChanged++ });
        _context = new AiToolContext
        {
            Scope = AiAccessScope.Vault,
            Review = _review,
            ActivityReported = activity =>
            {
                if (!_turn.Contains(activity)) _turn.Add(activity);
            }
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private async Task<JsonElement> Call(string name, object args)
    {
        var result = await _tools.ExecuteAsync(name, JsonSerializer.SerializeToElement(args), _context);
        Assert.False(result.IsError, result.Text);
        return JsonSerializer.Deserialize<JsonElement>(result.Text);
    }

    private AiReviewReport ApproveAll() => _tools.ResolveReview(_review, _review.Changes.ToHashSet());

    [Fact]
    public async Task Edit_IsQueued_WithoutTouchingTheNote_AndToldToTheModel()
    {
        var note = _vault.CreateNote("Plan", initialContent: "# Plan\n\nUno\n");

        var result = await Call("append_to_note", new { path = "Plan", text = "Dos" });

        Assert.Equal("queued_for_review", result.GetProperty("status").GetString());
        Assert.Equal("# Plan\n\nUno\n", File.ReadAllText(note.FullPath));
        Assert.Equal(AiToolActivityState.Pending, _turn.Single().State);
        Assert.Null(_turn.Single().Undo);
        Assert.Equal(0, _vaultChanged);
        var change = Assert.Single(_review.Changes);
        Assert.Equal(AiReviewChangeKind.Edit, change.Kind);
        Assert.Equal("# Plan\n\nUno\n\nDos\n", change.ProposedContent);
    }

    [Fact]
    public async Task ReadNote_ShowsQueuedChanges_AndLaterEditsBuildOnThem()
    {
        var note = _vault.CreateNote("Plan", initialContent: "# Plan\n\nUno\n");
        await Call("append_to_note", new { path = "Plan", text = "Dos" });

        var read = await Call("read_note", new { path = "Plan" });
        Assert.True(read.GetProperty("pending_review").GetBoolean());
        Assert.Contains("Dos", read.GetProperty("content").GetString());

        await Call("edit_note", new { path = "Plan", edits = new[] { new { find = "Dos", replace = "Tres" } } });
        Assert.Equal(2, _review.Changes.Count);
        Assert.Same(_review.Changes[0], _review.Changes[1].DependsOn);

        var report = ApproveAll();

        Assert.Equal(new AiReviewReport(2, 0, 0), report);
        Assert.Equal("# Plan\n\nUno\n\nTres\n", File.ReadAllText(note.FullPath));
        Assert.All(_turn, a => Assert.Equal(AiToolActivityState.Succeeded, a.State));
        Assert.All(_turn.Where(a => a.Tool != "read_note"), a => Assert.Equal(AiUndoKind.NoteEdited, a.Undo!.Kind));
        Assert.Equal(1, _vaultChanged);
    }

    [Fact]
    public async Task AppliedChanges_CanBeRevertedLikeAnyOtherAction()
    {
        var note = _vault.CreateNote("Plan", initialContent: "# Plan\n\nUno\n");
        await Call("append_to_note", new { path = "Plan", text = "Dos" });
        await Call("create_note", new { title = "Nueva", content = "Hola" });
        ApproveAll();
        Assert.True(File.Exists(Path.Combine(_vault.VaultPath, "Nueva.md")));

        var report = _tools.RevertActivities(_turn);

        Assert.Equal(2, report.Reverted);
        Assert.Equal("# Plan\n\nUno\n", File.ReadAllText(note.FullPath));
        Assert.False(File.Exists(Path.Combine(_vault.VaultPath, "Nueva.md")));
    }

    [Fact]
    public async Task Creation_IsQueued_AndCreatedOnlyWhenApproved()
    {
        await Call("create_note", new { title = "Ideas", content = "Una idea" });
        Assert.False(File.Exists(Path.Combine(_vault.VaultPath, "Ideas.md")));
        var change = Assert.Single(_review.Changes);
        Assert.Equal(AiReviewChangeKind.Create, change.Kind);
        Assert.Equal("Ideas.md", change.RelativePath);

        ApproveAll();

        Assert.Contains("Una idea", File.ReadAllText(Path.Combine(_vault.VaultPath, "Ideas.md")));
        Assert.Equal(Path.Combine(_vault.VaultPath, "Ideas.md"), _turn.Single().TargetId);
    }

    [Fact]
    public async Task NoteChangedSinceProposal_IsNotOverwritten_AndItsFollowersAreSkipped()
    {
        var note = _vault.CreateNote("Plan", initialContent: "# Plan\n\nUno\n");
        await Call("append_to_note", new { path = "Plan", text = "Dos" });
        await Call("append_to_note", new { path = "Plan", text = "Tres" });
        File.WriteAllText(note.FullPath, "# Plan\n\nEditado por el usuario\n");

        var report = ApproveAll();

        Assert.Equal(new AiReviewReport(0, 2, 0), report);
        Assert.Equal("# Plan\n\nEditado por el usuario\n", File.ReadAllText(note.FullPath));
        Assert.Equal(AiReviewProblem.ChangedSince, _review.Changes[0].Problem);
        Assert.Equal(AiReviewProblem.Dependency, _review.Changes[1].Problem);
        Assert.All(_turn, a => Assert.Equal(AiToolActivityState.Failed, a.State));
        Assert.All(_turn, a => Assert.False(string.IsNullOrEmpty(a.Error)));
    }

    [Fact]
    public async Task OnlySelectedChangesApply_TheRestAreDiscarded()
    {
        var plan = _vault.CreateNote("Plan", initialContent: "# Plan\n");
        var otra = _vault.CreateNote("Otra", initialContent: "# Otra\n");
        await Call("append_to_note", new { path = "Plan", text = "A" });
        await Call("append_to_note", new { path = "Otra", text = "B" });

        var report = _tools.ResolveReview(_review, [_review.Changes[1]]);

        Assert.Equal(new AiReviewReport(1, 0, 1), report);
        Assert.Equal("# Plan\n", File.ReadAllText(plan.FullPath));
        Assert.Contains("B", File.ReadAllText(otra.FullPath));
        Assert.Equal(AiToolActivityState.Discarded, _turn[0].State);
        Assert.Equal(AiToolActivityState.Succeeded, _turn[1].State);
        Assert.False(_review.HasPending);
    }

    [Fact]
    public async Task NoOpEdit_QueuesNothing()
    {
        _vault.CreateNote("Plan", initialContent: "# Plan\n\nTexto\n");
        await Call("edit_note", new { path = "Plan", edits = new[] { new { find = "Texto", replace = "Texto" } } });
        Assert.Empty(_review.Changes);
        Assert.Equal(AiToolActivityState.Succeeded, _turn.Single().State);
    }

    [Fact]
    public async Task StructuralActions_StillApplyAtOnce()
    {
        _vault.CreateNote("Plan", initialContent: "# Plan\n");
        await Call("rename_note", new { path = "Plan", new_title = "Proyecto" });
        Assert.True(File.Exists(Path.Combine(_vault.VaultPath, "Proyecto.md")));
        Assert.Empty(_review.Changes);
    }

    [Fact]
    public async Task WithChangesOff_EverythingIsDiscarded()
    {
        _vault.CreateNote("Plan", initialContent: "# Plan\n");
        await Call("append_to_note", new { path = "Plan", text = "A" });
        _settings.Settings.Ai.AllowVaultChanges = false;

        var report = ApproveAll();

        Assert.Equal(new AiReviewReport(0, 0, 1), report);
        Assert.Equal("# Plan\n", File.ReadAllText(Path.Combine(_vault.VaultPath, "Plan.md")));
    }
}
