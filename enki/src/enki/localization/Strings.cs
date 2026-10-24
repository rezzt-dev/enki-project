using System.Resources;

namespace enki.Localization;

/// <summary>Hand-written stand-in for the Visual-Studio-generated <c>Strings.Designer.cs</c>. This repo is
/// built with the plain .NET SDK/Rider, not through VS's ResXFileCodeGenerator custom tool, so there is no
/// automatic strongly-typed accessor class — every lookup goes through string keys via
/// <see cref="LocalizationService"/>'s indexer instead of generated properties. This class only exists to
/// expose the single <see cref="ResourceManager"/> that reads <c>localization/Strings.resx</c> (source) and
/// its per-culture satellites (<c>Strings.en-US.resx</c>, <c>Strings.fr-FR.resx</c>, ...), which the SDK's
/// convention-based resx build already produces without any extra csproj wiring — see
/// docs/agent-docs/internacionalizacion-i18n.md §1.1. <c>enki.Tests</c> also reads this (via
/// <c>InternalsVisibleTo</c>, see enki.csproj) for the resource-key parity test, §7.1.</summary>
internal static class Strings
{
    public static readonly ResourceManager ResourceManager =
        new("enki.Localization.Strings", typeof(Strings).Assembly);
}
