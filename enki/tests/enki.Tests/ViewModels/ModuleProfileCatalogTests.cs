using System.Linq;
using enki.Models;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>ALT-02: los perfiles de uso solo tocan indicadores de visibilidad, nunca datos, y el perfil
/// activo se deduce de esos indicadores.</summary>
public class ModuleProfileCatalogTests
{
    private static AppSettings Apply(string profileId)
    {
        var settings = new AppSettings();
        ModuleProfileCatalog.ApplyTo(settings, ModuleProfileCatalog.Find(profileId)!);
        return settings;
    }

    private static string[] EnabledKeys(AppSettings settings) =>
        SidebarSectionCatalog.AllKeys.Where(k => ModuleProfileCatalog.IsEnabled(settings, k)).ToArray();

    [Fact]
    public void EveryProfile_OnlyReferencesKnownModules_AndKeepsOneMainSectionVisible()
    {
        foreach (var profile in ModuleProfileCatalog.All)
        {
            Assert.All(profile.Modules, k => Assert.Contains(k, SidebarSectionCatalog.AllKeys));
            Assert.DoesNotContain("Assistant", profile.Modules);
            Assert.Contains(profile.Modules, k => SidebarSectionCatalog.DefaultMainSectionOrder.Contains(k));
        }
    }

    [Fact]
    public void NotesOnly_LeavesNoTraceOfKanbanCalendarOrReminders()
    {
        var settings = Apply(ModuleProfileCatalog.NotesOnly);

        Assert.True(settings.NotesPanelEnabled);
        Assert.False(settings.KanbanPanelEnabled);
        Assert.False(settings.CalendarPanelEnabled);
        Assert.False(settings.RemindersSidebarEnabled);
        Assert.False(settings.RemindersPanelEnabled);
        Assert.False(settings.InboxPanelEnabled);
        Assert.False(settings.HabitsPanelEnabled);
        Assert.False(settings.DashboardPanelEnabled);
    }

    [Fact]
    public void Everything_MatchesAFreshInstall()
    {
        var fresh = new AppSettings();
        var applied = Apply(ModuleProfileCatalog.Everything);

        Assert.Equal(EnabledKeys(fresh), EnabledKeys(applied));
        Assert.Equal(fresh.RemindersPanelEnabled, applied.RemindersPanelEnabled);
        Assert.Equal(fresh.InboxPanelEnabled, applied.InboxPanelEnabled);
    }

    [Fact]
    public void Reapplying_AfterANarrowerProfile_RestoresModules()
    {
        var settings = Apply(ModuleProfileCatalog.NotesOnly);
        ModuleProfileCatalog.ApplyTo(settings, ModuleProfileCatalog.Find(ModuleProfileCatalog.Everything)!);

        Assert.True(settings.KanbanPanelEnabled);
        Assert.True(settings.CalendarPanelEnabled);
        Assert.True(settings.RemindersSidebarEnabled);
    }

    [Fact]
    public void ApplyTo_DoesNotTouchOrderOrCustomNames()
    {
        var settings = new AppSettings
        {
            SidebarMainSectionOrder = ["Notes", "Kanban"],
            SidebarSectionNames = { ["Notes"] = "Ideas" }
        };

        ModuleProfileCatalog.ApplyTo(settings, ModuleProfileCatalog.Find(ModuleProfileCatalog.NotesOnly)!);

        Assert.Equal(["Notes", "Kanban"], settings.SidebarMainSectionOrder);
        Assert.Equal("Ideas", settings.SidebarSectionNames["Notes"]);
    }

    [Theory]
    [InlineData(ModuleProfileCatalog.NotesOnly)]
    [InlineData(ModuleProfileCatalog.Planner)]
    [InlineData(ModuleProfileCatalog.Everything)]
    public void Detect_RecognisesTheAppliedProfile(string id)
    {
        Assert.Equal(id, ModuleProfileCatalog.Detect(EnabledKeys(Apply(id))));
    }

    [Fact]
    public void Detect_ReturnsNullOnceTheUserCustomises()
    {
        var settings = Apply(ModuleProfileCatalog.NotesOnly);
        settings.KanbanPanelEnabled = true;

        Assert.Null(ModuleProfileCatalog.Detect(EnabledKeys(settings)));
    }

    [Fact]
    public void Detect_IgnoresTheAssistant()
    {
        var keys = EnabledKeys(Apply(ModuleProfileCatalog.Planner)).Append("Assistant");

        Assert.Equal(ModuleProfileCatalog.Planner, ModuleProfileCatalog.Detect(keys));
    }

    [Fact]
    public void Find_ReturnsNullForUnknownIds()
    {
        Assert.Null(ModuleProfileCatalog.Find("nope"));
        Assert.Null(ModuleProfileCatalog.Find(null));
    }
}
