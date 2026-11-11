using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using enki.Models;
using enki.Localization;

namespace enki.Services;

/// <summary>Cambios visuales propuestos: confirmación individual, control de conflictos y reversión.</summary>
public sealed partial class AiVaultToolbox
{
    private readonly AiSafeSettingsService _safeSettings;
    private readonly NotificationService? _notifications;

    private void RegisterSettingsTools()
    {
        Register("list_safe_settings", AiToolScope.App, AiToolEffect.Read,
            "Lists only visual settings that may be changed with explicit user confirmation. No keys, providers or privacy settings.",
            Schema(), _ => new { Settings = _safeSettings.List() });
        RegisterAsync("change_safe_setting", AiToolScope.App, AiToolEffect.Change,
            "Proposes a reversible visual setting change from list_safe_settings. Always waits for the user's confirmation. "
            + "Never changes credentials, providers or privacy. Only request a change the user asked for.",
            Schema(Req("key", Text("Key from list_safe_settings.")), Req("value", new JsonObject
                { ["type"] = new JsonArray("string", "integer", "boolean") })),
            async call =>
            {
                var key = call.RequireText("key");
                var value = call.Get("value") ?? throw AiToolCall.Invalid("'value' is required.");
                _safeSettings.Validate(key, value);
                var before = _safeSettings.Read(key);
                var after = JsonSerializer.Serialize(JsonSerializer.Deserialize<object>(value.GetRawText()));
                if (before == after) return new { Changed = false, Key = key };
                await RequireApprovalAsync(call, LocalizedTextFormatter.Format("Assistant_Settings_Confirm"),
                    [$"{AiSafeSettingsService.Label(key)}: {AiSafeSettingsService.ValueLabel(before)} → {AiSafeSettingsService.ValueLabel(after)}"]);
                call.Context.Cancellation.ThrowIfCancellationRequested();
                if (_safeSettings.Read(key) != before)
                    throw new AiToolException(AiToolErrorKind.Conflict, "The setting changed while waiting for confirmation. Nothing was applied.");
                _safeSettings.Write(key, value);
                call.Undoable(new AiToolUndo { Kind = AiUndoKind.SafeSettingChanged, Target = key,
                    Previous = before, ResultHash = _safeSettings.Read(key) });
                SettingsApplied(AiSafeSettingsService.Label(key));
                return new { Changed = true, Key = key };
            });
    }

    private void SettingsApplied(string key)
    {
        _host.SettingsChanged();
        _notifications?.PushLocalized(LocalizedTextFormatter.Format("Assistant_Settings_Changed_Title"),
            LocalizedTextFormatter.Format("Assistant_Settings_Changed", key), NotificationSeverity.Success);
    }

    private AiUndoSkipReason? RevertSetting(AiToolUndo undo)
    {
        if (_safeSettings.Read(undo.Target) != undo.ResultHash) return AiUndoSkipReason.ChangedSince;
        if (undo.Previous is null) return AiUndoSkipReason.Missing;
        using var previous = JsonDocument.Parse(undo.Previous);
        _safeSettings.Write(undo.Target, previous.RootElement);
        SettingsApplied(AiSafeSettingsService.Label(undo.Target));
        return null;
    }
}
