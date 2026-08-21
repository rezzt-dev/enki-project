using System;
using System.IO;
using System.Linq;

namespace enki.Services;

/// <summary>Resuelve la carpeta base donde Enki persiste su estado (<c>settings.json</c>, <c>kanban.json</c>,
/// <c>polar-tasks.json</c>, etc. — ver <see cref="SettingsService"/> y el resto de servicios que aceptan un
/// <c>storageDirectory</c> opcional). En un build de Debug compilado desde el código fuente (<c>dotnet run</c>,
/// F5 del IDE) usa una carpeta dentro del propio proyecto (<c>src/enki/.devdata/</c>, en <c>.gitignore</c>)
/// en vez de <c>%AppData%/Enki</c> — así una copia recién clonada arranca siempre en blanco (sin vault, sin
/// cuenta Polar vinculada, sin históricos de pruebas anteriores) y cualquier estado de desarrollo se resetea
/// borrando esa carpeta, sin pisar ni mezclar datos con una instalación Release real de Enki que use la misma
/// máquina (que sigue usando <c>%AppData%/Enki</c> sin cambios).</summary>
internal static class AppDataDirectory
{
    private const string DevDataFolderName = ".devdata";

    public static string Resolve()
    {
#if DEBUG
        var devDataDirectory = FindDevDataDirectory();
        if (devDataDirectory is not null) return devDataDirectory;
#endif
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Enki");
    }

#if DEBUG
    /// <summary>Sube desde la carpeta del ejecutable (<c>bin/Debug/net8.0/...</c>) hasta encontrar
    /// <c>enki.csproj</c> — así funciona sin importar el target framework o si se ejecuta desde el IDE o la
    /// terminal. Si por lo que sea no lo encuentra (build publicado sin el .csproj al lado, por ejemplo),
    /// cae de vuelta a <c>%AppData%/Enki</c> en vez de fallar.</summary>
    private static string? FindDevDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; dir is not null && i < 10; i++)
        {
            if (dir.EnumerateFiles("enki.csproj").Any())
                return Path.Combine(dir.FullName, DevDataFolderName);
            dir = dir.Parent;
        }
        return null;
    }
#endif
}
