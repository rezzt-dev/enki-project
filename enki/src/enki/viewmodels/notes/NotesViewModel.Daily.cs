using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Documentos › nota diaria: abrir la de hoy o la de cualquier otro día y el calendario de
/// constancia que las resume (propuesta 8, docs/project-info/propuesta-nuevas-funcionalidades.md §8).
/// Partial aparte de <c>NotesViewModel.cs</c> por el mismo criterio que <c>NotesViewModel.Templates.cs</c>:
/// es un flujo completo con su propio estado de interfaz.
///
/// <para>La lógica de qué es la nota diaria de un día —cómo se llama, dónde está, cómo nace de la
/// plantilla— vive entera en <see cref="DailyNoteService"/>; aquí solo está lo que es propio de la pantalla
/// de Documentos: a qué carpeta van las notas que se crean, cuándo hay que recargar la rejilla y cómo se
/// proyecta el calendario.</para>
///
/// <para><b>Constancia, no gamificación.</b> El calendario dice cuántos días tienen nota y cuáles, y nada
/// más: ni rachas, ni puntos, ni objetivos, ni un color que "premie" un mes completo. La fila de hábitos
/// que lo acompaña es la misma lectura sobre los datos reales de <see cref="HabitService"/> —sin copiarlos
/// a ningún almacén propio— para que las dos constancias se lean juntas, y desde ahí se salta a Hábitos o a
/// Estadísticas, que es donde cada una se gestiona y se analiza a fondo.</para></summary>
public sealed partial class NotesViewModel
{
    /// <summary>Ancho del calendario de constancia: diez semanas. Mismo tamaño de ventana que la
    /// cuadrícula de Hábitos (<c>HabitsViewModel.ConsistencyWeeks</c>), para que las dos se lean como la
    /// misma medida de "los últimos dos meses y pico" y la fila de hábitos de este panel coincida con lo
    /// que muestra aquella pantalla.</summary>
    private const int ConsistencyWeeks = 10;

    public ObservableCollection<DailyConsistencyDayViewModel> ConsistencyDays { get; } = [];

    /// <summary>La misma ventana de días vista desde los hábitos activos, o vacía si el vault no tiene
    /// ninguno — reutiliza <see cref="ConsistencyDayViewModel"/> (el tipo compartido con Hábitos, el
    /// Dashboard y Estadísticas) en vez de un segundo modelo de celda con las mismas tres propiedades.</summary>
    public ObservableCollection<ConsistencyDayViewModel> HabitConsistencyDays { get; } = [];

    /// <summary>Iniciales de los días de la semana, en el orden que dicte el idioma activo (lunes primero
    /// en español, domingo en inglés de EE. UU.) — la columna que etiqueta las filas del calendario.</summary>
    public ObservableCollection<string> ConsistencyWeekdayLabels { get; } = [];

    [ObservableProperty]
    private bool _isConsistencyOpen;

    /// <summary>"24 de 70 días con nota" — el único dato numérico del panel, deliberadamente descriptivo
    /// (cuántos días, sobre cuántos) en vez de un porcentaje de cumplimiento o una racha.</summary>
    [ObservableProperty]
    private string _consistencySummary = string.Empty;

    [ObservableProperty]
    private string _consistencyHabitsSummary = string.Empty;

    /// <summary>Si la fila de hábitos tiene algo que decir: hay al menos un hábito activo en el vault. Sin
    /// hábitos, el panel se queda solo con las notas diarias en vez de enseñar una fila vacía que invite a
    /// preguntarse qué falta.</summary>
    [ObservableProperty]
    private bool _hasHabitConsistency;

    /// <summary>Si el panel puede ofrecer el salto a Hábitos / Estadísticas — falso en los tests y en
    /// cualquier composición que construya este viewmodel sin esos callbacks.</summary>
    public bool CanOpenHabits => _onOpenHabits is not null;

    public bool CanOpenStatistics => _onOpenStatistics is not null;

    /// <summary>Dónde nace una nota diaria que aún no existe: la misma carpeta en la que nacería una nota
    /// nueva o una creada desde plantilla (<see cref="TemplateTargetFolder"/>), para que las tres vías no
    /// dejen las notas en sitios distintos.</summary>
    private string DailyNoteTargetFolder => TemplateTargetFolder;

    // ===================== Abrir la nota del día =====================

