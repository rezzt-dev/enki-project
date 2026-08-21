using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using enki.Models;

namespace enki.Services;

/// <summary>Wire-format DTOs for the 4 tables of docs/supabase-sync/03-esquema-supabase.md — snake_case
/// field names exactly as specified in docs/supabase-sync/05-contrato-interoperabilidad.md. Kept separate
/// from the local <c>enki.Models</c> POCOs (which use normal C# PascalCase, consistent with every other
/// local JSON file Enki already persists) so the wire format and the local storage format can evolve
/// independently — mapping between the two happens only in <see cref="PolarSyncService"/>.</summary>
internal static class PolarWire
{
    public static long ToEpochMs(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public static long? ToEpochMsOrNull(DateTime? utc) => utc is { } value ? ToEpochMs(value) : null;

    public static DateTime FromEpochMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    public static DateTime? FromEpochMsOrNull(long? ms) => ms is { } value ? FromEpochMs(value) : null;

    public static string RecurrenceToWire(PolarRecurrence recurrence) => recurrence switch
    {
        PolarRecurrence.Daily => "DAILY",
        PolarRecurrence.Weekly => "WEEKLY",
        PolarRecurrence.Monthly => "MONTHLY",
        PolarRecurrence.MonWed => "MON_WED",
        PolarRecurrence.FirstDayMonth => "FIRST_DAY_MONTH",
        _ => "NONE"
    };

    public static PolarRecurrence RecurrenceFromWire(string? wire) => wire switch
    {
        "DAILY" => PolarRecurrence.Daily,
        "WEEKLY" => PolarRecurrence.Weekly,
        "MONTHLY" => PolarRecurrence.Monthly,
        "MON_WED" => PolarRecurrence.MonWed,
        "FIRST_DAY_MONTH" => PolarRecurrence.FirstDayMonth,
        _ => PolarRecurrence.None
    };
}

internal sealed class TaskListDto : IPolarWireRow
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("icon")] public string Icon { get; set; } = "ic_list";
    [JsonPropertyName("color")] public string Color { get; set; } = "#7F52FF";
    [JsonPropertyName("order_index")] public int OrderIndex { get; set; }
    [JsonPropertyName("home_order_index")] public int HomeOrderIndex { get; set; }
    [JsonPropertyName("is_dependency_chain")] public bool IsDependencyChain { get; set; }
    [JsonPropertyName("is_deleted")] public bool IsDeleted { get; set; }
    [JsonPropertyName("deleted_at")] public long? DeletedAt { get; set; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public long UpdatedAt { get; set; }
}

internal sealed class TaskDto : IPolarWireRow
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [JsonPropertyName("list_id")] public string ListId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("completed")] public bool Completed { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
    [JsonPropertyName("due_date")] public long? DueDate { get; set; }
    [JsonPropertyName("order_index")] public int OrderIndex { get; set; }
    [JsonPropertyName("recurrence")] public string Recurrence { get; set; } = "NONE";
    [JsonPropertyName("priority")] public int Priority { get; set; }
    [JsonPropertyName("image_path")] public string? ImagePath { get; set; }
    [JsonPropertyName("time_estimate")] public int TimeEstimate { get; set; }
    [JsonPropertyName("is_deleted")] public bool IsDeleted { get; set; }
    [JsonPropertyName("deleted_at")] public long? DeletedAt { get; set; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public long UpdatedAt { get; set; }
}

internal sealed class SubtaskDto : IPolarWireRow
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [JsonPropertyName("task_id")] public string TaskId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("completed")] public bool Completed { get; set; }
    [JsonPropertyName("due_date")] public long? DueDate { get; set; }
    [JsonPropertyName("order_index")] public int OrderIndex { get; set; }
    [JsonPropertyName("is_deleted")] public bool IsDeleted { get; set; }
    [JsonPropertyName("deleted_at")] public long? DeletedAt { get; set; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public long UpdatedAt { get; set; }
}

internal sealed class ReminderDto : IPolarWireRow
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("date_time")] public long DateTime { get; set; }
    [JsonPropertyName("is_completed")] public bool IsCompleted { get; set; }
    [JsonPropertyName("latitude")] public double? Latitude { get; set; }
    [JsonPropertyName("longitude")] public double? Longitude { get; set; }
    [JsonPropertyName("radius")] public float? Radius { get; set; }
    [JsonPropertyName("location_name")] public string? LocationName { get; set; }
    [JsonPropertyName("is_deleted")] public bool IsDeleted { get; set; }
    [JsonPropertyName("deleted_at")] public long? DeletedAt { get; set; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public long UpdatedAt { get; set; }
}
