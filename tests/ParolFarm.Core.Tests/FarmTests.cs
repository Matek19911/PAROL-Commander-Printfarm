using ParolFarm.Core.Farm;
using ParolFarm.Core.Printers;
using Xunit;

namespace ParolFarm.Core.Tests;

public class BambuParserTests
{
    [Fact]
    public void MergesIncrementalReports_OldFirmware()
    {
        var p = new BambuStatusParser();
        p.Apply("""{"print":{"gcode_state":"RUNNING","bed_temper":55.2,"bed_target_temper":55,"mc_percent":40,"layer_num":12,"total_layer_num":80,"command":"push_status"}}""");
        p.Apply("""{"print":{"mc_percent":41,"command":"push_status"}}""");
        var s = p.Build(true, DateTime.UtcNow);
        Assert.Equal("RUNNING", s.GcodeState);
        Assert.Equal(55.2, s.BedTemp!.Value, 3);
        Assert.Equal(41, s.Progress);
        Assert.Equal(12, s.Layer);
        Assert.True(s.IsPrinting);
    }

    [Fact]
    public void ReadsPackedBedTemperature_NewFirmware()
    {
        var p = new BambuStatusParser();
        // Soll 100 (oberes Wort), Ist 36 (unteres Wort)
        long packed = (100L << 16) | 36;
        p.Apply("{\"print\":{\"gcode_state\":\"FINISH\",\"device\":{\"bed\":{\"info\":{\"temp\":" + packed + "}}}}}");
        var s = p.Build(true, DateTime.UtcNow);
        Assert.Equal(36, s.BedTemp);
        Assert.Equal(100, s.BedTarget);
        Assert.True(s.IsFinished);
    }

    [Fact]
    public void ProjectFileCommand_UsesFtpUrlAndPlate()
    {
        var json = BambuCommands.ProjectFile(PrinterKind.X2D, "teil.3mf", 2, false, true);
        Assert.Contains("\"url\":\"ftp:///teil.3mf\"", json);
        Assert.Contains("\"param\":\"Metadata/plate_2.gcode\"", json);
        Assert.Contains("\"command\":\"project_file\"", json);
        var g = BambuCommands.Gcode("G90\r\nG1 Z200 F1200\n\n");
        Assert.Contains("\"param\":\"G90\\nG1 Z200 F1200\\n\"", g);
    }
}

public class FarmCoordinatorTests
{
    sealed class FakeRemoval : IRemovalService
    {
        public int Calls;
        public RemovalOutcome Next = new(RemovalResult.Done);
        public async Task<RemovalOutcome> RemoveAsync(PrinterConfig printer, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(100, ct);
            return Next;
        }
    }

