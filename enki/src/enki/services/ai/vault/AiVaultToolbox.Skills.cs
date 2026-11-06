using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Localization;

namespace enki.Services;

/// <summary>Herramientas de la biblioteca real de skills. Solo están disponibles en chats del vault y
/// conservan los eventos, la protección de skills empaquetadas y el control de cambios externos.</summary>
public sealed partial class AiVaultToolbox
{
    private void RegisterSkillTools()
    {
        Register("list_skills", AiToolScope.Vault, AiToolEffect.Read,
            "Lists skills in the vault library. Paths are relative to the skills library, not the vault.",
            Schema(LimitParam()), call =>
            {
                SkillWorkspace();
                return new { Skills = _skills.LoadSkills().Take(Limit(call)).Select(skill => new
                {
                    Path = Path.GetRelativePath(_skills.SkillsDirectory, skill.FilePath).Replace('\\', '/'),
                    skill.Name, skill.Description, skill.IsDefault
                }) };
            });

        Register("read_skill", AiToolScope.Vault, AiToolEffect.Read,
            "Reads a full skill, including YAML frontmatter. Required before write_skill in the same turn.",
            Schema(Req("path", Text("Markdown path relative to the skills library."))), call =>
            {
                var path = SkillPath(call);
                var skill = _skills.ReadSkill(path);
                if (skill.Content.Length > AiSettings.MaxMaxContextChars)
                    throw AiToolCall.Invalid("This skill is too large to read or replace through the assistant.");
                call.Context.SkillReads[path] = skill;
                call.Target(AiToolTargetKind.File, skill.Name);
                return new { skill.Name, skill.Content, skill.IsDefault };
            });

        Register("create_skill", AiToolScope.Vault, AiToolEffect.Change,
            "Creates a custom skill in the library root without overwriting existing files.",
            Schema(Req("name", Text("Skill name.")), Opt("content", Text("Markdown content, optionally with YAML frontmatter."))), call =>
            {
                SkillWorkspace();
                var skill = _skills.CreateSkill(_skills.SkillsDirectory, call.RequireText("name"));
                if (call.GetText("content") is { } content) _skills.SaveSkill(skill, content);
                call.Context.SkillReads[skill.FilePath] = skill;
                call.Target(AiToolTargetKind.File, skill.Name);
                return new { Path = Path.GetFileName(skill.FilePath), skill.Name };
            });

        Register("write_skill", AiToolScope.Vault, AiToolEffect.Change,
            "Replaces a custom skill read with read_skill in this turn. Rejects external changes and bundled skills.",
            Schema(Req("path", Text("Markdown path relative to the skills library.")), Req("content", Text("Full new Markdown content."))), call =>
            {
                var path = SkillPath(call);
                if (!call.Context.SkillReads.TryGetValue(path, out var skill))
                    throw AiToolCall.Invalid("Read this skill with read_skill before replacing it.");
                if (skill.IsDefault) throw AiToolCall.Invalid("Bundled skills cannot be overwritten. Create a custom skill instead.");
                _skills.SaveSkill(skill, call.RequireText("content"));
                call.Target(AiToolTargetKind.File, skill.Name);
                return new { skill.Name };
            });

        RegisterAsync("delete_skill", AiToolScope.Vault, AiToolEffect.Irreversible,
            "Permanently deletes a custom skill after explicit user confirmation. Bundled skills cannot be deleted.",
            Schema(Req("path", Text("Markdown path relative to the skills library."))), async call =>
            {
                var path = SkillPath(call);
                var skill = _skills.ReadSkill(path);
                if (skill.IsDefault) throw AiToolCall.Invalid("Bundled skills cannot be deleted.");
                call.Target(AiToolTargetKind.File, skill.Name);
                await RequireApprovalAsync(call, LocalizedTextFormatter.Format("Assistant_Skill_DeleteConfirm"), [skill.Name]);
                path = SkillPath(call);
                if (File.ReadAllText(path) != skill.Content)
                    throw new AiToolException(AiToolErrorKind.Conflict, "The skill changed while awaiting confirmation. Read it again.");
                _skills.DeleteSkill(skill);
                call.Context.SkillReads.Remove(path);
                return new { Deleted = true };
            });
    }

    /// <summary>Reutiliza las restricciones de rutas del vault sobre la raíz de la biblioteca. Tampoco
    /// acepta que la carpeta interna o la biblioteca se hayan sustituido por enlaces simbólicos.</summary>
    private AiWorkspace SkillWorkspace()
    {
        if (!_vault.IsVaultConfigured) throw new VaultUnavailableException(_vault.VaultPath);
        foreach (var path in new[] { Path.Combine(_vault.VaultPath, ".enki"), _skills.SkillsDirectory })
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new AiToolException(AiToolErrorKind.AccessDenied, "The skills library cannot be a symbolic link.");
        return new AiWorkspace("skills", "Skills", _skills.SkillsDirectory, null);
    }

    private string SkillPath(AiToolCall call)
    {
        var path = ResolvePath(SkillWorkspace(), call.RequireText("path"));
        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw AiToolCall.Invalid("Skills must be Markdown files (.md).");
        if (!File.Exists(path)) throw new FileNotFoundException("Skill not found.");
        return path;
    }
}
