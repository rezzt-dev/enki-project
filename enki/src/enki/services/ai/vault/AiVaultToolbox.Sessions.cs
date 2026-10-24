using System;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Gestión de las sesiones como tales —crear, renombrar, cambiar de color, mandar a la papelera—,
/// solo con acceso al vault entero. Su contenido se maneja con las herramientas de notas y archivos pasando
/// la sesión como <c>workspace</c>. Las sesiones protegidas con contraseña no se crean desde aquí: necesitan
/// una contraseña y enseñar códigos de recuperación, y eso es cosa del usuario.</summary>
public sealed partial class AiVaultToolbox
{
    private void RegisterSessionTools()
    {
        Register("create_session", AiToolScope.Sessions, AiToolEffect.Change,
            "Creates a new session: an isolated workspace inside the vault with its own notes and folders, shown as a graph "
            + "in the Sessions section. Use its name as 'workspace' afterwards. Password-protected sessions can only be created by the user.",
            Schema(Req("name", Text("Session name."))),
            call =>
            {
                var name = call.RequireText("name").Trim();
                call.Target(AiToolTargetKind.Session, name);
                var session = _sessions.Create(name);
                call.Target(AiToolTargetKind.Session, session.Name, session.Id);
                _host.SessionsChanged();
                return new { Workspace = session.Name, session.Id };
            });

        Register("update_session", AiToolScope.Sessions, AiToolEffect.Change,
            "Renames a session and/or changes its color (hex like #4F8A8B, or null for the automatic color).",
            Schema(Req("session", Text("Session name or id.")), Opt("new_name", Text("New name.")),
                Opt("color", NullableText("Hex color, or null to go back to the automatic one."))),
            call =>
            {
                var session = SessionArgument(call);
                if (call.GetText("new_name") is { Length: > 0 } name) session = _sessions.Rename(session, name.Trim());
                if (call.Has("color")) session = _sessions.SetColor(session, call.GetText("color"));
                call.Target(AiToolTargetKind.Session, session.Name, session.Id);
                _host.SessionsChanged();
                return new { Workspace = session.Name, session.Id, session.Color };
            });

        Register("delete_session", AiToolScope.Sessions, AiToolEffect.Change,
            "Moves a session, with all its notes and files, to the trash. The default session and this chat's own session cannot be deleted.",
            Schema(Req("session", Text("Session name or id."))),
            call =>
            {
                var session = SessionArgument(call);
                if (session.IsDefault)
                    throw AiToolCall.Invalid("The default session cannot be deleted; every vault keeps at least one.");
                if (session.Id == call.Context.Scope.SessionId)
                    throw AiToolCall.Invalid("This chat belongs to that session, so it cannot delete it.");
                if (!_host.PrepareSessionRemoval(session))
                    throw new AiToolException(AiToolErrorKind.Conflict,
                        "A note of that session is open with unsaved changes or a conflict. Ask the user to resolve it first.");
                if (!_sessions.Delete(session)) throw new AiToolException(AiToolErrorKind.NotFound, "The session no longer exists.");
                _host.SessionsChanged();
                return new { Trashed = true, Workspace = session.Name };
            });
    }

    /// <summary>El argumento <c>session</c> resuelto entre las sesiones del ámbito, igual que un <c>workspace</c>.</summary>
    private Session SessionArgument(AiToolCall call)
    {
        var requested = call.RequireText("session");
        if (string.Equals(requested.Trim(), AiWorkspace.VaultId, StringComparison.OrdinalIgnoreCase))
            throw AiToolCall.Invalid("\"vault\" is the vault root, not a session.");
        var workspace = Workspace(call, "session");
        call.Target(AiToolTargetKind.Session, workspace.Name, workspace.Id);
        return workspace.Session ?? throw AiToolCall.Invalid("A session name or id is required.");
    }
}
