using System.Collections.Generic;
using System.Linq;
using enki.Models;
using enki.Localization;

namespace enki.ViewModels;

/// <summary>Opción del selector de modelo. <see cref="Alias"/> vacío = el modelo por defecto del CLI. Los
/// alias son los que entiende <c>claude --model</c>, que siempre resuelve a la versión más reciente.</summary>
public sealed record AiModelOption(string Alias, string Label)
{
    public override string ToString() => Label;

    public static IReadOnlyList<AiModelOption> Build(AiProvider provider, IReadOnlyList<string>? localModels = null, string? selected = null)
    {
        var options = provider switch
        {
            AiProvider.OpenAi => new List<AiModelOption> { new(string.Empty, LocalizationService.Instance["Assistant_Model_Default"]) },
            AiProvider.Ollama => (localModels ?? []).Select(m => new AiModelOption(m, m)).ToList(),
            _ => Build().ToList()
        };
        if (!string.IsNullOrWhiteSpace(selected) && options.All(m => m.Alias != selected)) options.Add(new(selected, selected));
        if (options.Count == 0) options.Add(new(string.Empty, LocalizationService.Instance["Assistant_Model_Default"]));
        return options;
    }

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

/// <summary>Opción de proveedor compartida por Ajustes y el chat.</summary>
public sealed record AiProviderOption(AiProvider Provider, string Label)
{
    public override string ToString() => Label;
    public static IReadOnlyList<AiProviderOption> Build() =>
        [new(AiProvider.Claude, "Claude"), new(AiProvider.OpenAi, "OpenAI · ChatGPT"), new(AiProvider.Ollama, "Ollama")];
}
