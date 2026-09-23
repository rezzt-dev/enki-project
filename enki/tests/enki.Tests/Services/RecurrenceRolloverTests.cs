using System;
using System.IO;
using System.Text.Json;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre el avance acotado de recurrencias
/// (<see cref="PolarTaskService.AdvanceUntilAfter"/>) y el saneado del enum al cargar.
///
/// <para>Lo que había antes eran dos bucles <c>while (next &lt;= now)</c> sin tope, en
/// <c>PolarTaskService.RollRecurringTasks</c> y en <c>ReminderService.Fire</c>, apoyados en que
/// <see cref="PolarTaskService.CalculateNextDueDate"/> siempre avanza. No siempre avanza: su rama por
/// defecto devuelve la fecha sin tocar, y <c>System.Text.Json</c> deserializa un entero cualquiera a un
/// <c>enum</c> sin quejarse. Un <c>"Recurrence": 99</c> en el JSON bastaba para colgar la app al cargar o
/// al sincronizar, sin salida desde la interfaz (.docs/auditoria-seguridad-y-fallos-criticos.md,
/// BUG-01). Los tests con un valor fuera de rango son, literalmente, los que antes no terminaban.</para></summary>
public class RecurrenceRolloverTests : IDisposable
{
    /// <summary>Valor que no existe en <see cref="PolarRecurrence"/> — lo que deja en memoria un archivo
    /// dañado, editado a mano o escrito por una versión con más opciones de las que esta conoce.</summary>
    private const PolarRecurrence UnknownRecurrence = (PolarRecurrence)99;

    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public RecurrenceRolloverTests() => Directory.CreateDirectory(_storageDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    [Fact]
    public void AdvanceUntilAfter_ReturnsTheFirstOccurrenceStrictlyAfterTheThreshold()
    {
        var start = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);

        var next = PolarTaskService.AdvanceUntilAfter(start, PolarRecurrence.Daily, now);

        Assert.NotNull(next);
        Assert.True(next > now);
        Assert.Equal(new DateTime(2026, 1, 11, 9, 0, 0, DateTimeKind.Utc), next!.Value);
    }

    /// <summary>Siempre avanza al menos una vez, aunque el punto de partida ya sea posterior al umbral —
    /// es el contrato que tenía el <c>do/while</c> de <c>ReminderService.Fire</c>.</summary>
    [Fact]
    public void AdvanceUntilAfter_AlwaysAdvancesAtLeastOnce()
    {
        var start = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var next = PolarTaskService.AdvanceUntilAfter(start, PolarRecurrence.Weekly, now);

        Assert.Equal(new DateTime(2026, 3, 8, 9, 0, 0, DateTimeKind.Utc), next);
    }

    /// <summary>El test que antes no terminaba.</summary>
    [Fact]
    public void AdvanceUntilAfter_WithAnUnknownRecurrence_GivesUpInsteadOfLoopingForever()
    {
        var start = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Null(PolarTaskService.AdvanceUntilAfter(start, UnknownRecurrence, now));
        Assert.Null(PolarTaskService.AdvanceUntilAfter(start, PolarRecurrence.None, now));
    }

    /// <summary>Una fecha pegada a <see cref="DateTime.MaxValue"/> desborda al sumarle el intervalo; no
    /// hay siguiente ocurrencia posible, y eso no puede ser una excepción que suba hasta la UI.</summary>
    [Fact]
    public void AdvanceUntilAfter_NearMaxValue_ReturnsNullInsteadOfThrowing()
    {
        var next = PolarTaskService.AdvanceUntilAfter(
            DateTime.MaxValue.AddDays(-1), PolarRecurrence.Weekly, DateTime.MaxValue.AddDays(-2));

        Assert.Null(next);
    }

    [Fact]
    public void RollRecurringTasks_WithAnUnknownRecurrence_LeavesTheTaskAloneInsteadOfHanging()
    {
        var service = new PolarTaskService(_storageDirectory);
        var list = service.CreateList("Lista", "Folder", "#FFFFFF", isDependencyChain: false);
        var task = service.CreateTask(list, "Tarea", string.Empty, priority: 1, dueDateUtc: null,
            PolarRecurrence.None, timeEstimate: 0, tags: []);
        task.Recurrence = UnknownRecurrence;
        task.Completed = true;
        task.DueDateUtc = DateTime.UtcNow.AddDays(-30);
        service.Save();

        var changed = service.RollRecurringTasks();

        Assert.False(changed);
        Assert.True(task.Completed);
    }

    [Fact]
    public void RollRecurringTasks_StillAdvancesAKnownRecurrencePastNow()
    {
        var service = new PolarTaskService(_storageDirectory);
        var list = service.CreateList("Lista", "Folder", "#FFFFFF", isDependencyChain: false);
        var task = service.CreateTask(list, "Tarea", string.Empty, priority: 1, dueDateUtc: null,
            PolarRecurrence.None, timeEstimate: 0, tags: []);
        task.Recurrence = PolarRecurrence.Daily;
        task.Completed = true;
        task.DueDateUtc = DateTime.UtcNow.AddDays(-10);
        service.Save();

        Assert.True(service.RollRecurringTasks());
        Assert.False(task.Completed);
        Assert.True(task.DueDateUtc > DateTime.UtcNow);
    }

    /// <summary>Un recordatorio recurrente que no se puede avanzar degrada a "una sola vez" en vez de
    /// colgar el temporizador que lo dispara.</summary>
    [Fact]
    public void CollectDue_WithAnUnknownRecurrence_FiresOnceInsteadOfHanging()
    {
        var service = new ReminderService(_storageDirectory);
        var reminder = service.Create("Recordatorio", string.Empty, DateTime.UtcNow.AddMinutes(-5), "Bell",
            recurrence: UnknownRecurrence);

        var due = service.PopDueReminders();

        Assert.Contains(due, r => r.Id == reminder.Id);
        Assert.True(reminder.IsTriggered);
    }

    [Fact]
    public void CollectDue_WithAKnownRecurrence_ReschedulesInsteadOfTriggering()
    {
        var service = new ReminderService(_storageDirectory);
        var reminder = service.Create("Recordatorio", string.Empty, DateTime.UtcNow.AddDays(-3), "Bell",
            recurrence: PolarRecurrence.Daily);

        service.PopDueReminders();

        Assert.False(reminder.IsTriggered);
        Assert.True(reminder.NotifyAtUtc > DateTime.UtcNow);
    }

    /// <summary>Además del corte en el bucle, el valor imposible se repara al cargar, para que ningún
    /// <c>switch</c> del resto de la app lo vea (el formateador de recurrencias, por ejemplo, lo pintaría
    /// como una etiqueta vacía).</summary>
    [Fact]
    public void Load_NormalizesAnOutOfRangeRecurrenceFromDisk()
    {
        var path = Path.Combine(_storageDirectory, "reminders.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new[]
        {
            new Reminder
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = "Heredado",
                NotifyAtUtc = DateTime.UtcNow.AddDays(1),
                Recurrence = UnknownRecurrence
            }
        }));

        var service = new ReminderService(_storageDirectory);

        Assert.Equal(PolarRecurrence.None, Assert.Single(service.ActiveReminders).Recurrence);
    }
}
