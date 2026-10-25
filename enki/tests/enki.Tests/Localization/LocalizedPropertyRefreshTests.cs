using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace enki.Tests.Localization;

/// <summary>Comprueba, propiedad a propiedad, que toda etiqueta calculada que se resuelve contra el idioma
/// activo se vuelve a anunciar en un cambio de idioma en caliente.
///
/// <para><see cref="RefreshLocalizedLabelsCoverageTests"/> solo compara el número de declaraciones de
/// <c>RefreshLocalizedLabels</c> con el número de llamadas en todo el árbol: no ve una propiedad que el
/// método simplemente se deja fuera. Esa laguna era real — <c>NotesViewModel.NewTabTooltip</c> solo se
/// anunciaba al cambiar de modo de vista, y los overlays de edición abiertos (tarjeta Kanban, tarea y lista
/// de Polar, recordatorio) sobreviven a ir a Ajustes y volver sin que <c>Reload()</c> los reconstruya,
/// porque reconstruirlos descartaría lo que el usuario llevara escrito.</para>
///
/// <para>Una propiedad se considera cubierta si <c>RefreshLocalizedLabels</c> de su clase la anuncia
/// directamente; si ese método delega en <c>Reload()</c>/<c>Rebuild()</c> y la clase la anuncia en algún
/// otro punto (el caso de <c>KanbanViewModel.BoardSummaryLabel</c>, que viaja en <c>RefreshStats</c>); si
/// un <c>[NotifyPropertyChangedFor]</c> la encadena a un campo observable; o si su clase figura en
/// <see cref="RecreatedByParent"/>.</para></summary>
public class LocalizedPropertyRefreshTests
{
    private static readonly string s_sourceDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "enki"));

    /// <summary>Tipos cuyas instancias no se refrescan: el padre las tira y las vuelve a crear en su
    /// <c>Reload()</c>/<c>Rebuild()</c>, así que nacen ya en el idioma nuevo. Cada entrada indica quién las
    /// reconstruye. Ampliar esta lista para silenciar una etiqueta que de verdad se queda obsoleta es
    /// exactamente lo que el test existe para impedir: verificar primero que el padre la recrea.</summary>
    private static readonly Dictionary<string, string> RecreatedByParent = new()
    {
        ["AiAttachmentPickerItemViewModel"] = "AssistantViewModel.CollectPickerItems, cada vez que se abre el selector de adjuntos",
        ["CalendarMonthDayViewModel"] = "CalendarViewModel.Rebuild → RebuildMonth",
        ["DailyConsistencyDayViewModel"] = "NotesViewModel.ReloadConsistency (también al cambiar de idioma, ver RefreshLocalizedLabels)",
        ["FileFolderCardViewModel"] = "FilesViewModel.Reload",
        ["HabitItemViewModel"] = "HabitsViewModel.Reload",
        ["KanbanCardViewModel"] = "KanbanViewModel.Reload → AddColumnViewModel",
        ["NoteCardViewModel"] = "NotesViewModel.Reload",
        ["NoteVersionItemViewModel"] = "EditorVersionHistoryViewModel.Reload",
        ["NotificationItemViewModel"] = "NotificationsViewModel.Reload y NotificationCenterViewModel.Reload",
        ["PolarTaskRowViewModel"] = "PolarTaskManagementViewModel.Reload",
        ["RecentVaultItemViewModel"] = "VaultSwitcherViewModel.Refresh (también al cambiar de idioma, ver RefreshLocalizedLabels)",
        ["ReminderItemViewModel"] = "ReminderInboxViewModel.Reload",
        ["ReminderListItemViewModel"] = "RemindersViewModel.Reload",
        ["SessionListItemViewModel"] = "SessionsViewModel.Reload",
        ["SessionOptionViewModel"] = "NotesViewModel.ReloadSessions",
        ["SkillFolderCardViewModel"] = "SkillsViewModel.Reload",
        ["TrashedPolarListCardViewModel"] = "TrashViewModel.Reload",
        ["TrashedPolarTaskCardViewModel"] = "TrashViewModel.Reload",
        ["TrashedReminderCardViewModel"] = "TrashViewModel.Reload",
        ["TrashedSessionCardViewModel"] = "TrashViewModel.Reload",
        ["TrashedTaskCardViewModel"] = "TrashViewModel.Reload",

        // No son viewmodels: no hay binding que dejar obsoleto porque cada lectura vuelve a resolver el
        // texto contra la cultura activa, y quien los muestra sí está en la cadena de refresco.
        ["CustomThemeService"] = "servicio; el resultado se resuelve al leerse",
        ["NoteFile"] = "modelo; lo consume NoteCardViewModel, recreado en NotesViewModel.Reload",
        ["NoteVersion"] = "modelo; lo consume NoteVersionItemViewModel, recreado en EditorVersionHistoryViewModel.Reload",
        ["PolarSyncService"] = "servicio; LastError se resuelve al leerse",
        ["SlashCommand"] = "modelo; la paleta se reconstruye en EditorViewModel",
        ["VaultFile"] = "modelo; lo consume FilesViewModel.Reload",

        // Su título y su tooltip los reinyecta StatisticsViewModel.RefreshLocalizedLabels llamando a
        // ApplyLocalizedTitle, que anuncia ToggleTooltip; no es raíz de la cadena a propósito.
        ["StatisticsSectionViewModel"] = "StatisticsViewModel.RefreshLocalizedLabels → ApplyLocalizedTitle",
    };

    private static readonly Regex LocalizedLookup = new(
        @"LocalizationService\.Instance\s*\[|_localization\s*\[|Plural\.Get\(" +
        @"|LocalizedTextFormatter\.(?:Format|Resolve|Join)\(|RelativeTimeFormatter\.");

    private static readonly Regex ClassDeclaration = new(
        @"^\s*(?:\[[^\]]*\]\s*)?(?:public|internal|private|protected|sealed|abstract|static|partial|\s)*\bclass\s+(\w+)",
        RegexOptions.Multiline);

    [Fact]
    public void EveryLocalizedComputedProperty_IsAnnouncedOnLanguageChange()
    {
        // Se agrupa por nombre de clase, no por archivo: SettingsViewModel y NotesViewModel son parciales
        // repartidas en varios archivos, y su RefreshLocalizedLabels vive lejos de buena parte de las
        // etiquetas que anuncia.
        var sources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(s_sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (ClassDeclaration.Match(text) is not { Success: true } match) continue;
            if (!sources.TryGetValue(match.Groups[1].Value, out var parts))
                sources[match.Groups[1].Value] = parts = [];
            parts.Add(text);
        }

        var stale = new List<string>();
        foreach (var (className, parts) in sources)
        {
            if (RecreatedByParent.ContainsKey(className)) continue;
            var text = string.Join('\n', parts);
            var announced = AnnouncedProperties(text);
            stale.AddRange(LocalizedComputedProperties(text)
                .Where(property => !announced.Contains(property))
                .Select(property => $"{className}.{property}"));
        }

        Assert.True(stale.Count == 0,
            "Estas etiquetas se resuelven contra el idioma activo pero nada las vuelve a anunciar al " +
            "cambiarlo, así que se quedan en el idioma anterior hasta que otra cosa las toque. Anúncialas " +
            "en el RefreshLocalizedLabels de su clase (y conecta esa clase a MainWindowViewModel." +
            "RefreshAllLocalizedLabels), o documenta en RecreatedByParent quién recrea la instancia:" +
            Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    /// <summary>Propiedades de solo lectura cuyo getter resuelve un recurso localizado — tanto las de
    /// cuerpo de expresión (<c>=&gt;</c>) como las de bloque <c>get</c> sin <c>set</c>.</summary>
    private static IEnumerable<string> LocalizedComputedProperties(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var expression = Regex.Match(lines[i], @"^\s*public\s+(?!.*\bvoid\b)[\w\.<>\[\]\?,]+\s+(\w+)\s*=>(.*)");
            if (expression.Success)
            {
                var body = expression.Groups[2].Value;
                for (var j = i + 1; j < lines.Length && j - i <= 8 && !body.Contains(';'); j++) body += lines[j];
                if (LocalizedLookup.IsMatch(body)) yield return expression.Groups[1].Value;
                continue;
            }

            var block = Regex.Match(lines[i], @"^\s*public\s+[\w\.<>\[\]\?,]+\s+(\w+)\s*$");
            if (!block.Success || i + 1 >= lines.Length || lines[i + 1].Trim() != "{") continue;
            var getter = string.Join('\n', lines.Skip(i + 1).Take(14));
            if (LocalizedLookup.IsMatch(getter) && !Regex.IsMatch(getter, @"^\s*set", RegexOptions.Multiline))
                yield return block.Groups[1].Value;
        }
    }

    /// <summary>Nombres que la clase vuelve a anunciar en un cambio de idioma: los de su propio
    /// <c>RefreshLocalizedLabels</c> y los de todo método de la clase alcanzable desde él, más las cadenas
    /// <c>[NotifyPropertyChangedFor]</c>.
    ///
    /// <para>Se recorre el grafo de llamadas en vez de aceptar cualquier <c>OnPropertyChanged</c> del
    /// archivo porque esa versión laxa dejaba pasar el caso real: <c>NotesViewModel.NewTabTooltip</c> se
    /// anunciaba desde <c>OnIsSimplifiedViewChanged</c>, un método que el refresco nunca llega a invocar.
    /// Recorriéndolo sí se distingue de <c>KanbanViewModel.BoardSummaryLabel</c>, que viaja en
    /// <c>RefreshStats</c> y a ese sí lo llama <c>Reload()</c>.</para></summary>
    private static HashSet<string> AnnouncedProperties(string text)
    {
        var bodies = MethodBodies(text);
        var announced = new HashSet<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(RefreshLocalizedLabelsBodies(text));

        while (pending.Count > 0)
        {
            var body = pending.Dequeue();
            announced.UnionWith(Notifications(body));
            foreach (Match call in Regex.Matches(body, @"(\w+)\s*\("))
            {
                var callee = call.Groups[1].Value;
                if (!visited.Add(callee) || !bodies.TryGetValue(callee, out var calleeBody)) continue;
                pending.Enqueue(calleeBody);
            }
        }

        foreach (Match chained in Regex.Matches(text, @"\[NotifyPropertyChangedFor\(nameof\((\w+)\)\)\]"))
            announced.Add(chained.Groups[1].Value);

        return announced;
    }

    /// <summary>Cuerpo de cada método declarado a nivel de miembro, indexado por nombre — la entrada del
    /// recorrido de <see cref="AnnouncedProperties"/>. Misma delimitación por indentación que
    /// <see cref="RefreshLocalizedLabelsBodies"/>.</summary>
    private static Dictionary<string, string> MethodBodies(string text)
    {
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match block in Regex.Matches(
                     text,
                     @"^    (?:[\w\.<>\[\]\?,]+\s+)+?(\w+)\s*\([^)]*\)\s*\{(.*?)\n    \}",
                     RegexOptions.Singleline | RegexOptions.Multiline))
            bodies[block.Groups[1].Value] = block.Groups[2].Value;

        foreach (Match expression in Regex.Matches(
                     text, @"^    (?:[\w\.<>\[\]\?,]+\s+)+?(\w+)\s*\([^)]*\)\s*=>([^;]*);",
                     RegexOptions.Multiline))
            bodies.TryAdd(expression.Groups[1].Value, expression.Groups[2].Value);

        return bodies;
    }

    /// <summary>El cuerpo del método, delimitado por la llave de cierre a la indentación de miembro en vez
    /// de contando llaves: un <c>{</c> suelto dentro de un comentario o de una cadena de formato
    /// (<c>"{0}%"</c> aparece literalmente en uno de estos métodos) desequilibra cualquier recuento.</summary>
    private static IEnumerable<string> RefreshLocalizedLabelsBodies(string text)
    {
        foreach (Match expression in Regex.Matches(text, @"void\s+RefreshLocalizedLabels\s*\([^)]*\)\s*=>([^;]*);"))
            yield return expression.Groups[1].Value;

        foreach (Match block in Regex.Matches(
                     text, @"void\s+RefreshLocalizedLabels\s*\([^)]*\)\s*\{(.*?)\n    \}", RegexOptions.Singleline))
            yield return block.Groups[1].Value;
    }

    private static IEnumerable<string> Notifications(string body) =>
        Regex.Matches(body, @"OnPropertyChanged\(nameof\((\w+)\)\)").Select(m => m.Groups[1].Value);
}
