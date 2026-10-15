using System;
using System.Collections.Generic;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public class NotePropertyHelperTests
{
    [Fact]
    public void Extract_RecognizesYamlScalarsAndLists()
    {
        var properties = NotePropertyHelper.Extract("---\ntags: [work]\nstatus: active\npriority: 3\ndue: 2026-10-15\naliases:\n  - Alpha\n  - Beta\n---\n# Note");
        Assert.Equal("active", properties["status"]);
        Assert.Equal("3", properties["priority"]);
        Assert.Equal("2026-10-15", properties["due"]);
        Assert.Equal("Alpha, Beta", properties["aliases"]);
        Assert.False(properties.ContainsKey("tags"));
    }

    [Fact]
    public void Set_UpdatesOnePropertyAndPreservesOtherYamlAndBody()
    {
        var content = "---\n# note metadata\ntags: [work]\nstatus: old\naliases:\n  - Alpha\n---\n\n# Body\nText";
        var updated = NotePropertyHelper.Set(content, "status", "new: active");
        Assert.Contains("tags: [work]", updated);
        Assert.Contains("# note metadata", updated);
        Assert.Contains("aliases:\n  - Alpha", updated);
        Assert.Contains("# Body\nText", updated);
        Assert.Equal("new: active", NotePropertyHelper.Extract(updated)["status"]);
        Assert.DoesNotContain("status: old", updated);
    }

    [Fact]
    public void Set_RemovesOnlyRequestedProperty()
    {
        var content = "---\ntags: [work]\naliases:\n  - Alpha\nstatus: active\n---\n\n# Note";
        var updated = NotePropertyHelper.Set(content, "aliases", null);
        Assert.DoesNotContain("aliases", updated);
        Assert.DoesNotContain("- Alpha", updated);
        Assert.Equal("active", NotePropertyHelper.Extract(updated)["status"]);
        Assert.Equal(new List<string> { "work" }, NoteTagHelper.ExtractTags(updated));
    }

    [Fact]
    public void Set_RejectsReservedOrUnsafeKeys()
    {
        Assert.Throws<ArgumentException>(() => NotePropertyHelper.Set("# Note", "tags", "x"));
        Assert.Throws<ArgumentException>(() => NotePropertyHelper.Set("# Note", "a: b", "x"));
    }

    [Fact]
    public void Set_InvalidYaml_DoesNotWritePartialFrontMatter()
    {
        var invalid = "---\nstatus: [unfinished\n---\n\n# Note";
        Assert.Throws<FormatException>(() => NotePropertyHelper.Set(invalid, "owner", "Ana"));
    }

    [Fact]
    public void Set_RemovingLastProperty_PreservesExtraBodyBlankLines()
    {
        var content = "---\nstatus: active\n---\n\n\n# Note";
        Assert.Equal("\n# Note", NotePropertyHelper.Set(content, "status", null));
    }

    [Fact]
    public void Set_PreservesCrLfInImportedNotes()
    {
        var content = "---\r\nstatus: pending\r\n---\r\n\r\n# Note\r\nText";
        var updated = NotePropertyHelper.Set(content, "status", "done");
        Assert.Equal("---\r\nstatus: \"done\"\r\n---\r\n\r\n# Note\r\nText", updated);
    }

    [Fact]
    public void Query_FiltersByTagPropertyAndLocalDate()
    {
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        var note = new NoteFile { FullPath = "a.md", Title = "A", ModifiedUtc = DateTime.UtcNow,
            Tags = ["work"], Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["status"] = "In progress" } };
        var query = new NoteQuery { Tag = "WORK", PropertyKey = "STATUS", PropertyValue = "progress", ModifiedFrom = today, ModifiedTo = today };
        Assert.Single(NoteQueryService.Filter([note], query));
        query.ModifiedTo = "invalid";
        Assert.False(NoteQueryService.TryValidate(query, out _));
        Assert.Empty(NoteQueryService.Filter([note], query));
    }

    [Fact]
    public void Query_DateProperty_UsesFrontmatterDateAndExcludesMissingOrInvalidDates()
    {
        var note = new NoteFile { FullPath = "a.md", Title = "A", ModifiedUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["due"] = "2026-10-15" } };
        var query = new NoteQuery { DatePropertyKey = "DUE", ModifiedFrom = "2026-10-15", ModifiedTo = "2026-10-15" };
        Assert.Single(NoteQueryService.Filter([note], query));
        note.Properties["due"] = "tomorrow";
        Assert.Empty(NoteQueryService.Filter([note], query));
        note.Properties.Remove("due");
        Assert.Empty(NoteQueryService.Filter([note], query));
        query.ModifiedFrom = query.ModifiedTo = string.Empty;
        Assert.Empty(NoteQueryService.Filter([note], query));
        note.Properties["due"] = "2026-10-15";
        Assert.Single(NoteQueryService.Filter([note], query));
        query.DatePropertyKey = "due: unsafe";
        Assert.False(NoteQueryService.TryValidate(query, out _));
    }

    [Fact]
    public void Query_ExactPropertyValue_DoesNotMatchLongerStatus()
    {
        var note = new NoteFile { FullPath = "a.md", Title = "A",
            Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["status"] = "inactive" } };
        var query = new NoteQuery { PropertyKey = "status", PropertyValue = "active" };
        Assert.Single(NoteQueryService.Filter([note], query));
        query.ExactPropertyValue = true;
        Assert.Empty(NoteQueryService.Filter([note], query));
        note.Properties["status"] = "ACTIVE";
        Assert.Single(NoteQueryService.Filter([note], query));
    }
}
