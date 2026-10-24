using Avalonia;
using Avalonia.Headless;
using enki;

namespace enki.UITests;

/// <summary>Bootstraps a single, process-wide headless (no window, no GPU) instance of the real
/// <see cref="enki.App"/> — <c>AvaloniaXamlLoader.Load</c> inside <c>App.Initialize()</c> pulls in every
/// real style/resource dictionary (<c>Colors.axaml</c>, <c>ControlThemes.axaml</c>, fonts) exactly like a
/// normal launch, which is what lets Fase 6's UI tests (§11) instantiate compiled Views and get real
/// bindings/templates instead of a bare, unstyled Avalonia app.
///
/// Deliberately hand-rolled instead of using <c>Avalonia.Headless.XUnit</c>'s <c>[AvaloniaFact]</c>
/// attribute: that package depends on xunit.v3, whose <c>FactAttribute</c> collides with the classic
/// xunit 2.x this project already uses everywhere else. <see cref="EnsureInitialized"/> is idempotent and
/// thread-safe, since xunit can run multiple test classes concurrently within the same process, but
/// <see cref="Application"/> can only ever be configured once per process.</summary>
internal static class HeadlessApp
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (Gate)
        {
            if (_initialized) return;
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = System.Environment.GetEnvironmentVariable("ENKI_GRAPH_PREVIEW") is null
                })
                .SetupWithoutStarting();
            _initialized = true;
        }
    }
}
