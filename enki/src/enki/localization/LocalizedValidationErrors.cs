using System;
using enki.Models;

namespace enki.Localization;

/// <summary>Conserva la clave de una validación propia al cruzar el límite servicio/vista,
/// sin intentar deducirla a partir del mensaje ni cambiar el contrato de excepciones existente.</summary>
internal static class LocalizedValidationErrors
{
    private const string TextKey = "Enki.LocalizedValidation";

    public static InvalidOperationException Create(string key, params object?[] arguments)
    {
        var text = LocalizedTextFormatter.Format(key, arguments);
        var exception = new InvalidOperationException(LocalizedTextFormatter.Resolve(text));
        exception.Data[TextKey] = text;
        return exception;
    }

    public static LocalizedText Describe(Exception exception) =>
        exception.Data[TextKey] as LocalizedText ?? Services.VaultFailureReasons.DescribeLocalized(exception);
}
