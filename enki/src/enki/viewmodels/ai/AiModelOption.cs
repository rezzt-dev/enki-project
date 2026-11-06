using System.Collections.Generic;
using System.Linq;
using enki.Models;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Opción del selector de modelo. <see cref="Alias"/> vacío = el modelo por defecto del CLI. Los
/// alias son los que entiende <c>claude --model</c>, que siempre resuelve a la versión más reciente.</summary>
public sealed record AiModelOption(string Alias, string Label)
{
    public override string ToString() => Label;

    /// <param name="catalog">Modelos descubiertos por el proveedor (<see cref="AiBackendService.Models"/>).</param>
    public static IReadOnlyList<AiModelOption> Build(AiProvider provider, IReadOnlyList<AiModelEntry>? catalog = null, string? selected = null)
    {
        var discovered = (catalog ?? []).Select(m => new AiModelOption(m.Id, m.Label));
        var options = provider switch
        {
            AiProvider.OpenAi => new List<AiModelOption> { new(string.Empty, LocalizationService.Instance["Assistant_Model_Default"]) },
            AiProvider.Ollama => discovered.ToList(),
            // Vacío = el Flash más reciente que permita la clave, que sigue valiendo cuando Google retira versiones.
            AiProvider.Gemini => discovered.Prepend(new(string.Empty, LocalizationService.Instance["Assistant_Model_Default"])).ToList(),
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
        [
            new(AiProvider.Claude, LabelFor(AiProvider.Claude)), new(AiProvider.OpenAi, LabelFor(AiProvider.OpenAi)),
            new(AiProvider.Gemini, LabelFor(AiProvider.Gemini)), new(AiProvider.Ollama, LabelFor(AiProvider.Ollama))
        ];

    /// <summary>Nombre del proveedor tal como se enseña en selectores, estados y avisos.</summary>
    public static string LabelFor(AiProvider provider) => provider switch
    {
        AiProvider.OpenAi => "OpenAI · ChatGPT",
        AiProvider.Ollama => "Ollama",
        AiProvider.Gemini => "Google Gemini",
        _ => "Claude"
    };
}
