using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using enki.Localization;

namespace enki.Services;

/// <summary>Informe final de una importación (MED-03): qué se importó, qué se omitió o se importó con
/// pérdidas y por qué. Es texto Markdown que se guarda como una nota más dentro de la carpeta
/// «Importado de …», es decir, dentro del vault y nunca fuera de él.</summary>
public sealed partial class ExternalImportService
{
    /// <summary>Por qué un elemento no se importó o se importó incompleto. Cada valor tiene su texto en los
    /// recursos de idioma: <c>ExternalImport_Issue_&lt;valor&gt;</c>.</summary>
    public enum ImportIssueKind
    {
        /// <summary>Tipo de archivo o de elemento que Enki no importa.</summary>
        UnsupportedItem,
        /// <summary>Adjunto de un tipo que Enki no importa; la nota sí se importó.</summary>
        UnsupportedAttachment,
        /// <summary>Adjunto citado por la nota pero ausente de la exportación; la nota sí se importó.</summary>
        MissingAttachment,
        Empty,
        Trashed,
        Cancelled,
        NoDate,
        TooLarge,
        InvalidFormat,
        UnsafePath,
        EncryptedContent,
        /// <summary>La tarea se importó sin su repetición: las tarjetas no la tienen.</summary>
        RecurrenceDropped
    }

    /// <summary>Un elemento afectado y el motivo. <paramref name="Name"/> es el nombre que el usuario
    /// reconoce (título de la nota, ruta del archivo), nunca contenido de la nota.</summary>
    public sealed record ImportIssue(string Name, ImportIssueKind Kind);

    /// <summary>Tope de incidencias detalladas por informe; el resto solo se cuenta. Un grafo de miles de
    /// archivos desconocidos no debe producir un informe de megas.</summary>
    internal const int MaxReportedIssues = 500;

    internal sealed class IssueLog
    {
        private readonly List<ImportIssue> _items = [];
        public IReadOnlyList<ImportIssue> Items => _items;
        public int Overflow { get; private set; }

        public void Add(string name, ImportIssueKind kind)
        {
            if (_items.Count >= MaxReportedIssues) Overflow++;
            else _items.Add(new ImportIssue(name, kind));
        }
    }

    private string? _lastImportFolder;

    /// <summary>Texto Markdown del informe. Los títulos y los motivos salen de los recursos de idioma
    /// activos; los nombres de los elementos se escriben tal cual, sin saltos de línea ni comillas
    /// invertidas que rompan la lista.</summary>
    public static string BuildReport(Plan plan, Result result, bool interrupted = false)
    {
        var loc = LocalizationService.Instance;
        var report = new StringBuilder();
        report.Append("# ").AppendLine(string.Format(CultureInfo.CurrentCulture, loc["ExternalImport_Report_Title"], plan.Source)).AppendLine();
        report.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).AppendLine();
        if (interrupted) report.AppendLine("> " + loc["ExternalImport_Report_Interrupted"]).AppendLine();

        report.Append("## ").AppendLine(loc["ExternalImport_Report_Imported"]).AppendLine();
        report.AppendLine("- " + string.Format(CultureInfo.CurrentCulture, loc["ExternalImport_Report_Notes"], result.Notes, plan.Notes.Count));
        report.AppendLine("- " + string.Format(CultureInfo.CurrentCulture, loc["ExternalImport_Report_Tasks"], result.Tasks, plan.Tasks.Count));
        report.AppendLine("- " + string.Format(CultureInfo.CurrentCulture, loc["ExternalImport_Report_Events"], result.Events, plan.Events.Count));
        report.AppendLine("- " + string.Format(CultureInfo.CurrentCulture, loc["ExternalImport_Report_Assets"], result.Assets, plan.Assets.Count));
        report.AppendLine();

        var total = plan.Issues.Count + plan.IssuesOmitted + (plan.RecurringSeries > 0 ? 1 : 0);
        report.Append("## ").AppendLine(string.Format(CultureInfo.CurrentCulture, loc["ExternalImport_Report_Skipped"], total)).AppendLine();
        if (total == 0)
        {
            report.AppendLine(loc["ExternalImport_Report_NothingSkipped"]);
            return report.ToString();
        }

        if (plan.RecurringSeries > 0)
            report.AppendLine("- " + string.Format(CultureInfo.CurrentCulture, loc["ExternalImport_RecurringMessage"], plan.RecurringSeries));
        foreach (var group in plan.Issues.GroupBy(i => i.Kind))
        {
            report.AppendLine().Append("### ").AppendLine(string.Format(CultureInfo.CurrentCulture,
                loc["ExternalImport_Issue_" + group.Key], group.Count())).AppendLine();
            foreach (var issue in group) report.Append("- `").Append(Clean(issue.Name)).AppendLine("`");
        }
        if (plan.IssuesOmitted > 0)
            report.AppendLine().AppendLine(string.Format(CultureInfo.CurrentCulture, loc["ExternalImport_Report_More"], plan.IssuesOmitted));
        return report.ToString();
    }

    /// <summary>Guarda el informe como nota en la carpeta «Importado de …» de esta importación (la crea si
    /// la importación no escribió notas, por ejemplo con solo tareas). Devuelve la ruta de la nota, o
    /// <see langword="null"/> si no hay vault abierto: sin vault no hay dónde guardarla y el resultado se
    /// comunica solo con el aviso.</summary>
    public string? WriteReport(Plan plan, Result result, bool interrupted = false)
    {
        if (!_vault.IsVaultConfigured) return null;
        var folder = _lastImportFolder is { } known && Directory.Exists(known)
            ? known
            : VaultService.CreateFolder(_vault.VaultPath, "Importado de " + plan.Source).FullPath;
        var note = _vault.CreateNote(LocalizationService.Instance["ExternalImport_Report_NoteName"], folder,
            BuildReport(plan, result, interrupted));
        return note.FullPath;
    }

    private static string Clean(string name)
    {
        var flat = new string(name.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Replace('`', '\'');
        flat = flat.Trim();
        return flat.Length > 120 ? flat[..120] + "…" : flat;
    }
}
