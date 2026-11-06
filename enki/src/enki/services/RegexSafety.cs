using System;

namespace enki.Services;

/// <summary>Presupuesto común para expresiones regulares aplicadas a notas, plantillas, enlaces y temas
/// que puede proporcionar el usuario. Evita que una entrada patológica bloquee indefinidamente la UI.</summary>
internal static class RegexSafety
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
}
