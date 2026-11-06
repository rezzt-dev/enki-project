using System.Collections.Generic;
using System.Linq;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Single source of truth for every sidebar section that can appear either in the main
/// scrollable nav group or in the fixed bottom group (max <see cref="MaxBottomSections"/>, alongside
/// Ajustes) — shared between <see cref="MainWindowViewModel"/> (which reads it to build
/// <c>NavItems</c>/<c>BottomNavItems</c>) and <see cref="SettingsViewModel"/> (which reads it to build the
/// drag-and-drop rows in Ajustes › General › Secciones de la barra lateral). Keeping this in one place
/// means the shell's actual sidebar and the settings screen that configures it can never drift apart on
/// what a section's icon/label is or what "the default order" means. Ajustes itself is intentionally not a
/// key here — it never appears in either group, always renders as its own fixed entry beneath everything
/// else, and is opened from its own button rather than <c>NavigateCommand</c>.</summary>
public static class SidebarSectionCatalog
{
    /// <summary>Keys that start out in the main scrollable group, in their default order — the same set
    /// and order the sidebar always used before per-user reordering existed.</summary>
    public static readonly string[] DefaultMainSectionOrder =
    [
        "Dashboard", "Calendar", "Kanban", "Notes", "Files", "Sessions", "Trash", "Habits", "Skills", "Assistant", "TagManager"
    ];

    /// <summary>Keys that start out pinned to the fixed bottom group, in their default order.</summary>
    public static readonly string[] DefaultBottomSectionOrder =
    [
        "Reminders", "Notifications", "Statistics"
    ];

    /// <summary>Every valid key, main-group defaults first — used to filter out stale/unknown entries from
    /// a persisted order list (e.g. hand-edited settings.json).</summary>
    public static readonly string[] AllKeys = DefaultMainSectionOrder.Concat(DefaultBottomSectionOrder).ToArray();

    /// <summary>At most this many sections can be pinned to the bottom group at once — it renders as fixed
    /// buttons above Ajustes, and more than a handful there would defeat the point of it being the compact
    /// alternative to the scrollable main list.</summary>
    public const int MaxBottomSections = 3;

    /// <summary>Longitud máxima de un nombre personalizado — limitada por el ancho del sidebar desplegado.</summary>
    public const int MaxCustomNameLength = 16;

    /// <summary>Nombre personalizado de <paramref name="key"/> si el usuario definió uno, o <c>null</c>.</summary>
    public static string? CustomName(IReadOnlyDictionary<string, string>? names, string key) =>
        names is not null && names.TryGetValue(key, out var n) && !string.IsNullOrWhiteSpace(n)
            ? Truncate(n)
            : null;

    /// <summary>Recorta espacios y limita a <see cref="MaxCustomNameLength"/>; vacío devuelve cadena vacía.</summary>
    public static string Truncate(string? name)
    {
        var t = name?.Trim() ?? "";
        return t.Length > MaxCustomNameLength ? t[..MaxCustomNameLength] : t;
    }

    public static readonly IReadOnlyDictionary<string, (MaterialIconKind Icon, string LabelKey)> Meta =
        new Dictionary<string, (MaterialIconKind, string)>
        {
            ["Dashboard"] = (MaterialIconKind.ViewDashboardOutline, "Nav_Dashboard"),
            ["Calendar"] = (MaterialIconKind.CalendarBlankOutline, "Nav_Calendar"),
            ["Kanban"] = (MaterialIconKind.ViewColumnOutline, "Nav_Kanban"),
            ["Notes"] = (MaterialIconKind.NoteTextOutline, "Nav_Documents"),
            ["Files"] = (MaterialIconKind.FileImageOutline, "Nav_Files"),
            ["Sessions"] = (MaterialIconKind.SitemapOutline, "Nav_Sessions"),
            ["Trash"] = (MaterialIconKind.TrashCanOutline, "Nav_Trash"),
            ["TagManager"] = (MaterialIconKind.TagMultipleOutline, "Nav_TagManager"),
            ["Habits"] = (MaterialIconKind.ClipboardCheckOutline, "Nav_Habits"),
            ["Skills"] = (MaterialIconKind.Brain, "Nav_Skills"),
            ["Assistant"] = (MaterialIconKind.Creation, "Nav_Assistant"),
            ["Reminders"] = (MaterialIconKind.CalendarClockOutline, "Nav_RemindersLabel"),
            ["Notifications"] = (MaterialIconKind.BellRingOutline, "Nav_NotificationsLabel"),
            ["Statistics"] = (MaterialIconKind.ChartBoxOutline, "Nav_Statistics")
        };

    /// <summary>Resolves the persisted main/bottom order lists (either of which may be null/empty, e.g. a
    /// settings file saved before this feature existed) into two clean key lists: unknown/duplicate keys
    /// dropped, the bottom list capped at <see cref="MaxBottomSections"/>, and any key missing from both
    /// (a section added in a later Enki version than the one that last saved settings.json) appended to
    /// whichever group it belongs to by default.</summary>
    public static (List<string> Main, List<string> Bottom) Resolve(List<string>? mainOrder, List<string>? bottomOrder)
    {
        IEnumerable<string> mainSource = mainOrder is { Count: > 0 } ? mainOrder : DefaultMainSectionOrder;
        var main = mainSource.Where(k => AllKeys.Contains(k)).Distinct().ToList();

        IEnumerable<string> bottomSource = bottomOrder is { Count: > 0 } ? bottomOrder : DefaultBottomSectionOrder;
        var bottom = bottomSource
            .Where(k => AllKeys.Contains(k) && !main.Contains(k))
            .Distinct()
            .Take(MaxBottomSections)
            .ToList();

        var seen = new HashSet<string>(main.Concat(bottom));
        foreach (var key in AllKeys)
        {
            if (!seen.Add(key)) continue;
            if (DefaultBottomSectionOrder.Contains(key) && bottom.Count < MaxBottomSections) bottom.Add(key);
            else main.Add(key);
        }

        return (main, bottom);
    }
}
