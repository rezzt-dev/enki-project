using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace enki.Tests.Services;

public sealed class KanbanBoardScaleTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-board-scale-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(100)]
    public void LoadSelectAndLocate_OnlyBuildsVisibleBoard(int count)
    {
        Directory.CreateDirectory(_directory);
        var store = new KanbanStore();
        for (var b = 0; b < count; b++)
        {
            var board = new KanbanBoard { Name = $"Board {b}" };
            var column = new KanbanColumn { Id = $"column-{b}", Title = "Pending", Role = CoreColumnRole.Backlog };
            for (var c = 0; c < 100; c++) column.Cards.Add(new() { Id = $"card-{b}-{c}", Title = $"Task {c}" });
            board.Columns.Add(column);
            store.Boards.Add(board);
        }
        store.DefaultBoardId = store.Boards[0].Id;
        File.WriteAllText(Path.Combine(_directory, "kanban.json"), JsonSerializer.Serialize(store));
        var watch = Stopwatch.StartNew();
        var service = new KanbanService(_directory);
        var loadMs = watch.Elapsed.TotalMilliseconds;
        var settings = new SettingsService(_directory);
        var auth = new PolarAuthService(settings);
        var tasks = new PolarTaskService(_directory);
        var reminders = new ReminderService(_directory);
        var sync = new PolarSyncService(auth, new SupabaseRestClient(auth), tasks, reminders, settings);
        var vm = new KanbanViewModel(service, auth, new PolarTaskManagementViewModel(tasks, sync));
        watch.Restart();
        vm.SelectBoardCommand.Execute(vm.BoardOptions.Last());
        var selectMs = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(100, vm.Columns.Sum(c => c.Cards.Count));
        Assert.Equal(count * 100, service.AllColumns.Sum(c => c.Cards.Count));
        watch.Restart();
        for (var b = 0; b < count; b++) Assert.Equal($"Board {b}", service.FindCardLocation($"card-{b}-99")!.Board.Name);
        output.WriteLine($"Boards={count}; Cards={count * 100}; Load={loadMs:F2}ms; Select={selectMs:F2}ms; LocateAllBoards={watch.Elapsed.TotalMilliseconds:F2}ms");
    }
}
