using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace enki.Services;

/// <summary>Cronómetro del arranque: cada hito de la puesta en marcha deja una línea en el log con el tiempo
/// transcurrido desde el principio del proceso y lo que costó ese tramo.
///
/// <para>Existe porque un arranque solo se puede mejorar midiéndolo, y lo que se mide mal se optimiza al
/// azar. Sin esto, «tarda mucho en abrir» no dice si el tiempo se va en inflar el XAML de la ventana, en
/// construir los servicios, en leer el vault o en dibujar el primer fotograma — y cada uno de esos cuatro se
/// arregla de una manera distinta. Las marcas van a nivel Debug (Ajustes › General › Registro de
/// depuración), así que no ensucian el log de un usuario normal.</para>
///
/// <para>Las marcas se acumulan hasta que el registro de depuración está activo y entonces se vuelcan todas
/// de golpe, en orden. El nivel de depuración no se conoce hasta que <c>SettingsService</c> ha leído
/// <c>settings.json</c>, y eso ocurre dentro del ViewModel de la shell — así que sin esta cola las marcas que
/// fechan justo la parte más lenta del arranque (inicializar la plataforma, inflar la ventana, construir los
/// servicios) serían precisamente las que nunca se llegarían a ver.</para></summary>
public static class StartupTrace
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static readonly List<string> Pending = [];

    private static long _lastMilliseconds;
    private static bool _flushed = true;

    /// <summary>Reinicia el cronómetro. Lo llama <c>Program.Main</c> como primera instrucción del proceso,
    /// que es el único punto en el que «desde el principio» significa algo.</summary>
    public static void Start()
    {
        Clock.Restart();
        _lastMilliseconds = 0;
    }

    /// <summary>Registra un hito del arranque. El delta es tan importante como el total: un arranque que
    /// suma 3 s repartidos en seis tramos de 500 ms hay que arreglarlo de otra manera que uno que se los come
    /// en uno solo, y el que se arregla es el tramo, no el total.</summary>
    public static void Mark(string stage)
    {
        var elapsed = Clock.ElapsedMilliseconds;
        var delta = elapsed - _lastMilliseconds;
        _lastMilliseconds = elapsed;
        var line = $"[arranque] {stage}: {elapsed} ms (+{delta} ms)";

        lock (Gate)
        {
            if (!LogService.DebugEnabled)
            {
                // Todavía no se sabe si el usuario tiene el registro de depuración activo: se guarda sin más.
                Pending.Add(line);
                _flushed = false;
                return;
            }

            FlushUnderLock();
            LogService.Debug(line);
        }
    }

    /// <summary>Escribe de una vez lo que se acumuló antes de que el nivel de depuración estuviera
    /// decidido. Se llama desde <see cref="Mark"/> y solo una vez: en cuanto el volcado ocurre, las marcas
    /// siguientes ya salen directas.</summary>
    private static void FlushUnderLock()
    {
        if (_flushed) return;
        _flushed = true;
        foreach (var line in Pending) LogService.Debug(line);
        Pending.Clear();
    }
}