    /// <summary>Abre la nota de hoy, creándola si aún no existe. Con una plantilla configurada para la nota
    /// diaria (Ajustes › Editor › Plantillas), es esa plantilla la que decide tanto el nombre de la nota
    /// —si declara <c>title</c>— como su contenido inicial; sin plantilla, sigue siendo la fecha
    /// <c>yyyy-MM-dd</c> con un encabezado.
    ///
    /// <para>La plantilla solo interviene al <b>crear</b>: si la nota del día ya existe se abre tal cual,
    /// porque volver a aplicarla pisaría lo que el usuario lleve escrito hoy. Para eso está la acción
    /// "combinar con la plantilla" del editor (<see cref="EditorViewModel.MergeDailyTemplateCommand"/>),
    /// que solo añade lo que falta.</para></summary>
    [RelayCommand]
    private void OpenDailyNote() => OpenDailyNoteFor(DailyNoteService.Today);

    /// <summary>Abre la nota diaria de un día concreto, creándola si no existe — el destino de la
    /// navegación día anterior / día siguiente del editor y del clic en una celda del calendario. Internal
    /// para que <see cref="MainWindowViewModel"/> pueda cablearla al editor sin exponer un comando más.</summary>
    internal void OpenDailyNoteFor(DateOnly date)
    {
        DailyNoteOpening opening;
        try
        {
            opening = _dailyNoteService.OpenOrCreate(date, DailyNoteTargetFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            NotifyVaultWriteFailed(ex);
            return;
        }

        if (opening.Created)
        {
            Reload();
            // El calendario acaba de quedarse corto en un día: si está abierto, que lo refleje sin que el
            // usuario tenga que cerrarlo y volver a abrirlo.
            if (IsConsistencyOpen) ReloadConsistency();
        }

        OpenNote(opening.Note, opening.Created ? opening.CaretOffset : null);
    }

    /// <summary>Atajos de teclado de la navegación diaria (Ajustes › Atajos, remapeables). Viven aquí y no
    /// en <see cref="EditorViewModel"/> porque los <c>KeyBinding</c> se montan sobre <c>NotesView</c>, que
    /// es quien tiene el foco de la página; reenvían a la pestaña activa y son un no-op silencioso cuando
    /// esa pestaña no es una nota diaria —igual que el resto de atajos de pestañas cuando no hay ninguna
    /// abierta.</summary>
    [RelayCommand]
    private void GoToPreviousDay() => ActiveEditor?.OpenPreviousDayCommand.Execute(null);

    [RelayCommand]
    private void GoToNextDay() => ActiveEditor?.OpenNextDayCommand.Execute(null);

    // ===================== Calendario de constancia =====================

    [RelayCommand]
    private void ToggleConsistency()
    {
        if (IsConsistencyOpen)
        {
            IsConsistencyOpen = false;
            return;
        }

        ReloadConsistency();
        IsConsistencyOpen = true;
    }

    [RelayCommand]
    private void CloseConsistency() => IsConsistencyOpen = false;

    /// <summary>Abre la nota diaria de la celda pulsada. Los días futuros que la rejilla dibuja para
    /// completar la última semana no llegan aquí (no son pulsables), pero se comprueba igualmente: el
    /// comando también es accesible desde el teclado y desde un lector de pantalla.</summary>
    [RelayCommand]
    private void OpenConsistencyDay(DailyConsistencyDayViewModel? day)
    {
        if (day is null || !day.IsInteractive) return;
        IsConsistencyOpen = false;
        OpenDailyNoteFor(day.Date);
    }

    [RelayCommand]
    private void OpenHabitsFromConsistency()
    {
        IsConsistencyOpen = false;
        _onOpenHabits?.Invoke();
    }

    [RelayCommand]
    private void OpenStatisticsFromConsistency()
    {
        IsConsistencyOpen = false;
        _onOpenStatistics?.Invoke();
    }

    /// <summary>Recalcula el calendario entero: una pasada por el vault para saber qué días tienen nota
    /// (<see cref="DailyNoteService.FindInRange"/>) y, si hay hábitos, otra por sus marcas. Se llama al
    /// abrir el panel y cuando algo que ve el panel cambia (una nota diaria recién creada), no de forma
    /// continua: es una foto, como el resto de proyecciones de lectura de la app.</summary>
    internal void ReloadConsistency()
    {
        var today = DailyNoteService.Today;
        // La ventana termina al final de la semana en curso y no hoy: así la última columna es una semana
        // completa como las demás (sus días que aún no han llegado se pintan apagados) y hoy nunca queda
        // fuera del calendario.
        var end = StartOfWeek(today).AddDays(6);
        var start = end.AddDays(-(ConsistencyWeeks * 7 - 1));

        var notesByDate = _dailyNoteService.FindInRange(start, end);
        var habits = _habitService?.Store.Habits.Where(habit => !habit.Archived).ToList() ?? [];

        ConsistencyDays.Clear();
        HabitConsistencyDays.Clear();
        var daysWithNote = 0;
        var habitDaysKept = 0;
        var habitDaysApplicable = 0;

        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var isFuture = date > today;
            var hasNote = notesByDate.ContainsKey(date);
            if (hasNote) daysWithNote++;

            ConsistencyDays.Add(new DailyConsistencyDayViewModel
            {
                Date = date,
                HasNote = hasNote,
                IsToday = date == today,
                IsFuture = isFuture
            });

            if (habits.Count == 0) continue;

            var (applicable, completed) = HabitDayTotals(habits, date, isFuture);
            var kept = applicable > 0 && completed == applicable;
            if (applicable > 0) habitDaysApplicable++;
            if (kept) habitDaysKept++;

            HabitConsistencyDays.Add(new ConsistencyDayViewModel
            {
                Date = date,
                IsDone = kept,
                IsApplicable = applicable > 0,
                Tooltip = HabitDayTooltip(date, applicable, completed, isFuture)
            });
        }

        ReloadWeekdayLabels(start);

        var totalPastDays = ConsistencyDays.Count(day => !day.IsFuture);
        ConsistencySummary = string.Format(CultureInfo.CurrentCulture,
            LocalizationService.Instance["Notes_Consistency_NotesSummary"], daysWithNote, totalPastDays);
        ConsistencyHabitsSummary = string.Format(CultureInfo.CurrentCulture,
            LocalizationService.Instance["Notes_Consistency_HabitsSummary"], habitDaysKept, habitDaysApplicable);
        HasHabitConsistency = habits.Count > 0 && habitDaysApplicable > 0;
    }

