using System;
using System.Collections.Generic;
using System.Diagnostics;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre la validación del manifiesto de cifrado de una sesión protegida
/// (.docs/auditoria-seguridad-y-fallos-criticos.md, SEC-06). Los tres campos que gobiernan el desbloqueo
/// —la sal, la DEK envuelta y el número de iteraciones de PBKDF2— salen de <c>.session.json</c>, o sea de
/// un archivo del disco, y hasta este arreglo se consumían tal cual: un base64 roto lanzaba una
/// <see cref="FormatException"/> que nadie capturaba y que salía por encima de un método documentado como
/// "nunca lanza por contraseña incorrecta", y un <c>Iterations</c> disparatado se pagaba íntegro antes de
/// descubrir que el manifiesto no servía.
///
/// <para>Lo que fija esta suite es sobre todo la distinción: "contraseña incorrecta" devuelve false,
/// "manifiesto ilegible" lanza <see cref="SessionManifestCorruptException"/>. Colapsar las dos en el mismo
/// false es lo que hace que alguien reintente para siempre una contraseña correcta.</para></summary>
public class SessionManifestValidationTests
{
    private const string Root = "/tmp/enki-tests-sesion";
    private const string Password = "contraseña-de-prueba-larga";

    private static SessionEncryptionInfo NewInfo() =>
        SessionEncryptionService.CreateEncryptionInfo(Password, out _);

    private static SessionEncryptionInfo NewInfo(out IReadOnlyList<string> recoveryCodes) =>
        SessionEncryptionService.CreateEncryptionInfo(Password, out recoveryCodes);

    // ===================== Lo que sí tiene que seguir funcionando =====================

    [Fact]
    public void ARealManifest_StillUnlocks()
    {
        var info = NewInfo();

        Assert.True(new SessionEncryptionService().TryUnlockWithPassword(Root, info, Password));
    }

    [Fact]
    public void AWrongPassword_ReturnsFalseAndDoesNotThrow()
    {
        var info = NewInfo();

        Assert.False(new SessionEncryptionService().TryUnlockWithPassword(Root, info, "otra-cosa-distinta"));
    }

    [Fact]
    public void TheIterationCountWrittenByThisVersion_IsInsideTheAcceptedRange()
    {
        // Ata el rango al valor real: si algún día se sube DefaultIterations por encima del techo, este
        // test avisa antes de que un usuario se quede sin poder abrir la sesión que acaba de crear.
        var info = NewInfo();

        Assert.Equal(210_000, info.Password.Iterations);
        Assert.True(new SessionEncryptionService().TryUnlockWithPassword(Root, info, Password));
    }

    // ===================== Manifiesto ilegible =====================

    [Fact]
    public void InvalidBase64Salt_ReportsACorruptManifest()
    {
        var info = NewInfo();
        info.Password.SaltBase64 = "no-es-base64-válido!!";

        Assert.Throws<SessionManifestCorruptException>(
            () => new SessionEncryptionService().TryUnlockWithPassword(Root, info, Password));
    }

    [Fact]
    public void InvalidBase64WrappedKey_ReportsACorruptManifest()
    {
        var info = NewInfo();
        info.Password.WrappedDekBase64 = "*** tampoco ***";

        Assert.Throws<SessionManifestCorruptException>(
            () => new SessionEncryptionService().TryUnlockWithPassword(Root, info, Password));
    }

    [Fact]
    public void EmptySalt_ReportsACorruptManifest()
    {
        var info = NewInfo();
        info.Password.SaltBase64 = string.Empty;

        Assert.Throws<SessionManifestCorruptException>(
            () => new SessionEncryptionService().TryUnlockWithPassword(Root, info, Password));
    }

    [Fact]
    public void TruncatedWrappedKey_ReportsACorruptManifest()
    {
        var info = NewInfo();
        // Base64 válido, pero más corto que el nonce + clave + tag que produce el envoltorio.
        info.Password.WrappedDekBase64 = Convert.ToBase64String(new byte[8]);

        Assert.Throws<SessionManifestCorruptException>(
            () => new SessionEncryptionService().TryUnlockWithPassword(Root, info, Password));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(999)]
    public void AnImpossibleIterationCount_ReportsACorruptManifest(int iterations)
    {
        var info = NewInfo();
        info.Password.Iterations = iterations;

        Assert.Throws<SessionManifestCorruptException>(
            () => new SessionEncryptionService().TryUnlockWithPassword(Root, info, Password));
    }

    [Fact]
    public void AnEnormousIterationCount_IsRejectedWithoutDerivingAnything()
    {
        var info = NewInfo();
        info.Password.Iterations = 2_000_000_000;

        // El assert que importa es el tiempo: derivar dos mil millones de iteraciones de PBKDF2-SHA256
        // tardaría horas, así que sin la cota este test no falla, se cuelga. Se mide para que el fallo sea
        // un número y no "la suite no termina".
        var stopwatch = Stopwatch.StartNew();
        Assert.Throws<SessionManifestCorruptException>(
            () => new SessionEncryptionService().TryUnlockWithPassword(Root, info, Password));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"El rechazo tardó {stopwatch.Elapsed}: el coste se está pagando antes de validar.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void AnUnknownFormatVersion_ReportsACorruptManifest(int version)
    {
        var info = NewInfo();
        info.FormatVersion = version;

        Assert.Throws<SessionManifestCorruptException>(
            () => new SessionEncryptionService().TryUnlockWithPassword(Root, info, Password));
    }

    // ===================== Ruta de recuperación por código =====================

    [Fact]
    public void ACorruptRecoverySlot_DoesNotStopTheOthersFromBeingTried()
    {
        var info = NewInfo(out var codes);
        // La primera ranura queda ilegible; el usuario tiene el código de la última. Las 5 ranuras son
        // envoltorios independientes de la misma DEK, así que una dañada no puede costarle las otras
        // cuatro — que pueden ser su única vía de vuelta a sus notas.
        info.RecoveryCodes[0].SaltBase64 = "roto!!";

        var unlocked = new SessionEncryptionService()
            .TryUnlockWithRecoveryCode(Root, info, codes[4], out var usedSlotIndex);

        Assert.True(unlocked);
        Assert.Equal(4, usedSlotIndex);
    }

    [Fact]
    public void AWrongRecoveryCode_ReturnsFalseWhenEverySlotIsIntact()
    {
        var info = NewInfo();

        Assert.False(new SessionEncryptionService()
            .TryUnlockWithRecoveryCode(Root, info, "CODIGO-QUE-NO-EXISTE", out var usedSlotIndex));
        Assert.Null(usedSlotIndex);
    }

    [Fact]
    public void AWrongRecoveryCode_ReportsCorruptionWhenSomeSlotWasUnreadable()
    {
        var info = NewInfo();
        info.RecoveryCodes[2].WrappedDekBase64 = "###";

        // Ninguna ranura ha abierto nada y al menos una era ilegible: decirle al usuario "código
        // incorrecto" aquí lo mandaría a probar los otros cuatro códigos de una sesión que está dañada.
        Assert.Throws<SessionManifestCorruptException>(() => new SessionEncryptionService()
            .TryUnlockWithRecoveryCode(Root, info, "CODIGO-QUE-NO-EXISTE", out _));
    }

    [Fact]
    public void ARealRecoveryCode_StillUnlocks()
    {
        var info = NewInfo(out var codes);

        Assert.True(new SessionEncryptionService()
            .TryUnlockWithRecoveryCode(Root, info, codes[0], out var usedSlotIndex));
        Assert.Equal(0, usedSlotIndex);
    }
}
