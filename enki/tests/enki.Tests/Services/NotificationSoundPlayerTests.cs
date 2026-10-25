using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="NotificationSoundPlayer.TryPlay"/> — el orden de la cadena de fallback de la
/// Fase 5 del roadmap de mejora continua (gap 9/10: asset empaquetado → comando del SO → Console.Beep →
/// silencio). Los tres eslabones reales (<c>Process.Start</c>, P/Invoke a winmm, <c>Console.Beep</c>) no
/// son mockeables sin un framework de mocking (que este proyecto no usa), así que estos tests prueban el
/// núcleo de decisión inyectando los tres eslabones como <see cref="System.Func{T,TResult}"/>/
/// <see cref="System.Func{TResult}"/> fake — el mismo patrón de "parámetro inyectable solo para tests" que
/// <c>SupabaseRestClientTests</c> usa con <c>HttpMessageHandler</c>.</summary>
public class NotificationSoundPlayerTests
{
    [Fact]
    public void VolumeZero_NeverAttemptsAnyBackend()
    {
        var packagedAssetCalled = false;
        var systemSoundCalled = false;
        var beepCalled = false;

        var played = NotificationSoundPlayer.TryPlay(0,
            _ => { packagedAssetCalled = true; return true; },
            () => { systemSoundCalled = true; return true; },
            () => { beepCalled = true; return true; });

        Assert.False(played);
        Assert.False(packagedAssetCalled);
        Assert.False(systemSoundCalled);
        Assert.False(beepCalled);
    }

    [Fact]
    public void PackagedAssetSucceeds_StopsThereWithoutTryingFallbacks()
    {
        var systemSoundCalled = false;
        var beepCalled = false;

        var played = NotificationSoundPlayer.TryPlay(70,
            _ => true,
            () => { systemSoundCalled = true; return true; },
            () => { beepCalled = true; return true; });

        Assert.True(played);
        Assert.False(systemSoundCalled);
        Assert.False(beepCalled);
    }

    [Fact]
    public void PackagedAssetFails_FallsBackToSystemSound()
    {
        var beepCalled = false;

        var played = NotificationSoundPlayer.TryPlay(70,
            _ => false,
            () => true,
            () => { beepCalled = true; return true; });

        Assert.True(played);
        Assert.False(beepCalled);
    }

    [Fact]
    public void PackagedAssetAndSystemSoundFail_FallsBackToBeep()
    {
        var played = NotificationSoundPlayer.TryPlay(70,
            _ => false,
            () => false,
            () => true);

        Assert.True(played);
    }

    [Fact]
    public void AllBackendsFail_ReturnsFalseInsteadOfThrowing()
    {
        var played = NotificationSoundPlayer.TryPlay(70,
            _ => false,
            () => false,
            () => false);

        Assert.False(played);
    }

    [Fact]
    public void ExceptionInABackend_IsTreatedAsFailureAndFallsThrough()
    {
        var played = NotificationSoundPlayer.TryPlay(70,
            _ => throw new System.InvalidOperationException("no hay reproductor en esta máquina"),
            () => throw new System.IO.IOException("comando del SO no encontrado"),
            () => true);

        Assert.True(played);
    }
}
