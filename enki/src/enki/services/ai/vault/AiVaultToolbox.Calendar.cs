using System;
using System.Collections.Generic;
using System.Linq;
using enki.Models;
using enki.Localization;
using enki.ViewModels;

namespace enki.Services;

/// <summary>Capas reales y suscripciones de solo lectura. Las URL privadas nunca salen del servicio.</summary>
public sealed partial class AiVaultToolbox
{
    private readonly ExternalCalendarService? _externalCalendars;
    private sealed record CalendarLayer(string Key, string Name, bool External, DateTime? UpdatedUtc = null);

    private IEnumerable<CalendarLayer> CalendarLayers()
    {
        yield return new(CalendarAggregationService.KanbanLayerKey, "Kanban", false);
        yield return new(CalendarAggregationService.ReminderLayerKey, LocalizationService.Instance["Nav_RemindersLabel"], false);
        if (_polarAuth.IsLinked)
            foreach (var list in _polar.Store.Lists.Where(l => !l.IsDeleted))
                yield return new(list.Id, list.Title, false);
        if (_externalCalendars is not null)
            foreach (var feed in _externalCalendars.Subscriptions)
                yield return new(CalendarAggregationService.ExternalLayerPrefix + feed.Id, feed.Name, true, feed.LastUpdatedUtc);
    }

    private void RegisterCalendarTools()
    {
        Register("list_calendar_layers", AiToolScope.Vault, AiToolEffect.Read,
            "Lists Calendar layers, their visibility and read-only subscribed calendars with cache update time. Never exposes subscription URLs. "
            + "External events are cached; this does not contact the remote server.", Schema(),
            _ => new { Layers = CalendarLayers().Select(l => new { l.Key, l.Name, l.External, l.UpdatedUtc,
                Visible = !_settings.Settings.CalendarHiddenLayerKeys.Contains(l.Key) }).ToList() }, () => _calendar is not null);
        RegisterAsync("set_calendar_layer", AiToolScope.Vault, AiToolEffect.Change,
            "Shows or hides an existing Calendar layer, with confirmation. Does not edit events or subscriptions.",
            Schema(Req("key", Text("Layer key from list_calendar_layers.")), Req("visible", Flag("Show this layer."))),
            async call =>
            {
                var key = call.RequireText("key");
                var layer = CalendarLayers().FirstOrDefault(l => l.Key == key)
                    ?? throw AiToolCall.Invalid("Unknown calendar layer. Use list_calendar_layers.");
                var visible = call.GetFlag("visible") ?? throw AiToolCall.Invalid("'visible' is required.");
                var before = !_settings.Settings.CalendarHiddenLayerKeys.Contains(key);
                if (before == visible) return new { Changed = false };
                await RequireApprovalAsync(call, LocalizedTextFormatter.Format("Assistant_Calendar_Confirm"),
                    [$"{layer.Name}: {AiSafeSettingsService.ValueLabel(before ? "true" : "false")} → {AiSafeSettingsService.ValueLabel(visible ? "true" : "false")}"]);
                call.Context.Cancellation.ThrowIfCancellationRequested();
                if (!CalendarLayers().Any(l => l.Key == key) || before != !_settings.Settings.CalendarHiddenLayerKeys.Contains(key))
                    throw new AiToolException(AiToolErrorKind.Conflict, "The calendar layer changed while waiting for confirmation.");
                WriteLayerVisibility(key, visible);
                call.Undoable(new AiToolUndo { Kind = AiUndoKind.CalendarLayerChanged, Target = key,
                    Previous = before.ToString(), ResultHash = visible.ToString() });
                return new { Changed = true };
            }, () => _calendar is not null);
        RegisterAsync("export_calendar_ics", AiToolScope.Vault, AiToolEffect.Read,
            "Exports pending native Calendar entries to .ics using Enki's save dialog. The user chooses the destination. "
            + "Includes recurrence rules, excludes completed items and subscribed external events. Only use when asked to export. "
            + "Returns saved=false if cancelled, empty or failed; never claim success then.", Schema(),
            async call => new { Saved = await _host.ExportCalendar!(call.Context.Cancellation) },
            () => _calendar is not null && _host.ExportCalendar is not null);
    }

    private IEnumerable<CalendarItemViewModel> FilterAgenda(AiToolCall call, DateTime from, DateTime to)
    {
        var layers = call.GetTextList("layer_keys");
        if (layers is not null && layers.Except(CalendarLayers().Select(l => l.Key)).Any())
            throw AiToolCall.Invalid("Unknown calendar layer. Use list_calendar_layers.");
        var visibleOnly = call.GetFlag("visible_only") ?? false;
        var completed = call.GetFlag("include_completed") ?? true;
        return _calendar!.BuildItems(from, to, call.GetFlag("include_occurrences") ?? true,
                call.GetFlag("include_external") ?? true)
            .Where(i => (layers is null || layers.Contains(i.LayerKey))
                && (!visibleOnly || !_settings.Settings.CalendarHiddenLayerKeys.Contains(i.LayerKey))
                && (completed || !i.IsCompleted));
    }

    private void WriteLayerVisibility(string key, bool visible)
    {
        var hidden = _settings.Settings.CalendarHiddenLayerKeys;
        var previous = hidden.ToList();
        if (visible) hidden.RemoveAll(k => k == key);
        else if (!hidden.Contains(key)) hidden.Add(key);
        try { _settings.Save(); }
        catch { _settings.Settings.CalendarHiddenLayerKeys = previous; throw; }
        SettingsApplied(CalendarLayers().FirstOrDefault(l => l.Key == key)?.Name ?? key);
    }

    private AiUndoSkipReason? RevertCalendarLayer(AiToolUndo undo)
    {
        if (!CalendarLayers().Any(l => l.Key == undo.Target)) return AiUndoSkipReason.Missing;
        if ((!_settings.Settings.CalendarHiddenLayerKeys.Contains(undo.Target)).ToString() != undo.ResultHash)
            return AiUndoSkipReason.ChangedSince;
        if (!bool.TryParse(undo.Previous, out var previous)) return AiUndoSkipReason.Failed;
        WriteLayerVisibility(undo.Target, previous);
        return null;
    }
}
