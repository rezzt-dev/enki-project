using System.Collections.Generic;
using enki.Localization;

namespace enki.ViewModels;

/// <summary>Opción del selector de modelo. <see cref="Alias"/> vacío = el modelo por defecto del CLI. Los
/// alias son los que entiende <c>claude --model</c>, que siempre resuelve a la versión más reciente.</summary>
public sealed record AiModelOption(string Alias, string Label)
{
    public override string ToString() => Label;

    public static IReadOnlyList<AiModelOption> Build() =>
    [
        new(string.Empty, LocalizationService.Instance["Assistant_Model_Default"]),
        new("claude-sonnet-5-5", "Sonnet 5.5"),
        new("fable", "Fable"),
        new("opus", "Opus"),
        new("sonnet", "Sonnet"),
        new("haiku", "Haiku")
    ];
}
