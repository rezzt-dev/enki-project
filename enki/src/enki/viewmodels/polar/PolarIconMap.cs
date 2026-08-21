using System.Collections.Generic;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>Maps Polar's opaque list-icon keys (docs/supabase-sync/01, "Enum de iconos válidos") to a
/// concrete <see cref="MaterialIconKind"/> for display. Per docs/supabase-sync/05 these keys are treated
/// as opaque strings — a key this map doesn't recognize just falls back to <see cref="Fallback"/> without
/// ever being rewritten, so round-tripping through Enki never loses a value Polar understands.</summary>
internal static class PolarIconMap
{
    public const MaterialIconKind Fallback = MaterialIconKind.FormatListBulleted;

    private static readonly Dictionary<string, MaterialIconKind> ByKey = new()
    {
        ["ic_list"] = MaterialIconKind.FormatListBulleted,
        ["ic_folder"] = MaterialIconKind.FolderOutline,
        ["ic_work"] = MaterialIconKind.BriefcaseOutline,
        ["ic_home"] = MaterialIconKind.HomeOutline,
        ["ic_favorite"] = MaterialIconKind.HeartOutline,
        ["ic_schedule"] = MaterialIconKind.ClockOutline,
        ["ic_star"] = MaterialIconKind.StarOutline,
        ["ic_circle"] = MaterialIconKind.CircleOutline,
        ["ic_edit"] = MaterialIconKind.PencilOutline,
        ["ic_location"] = MaterialIconKind.MapMarkerOutline,
        ["ic_image"] = MaterialIconKind.ImageOutline,
        ["ic_share"] = MaterialIconKind.ShareOutline,
        ["ic_sort"] = MaterialIconKind.SortVariant,
        ["ic_chat"] = MaterialIconKind.ChatOutline,
        ["ic_check_box"] = MaterialIconKind.CheckboxOutline,
        ["ic_heart"] = MaterialIconKind.HeartOutline
    };

    /// <summary>Offered by the list icon picker, in the same order as docs/supabase-sync/01's enum.</summary>
    public static readonly string[] Keys =
    [
        "ic_list", "ic_folder", "ic_work", "ic_home", "ic_favorite", "ic_schedule", "ic_star",
        "ic_circle", "ic_edit", "ic_location", "ic_image", "ic_share", "ic_sort", "ic_chat",
        "ic_check_box", "ic_heart"
    ];

    public static MaterialIconKind Resolve(string key) => ByKey.TryGetValue(key, out var icon) ? icon : Fallback;
}