    static async Task WaitFor(Func<bool> cond, int ms = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    static (FarmCoordinator, List<SimulatedPrinterConnection>, FakeRemoval) Create(params PrinterConfig[] printers)
    {
        var sims = new List<SimulatedPrinterConnection>();
        var removal = new FakeRemoval();
        var farm = new FarmCoordinator(cfg =>
        {
            var sim = new SimulatedPrinterConnection(cfg)
            {
                PrintDuration = TimeSpan.FromMilliseconds(600),
                CoolingRate = 200,
                Tick = TimeSpan.FromMilliseconds(20),
            };
            sims.Add(sim);
            return sim;
        }, removal)
        { TickInterval = TimeSpan.FromMilliseconds(20) };
        return (farm, sims, removal);
    }

    static string TempFile()
    {
        var f = Path.Combine(Path.GetTempPath(), $"teil-{Guid.NewGuid():N}.3mf");
        File.WriteAllText(f, "x");
        return f;
    }

    [Fact]
    public async Task RobotPrinter_PrintsQuantity_WithRemovalBetween()
    {
        var cfg = new PrinterConfig { Name = "X2D", Kind = PrinterKind.Simulation, HasRobot = true, ReleaseTemperatureC = 28, BedMoveWaitSeconds = 0 };
        var (farm, sims, removal) = Create(cfg);
        await using var _ = farm;
        var file = TempFile();
        var job = new FarmJob { FilePath = file, Quantity = 3 };
        farm.AddJob(job);
        await farm.SetPrintersAsync(new[] { cfg });

        await WaitFor(() => farm.Snapshot()[0].State == SlotState.CheckPlate);
        farm.Start();
        await Task.Delay(200);
        Assert.Equal(SlotState.CheckPlate, farm.Snapshot()[0].State); // ohne Bestätigung kein Druck
        farm.ConfirmPlateEmpty(cfg.Id);

        await WaitFor(() => job.Completed == 3 && farm.Snapshot()[0].State == SlotState.Ready, 20000);
        Assert.Equal(3, removal.Calls);
        Assert.Single(sims[0].Uploaded); // gleiche Datei nur einmal hochgeladen
        Assert.Equal(3, sims[0].SentGcode.Count); // Bett runter nach jedem Druck
        File.Delete(file);
    }

    [Fact]
    public async Task PrinterWithoutRobot_WaitsForOperator()
    {
        var cfg = new PrinterConfig { Name = "P1P", Kind = PrinterKind.Simulation, HasRobot = false };
        var (farm, _, removal) = Create(cfg);
        await using var _f = farm;
        var file = TempFile();
        farm.AddJob(new FarmJob { FilePath = file, Quantity = 2 });
        await farm.SetPrintersAsync(new[] { cfg });
        await WaitFor(() => farm.Snapshot()[0].State == SlotState.CheckPlate);
        farm.Start();
        farm.ConfirmPlateEmpty(cfg.Id);
        await WaitFor(() => farm.Snapshot()[0].State == SlotState.WaitingForOperator);
        await Task.Delay(200);
        Assert.Equal(SlotState.WaitingForOperator, farm.Snapshot()[0].State);
        Assert.Equal(0, removal.Calls);
        farm.ConfirmPlateEmpty(cfg.Id);
        await WaitFor(() => farm.GetJobs()[0].Completed == 2);
        File.Delete(file);
    }

    [Fact]
    public async Task FailedPrint_GoesToError_AndJobIsNotCounted()
    {
        var cfg = new PrinterConfig { Name = "X2D", Kind = PrinterKind.Simulation, HasRobot = true, BedMoveWaitSeconds = 0 };
        var (farm, sims, removal) = Create(cfg);
        await using var _f = farm;
        var file = TempFile();
        var job = new FarmJob { FilePath = file, Quantity = 1 };
        farm.AddJob(job);
        await farm.SetPrintersAsync(new[] { cfg });
        await WaitFor(() => farm.Snapshot()[0].State == SlotState.CheckPlate);
        sims[0].FailNextPrint = true;
        farm.Start();
        farm.ConfirmPlateEmpty(cfg.Id);
        await WaitFor(() => farm.Snapshot()[0].State == SlotState.Error);
        Assert.Equal(0, job.Completed);
        Assert.Equal(1, job.Remaining);
        Assert.Equal(0, removal.Calls);
        File.Delete(file);
    }

    [Fact]
    public async Task RobotNotReady_IsRetried()
    {
        var cfg = new PrinterConfig { Name = "X2D", Kind = PrinterKind.Simulation, HasRobot = true, BedMoveWaitSeconds = 0 };
        var (farm, _, removal) = Create(cfg);
        await using var _f = farm;
        var file = TempFile();
        farm.AddJob(new FarmJob { FilePath = file, Quantity = 1 });
        removal.Next = new RemovalOutcome(RemovalResult.NotReady, "nicht freigegeben");
        await farm.SetPrintersAsync(new[] { cfg });
        await WaitFor(() => farm.Snapshot()[0].State == SlotState.CheckPlate);
        farm.Start();
        farm.ConfirmPlateEmpty(cfg.Id);
        await WaitFor(() => removal.Calls >= 1);
        await WaitFor(() => farm.Snapshot()[0].State == SlotState.Cooling);
        Assert.Contains("nicht bereit", farm.Snapshot()[0].Message);
        File.Delete(file);
    }

    [Fact]
    public async Task StoppedFarm_DoesNotStartNewPrints()
    {
        var cfg = new PrinterConfig { Name = "X2D", Kind = PrinterKind.Simulation, HasRobot = true };
        var (farm, sims, _) = Create(cfg);
        await using var _f = farm;
        var file = TempFile();
        farm.AddJob(new FarmJob { FilePath = file, Quantity = 1 });
        await farm.SetPrintersAsync(new[] { cfg });
        await WaitFor(() => farm.Snapshot()[0].State == SlotState.CheckPlate);
        farm.ConfirmPlateEmpty(cfg.Id);
        await Task.Delay(300);
        Assert.Equal(SlotState.Ready, farm.Snapshot()[0].State);
        Assert.Empty(sims[0].Uploaded);
        File.Delete(file);
    }
}
