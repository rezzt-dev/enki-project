using Xunit;

namespace enki.UITests;

/// <summary>Groups every headless UI test class (<see cref="NotesViewUiTests"/>, <see cref="KanbanViewUiTests"/>,
/// <see cref="CalendarViewUiTests"/>) into one xUnit collection with parallelization disabled, so they all
/// run sequentially on the same worker thread. Avalonia's headless platform pins "the UI thread" to
/// whichever thread first calls <c>AppBuilder.SetupWithoutStarting</c> (see <see cref="HeadlessApp"/>) and
/// every later Avalonia call must happen on that exact same thread — xUnit's default parallel-by-class
/// execution would run each of these three classes on a different pool thread and break that invariant
/// (verified: it does, with either a hard "a different thread owns it" exception or a silently wrong
/// resource lookup). Other, non-UI test classes in the suite are unaffected and keep running in parallel as
/// before.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class AvaloniaHeadlessCollection
{
    public const string Name = "Avalonia Headless UI";
}
