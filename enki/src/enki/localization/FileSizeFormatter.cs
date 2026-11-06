namespace enki.Localization;

/// <summary>Single "128 MB"-style byte-size formatter — before Fase 6 this exact loop (B/KB/GB/GB, divide by
/// 1024 until under threshold) was copy-pasted three times with tiny drifts (<c>VaultFile.SizeLabel</c> and
/// <c>DashboardViewModel.FormatSize</c> stopped at GB and used "0" for the byte unit;
/// <c>SettingsViewModel.FormatBytes</c> stopped at GB too but always used "0.#", and only
/// <c>DashboardViewModel</c> went as far as TB). Consolidated here so every vault-size label in the app
/// (Dashboard, Ajustes › General, Archivos grid) renders identically.</summary>
public static class FileSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        double size = bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < Units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return unitIndex == 0 ? $"{size:0} {Units[unitIndex]}" : $"{size:0.#} {Units[unitIndex]}";
    }
}
