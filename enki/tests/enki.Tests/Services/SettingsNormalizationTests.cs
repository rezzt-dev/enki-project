using System;
using System.IO;
using enki.Models;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="AppSettings.Normalize"/> y la parte de
/// <see cref="VaultSettings.Normalize"/> que acota el zoom.
///
/// <para>Los rangos solo los aplicaban los controles de Ajustes, nunca la lectura del archivo, así que un
/// <c>settings.json</c> dañado, traído de otra versión o importado desde "Importar ajustes" podía dejar
/// valores imposibles donde nadie los revisaba: un cuerpo de letra 0 que hace que Avalonia rechace la
/// fuente, o —el caso grave— un <c>UiZoomPercent</c> a 0, que escala toda la ventana a nada y deja la app
/// sin ningún control con el que volver a Ajustes a arreglarlo
/// (.docs/auditoria-seguridad-y-fallos-criticos.md, BUG-02).</para></summary>
public class SettingsNormalizationTests
{
    [Fact]
    public void Normalize_LeavesValidValuesUntouched()
    {
        var settings = new AppSettings
        {
            EditorFontSize = 16,
            EditorColumnWidth = 900,
            EditorTabSize = 2,
            ReminderSoonWindowDays = 14,
            NotificationSoundVolume = 0,     // el silencio es una elección válida, no un valor a reparar
            CalendarWorkingHoursStart = 7,
            CalendarWorkingHoursEnd = 15
        };

        settings.Normalize();

        Assert.Equal(16, settings.EditorFontSize);
        Assert.Equal(900, settings.EditorColumnWidth);
        Assert.Equal(2, settings.EditorTabSize);
        Assert.Equal(14, settings.ReminderSoonWindowDays);
        Assert.Equal(0, settings.NotificationSoundVolume);
        Assert.Equal(7, settings.CalendarWorkingHoursStart);
        Assert.Equal(15, settings.CalendarWorkingHoursEnd);
    }

    /// <summary>Un cero donde el mínimo es mayor que cero es el <c>default(T)</c> de un archivo anterior a
    /// esa propiedad, no una elección: vuelve al valor por defecto, no al mínimo.</summary>
    [Fact]
    public void Normalize_TreatsAMissingPropertyAsItsDefault()
    {
        var settings = new AppSettings
        {
            EditorFontSize = 0,
            EditorColumnWidth = 0,
            EditorTabSize = 0,
            ReminderSoonWindowDays = 0,
            AutoBackupIntervalDays = 0,
            AutoBackupRetentionCount = 0
        };

        settings.Normalize();

        Assert.Equal(14, settings.EditorFontSize);
        Assert.Equal(760, settings.EditorColumnWidth);
        Assert.Equal(4, settings.EditorTabSize);
        Assert.Equal(7, settings.ReminderSoonWindowDays);
        Assert.Equal(1, settings.AutoBackupIntervalDays);
        Assert.Equal(5, settings.AutoBackupRetentionCount);
    }

    [Fact]
    public void Normalize_ClampsValuesOutsideTheRangeTheUiOffers()
    {
        var settings = new AppSettings
        {
            EditorFontSize = 100_000,
            EditorColumnWidth = -1,
            EditorTabSize = 99,
            ReminderSoonWindowDays = 5_000,
            NotificationSoundVolume = 900,
            AutoBackupRetentionCount = -3
        };

        settings.Normalize();

        Assert.Equal(20, settings.EditorFontSize);
        Assert.Equal(480, settings.EditorColumnWidth);
        Assert.Equal(8, settings.EditorTabSize);
        Assert.Equal(30, settings.ReminderSoonWindowDays);
        Assert.Equal(100, settings.NotificationSoundVolume);
        Assert.Equal(1, settings.AutoBackupRetentionCount);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Normalize_RepairsANonFiniteDouble(double value)
    {
        var settings = new AppSettings { EditorFontSize = value };

        settings.Normalize();

        Assert.Equal(14, settings.EditorFontSize);
    }

    /// <summary>Dos horas individualmente válidas pero cruzadas dejan el calendario sin ninguna hora
    /// laboral que pintar, así que la pareja se repara entera.</summary>
    [Fact]
    public void Normalize_RepairsCrossedWorkingHours()
    {
        var settings = new AppSettings { CalendarWorkingHoursStart = 20, CalendarWorkingHoursEnd = 6 };

        settings.Normalize();

        Assert.Equal(9, settings.CalendarWorkingHoursStart);
        Assert.Equal(18, settings.CalendarWorkingHoursEnd);
    }

    /// <summary>El caso sin salida: a 0 no queda un solo control con el que volver a Ajustes.</summary>
    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 50)]
    [InlineData(-200, 50)]
    [InlineData(100_000, 200)]
    [InlineData(150, 150)]
    public void Normalize_KeepsTheUiZoomWithinAUsableRange(int stored, int expected)
    {
        var settings = new VaultSettings { UiZoomPercent = stored };

        settings.Normalize();

        Assert.Equal(expected, settings.UiZoomPercent);
    }

    /// <summary>La ruta que de verdad importa: lo que llega del disco, no lo que construye un test. Un
    /// settings.json con números imposibles tiene que producir un servicio con valores usables.</summary>
    [Fact]
    public void SettingsService_NormalizesWhatItReadsFromDisk()
    {
        var storageDirectory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storageDirectory);
        try
        {
            File.WriteAllText(Path.Combine(storageDirectory, "settings.json"),
                """{ "EditorFontSize": 0, "EditorTabSize": 4000, "CalendarWorkingHoursStart": 99 }""");

            var service = new enki.Services.SettingsService(storageDirectory, new TestPolarCredentialStore());

            Assert.Equal(14, service.Settings.EditorFontSize);
            Assert.Equal(8, service.Settings.EditorTabSize);
            Assert.InRange(service.Settings.CalendarWorkingHoursStart, 0, 23);
            Assert.True(service.Settings.CalendarWorkingHoursEnd > service.Settings.CalendarWorkingHoursStart);
        }
        finally
        {
            if (Directory.Exists(storageDirectory)) Directory.Delete(storageDirectory, recursive: true);
        }
    }

    /// <summary>"Importar ajustes" es la entrada menos confiable de todas: un archivo que el usuario elige
    /// en un diálogo y que pudo escribir cualquiera.</summary>
    [Fact]
    public void ImportPreferences_NormalizesTheImportedValues()
    {
        var storageDirectory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storageDirectory);
        try
        {
            var service = new enki.Services.SettingsService(storageDirectory, new TestPolarCredentialStore());

            service.ImportPreferences("""{ "EditorFontSize": -5, "EditorColumnWidth": 99999 }""");

            // Un valor negativo no es "la propiedad no venía en el archivo", es una elección imposible:
            // se acota al extremo más cercano del rango, no al valor por defecto.
            Assert.Equal(12, service.Settings.EditorFontSize);
            Assert.Equal(1400, service.Settings.EditorColumnWidth);
        }
        finally
        {
            if (Directory.Exists(storageDirectory)) Directory.Delete(storageDirectory, recursive: true);
        }
    }
}