    /// <summary>Cuántos hábitos tocaban ese día y cuántos se marcaron, con la misma definición de "tocaba"
    /// que Hábitos y Estadísticas (<see cref="HabitService.IsApplicable"/>). Un día futuro no "toca"
    /// todavía: contarlo como pendiente pintaría de incumplimiento algo que aún no ha pasado.</summary>
    private static (int Applicable, int Completed) HabitDayTotals(List<Habit> habits, DateOnly date, bool isFuture)
    {
        if (isFuture) return (0, 0);

        var applicable = 0;
        var completed = 0;
        foreach (var habit in habits)
        {
            // Un hábito no cuenta antes de existir: si no, crear un hábito hoy dejaría diez semanas de
            // casillas "sin cumplir" a su espalda.
            if (date < DateOnly.FromDateTime(habit.CreatedUtc.ToLocalTime())) continue;
            if (!HabitService.IsApplicable(habit, date)) continue;

            applicable++;
            if (HabitService.IsCompleted(habit, date)) completed++;
        }

        return (applicable, completed);
    }

    private static string HabitDayTooltip(DateOnly date, int applicable, int completed, bool isFuture)
    {
        var dateLabel = date.ToString("ddd d MMM", CultureInfo.CurrentCulture);
        if (isFuture || applicable == 0)
            return string.Format(CultureInfo.CurrentCulture,
                LocalizationService.Instance["Notes_Consistency_HabitsTooltip_NotApplicable"], dateLabel);

        return string.Format(CultureInfo.CurrentCulture,
            LocalizationService.Instance["Notes_Consistency_HabitsTooltip"], dateLabel, completed, applicable);
    }

    private void ReloadWeekdayLabels(DateOnly start)
    {
        ConsistencyWeekdayLabels.Clear();
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        for (var offset = 0; offset < 7; offset++)
        {
            var name = names[(int)start.AddDays(offset).DayOfWeek];
            ConsistencyWeekdayLabels.Add(name.Length == 0 ? string.Empty : name[..1].ToUpper(CultureInfo.CurrentCulture));
        }
    }

    /// <summary>El primer día de la semana de <paramref name="date"/> según el idioma activo, para que cada
    /// columna del calendario sea una semana completa y cada fila, siempre el mismo día de la semana.</summary>
    private static DateOnly StartOfWeek(DateOnly date)
    {
        var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var delta = ((int)date.DayOfWeek - (int)firstDay + 7) % 7;
        return date.AddDays(-delta);
    }
}
