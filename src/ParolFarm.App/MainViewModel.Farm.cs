using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ParolFarm.Core.Farm;
using ParolFarm.Core.Printers;
using ParolFarm.Core.Robot;
using ParolFarm.Core.Settings;

namespace ParolFarm.App;

public sealed partial class PrinterRow : ObservableObject
{
    public string Id { get; init; } = "";
    [ObservableProperty] string _name = "";
    [ObservableProperty] string _state = "";
    [ObservableProperty] string _message = "";
    [ObservableProperty] string _printerState = "";
    [ObservableProperty] string _bed = "";
    [ObservableProperty] string _job = "";
    [ObservableProperty] bool _isError;
    [ObservableProperty] bool _needsAction;
}

public sealed partial class JobRow : ObservableObject
{
    public FarmJob Model { get; }
    public JobRow(FarmJob m) => Model = m;
    [ObservableProperty] string _progress = "";
    [ObservableProperty] string _printer = "";
    public string FileName => Model.FileName;
    public int Plate => Model.Plate;
    public string Options => (Model.UseAms ? "AMS" : "ohne AMS") + (Model.BedLeveling ? ", Bettnivellierung" : "");
}

public sealed record PrinterChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Entnahme = Roboterprogramm des Druckers ausführen.</summary>
sealed class RobotRemovalService : IRemovalService
{
    readonly MainViewModel _vm;
    public RobotRemovalService(MainViewModel vm) => _vm = vm;
    public Task<RemovalOutcome> RemoveAsync(PrinterConfig printer, CancellationToken ct) => _vm.RunRemovalAsync(printer, ct);
}

public sealed partial class MainViewModel
{
    FarmCoordinator? _farm;
    FarmData _farmData = new();
    static string FarmPath => Path.Combine(AppSettings.DataDirectory, "farm.json");

    public ObservableCollection<PrinterRow> PrinterRows { get; } = new();
    public ObservableCollection<JobRow> JobRows { get; } = new();
    public ObservableCollection<PrinterConfig> PrinterConfigs { get; } = new();
    public ObservableCollection<PrinterChoice> PrinterChoices { get; } = new();
    public ObservableCollection<string> ProgramNames { get; } = new();
    public PrinterKind[] PrinterKinds { get; } = Enum.GetValues<PrinterKind>();

    [ObservableProperty] bool _farmRunning;
    [ObservableProperty] PrinterRow? _selectedPrinterRow;
    [ObservableProperty] JobRow? _selectedJob;

    // Auftrag anlegen
    [ObservableProperty] int _newJobPlate = 1;
    [ObservableProperty] int _newJobQuantity = 1;
    [ObservableProperty] PrinterChoice? _newJobPrinter;
    [ObservableProperty] bool _newJobUseAms;
    [ObservableProperty] bool _newJobBedLeveling = true;

    // Drucker bearbeiten
    [ObservableProperty] PrinterConfig? _selectedPrinterConfig;
    [ObservableProperty] string _edName = "";
    [ObservableProperty] PrinterKind _edKind;
    [ObservableProperty] string _edHost = "";
    [ObservableProperty] string _edSerial = "";
    [ObservableProperty] string _edAccessCode = "";
    [ObservableProperty] bool _edEnabled = true;
    [ObservableProperty] bool _edHasRobot;
    [ObservableProperty] string? _edRemovalProgram;
    [ObservableProperty] double _edReleaseTemp = 30;
    [ObservableProperty] string _edBedGcode = "";
    [ObservableProperty] int _edBedWait = 20;

    void InitFarm()
    {
        try { _farmData = FarmData.Load(FarmPath); }
        catch (Exception ex) { AddLog($"farm.json defekt: {ex.Message}"); _farmData = new FarmData(); }

        _farm = new FarmCoordinator(
            cfg => cfg.Kind == PrinterKind.Simulation
                ? new SimulatedPrinterConnection(cfg) { PrintDuration = TimeSpan.FromSeconds(40), CoolingRate = 0.5 }
                : new BambuPrinterConnection(cfg),
            new RobotRemovalService(this));
        _farm.Log += AddLog;
        _farm.JobsChanged += () => _ui.BeginInvoke(SaveFarm);
        _farm.SetJobs(_farmData.Jobs);
        _ = ReconnectPrintersAsync();

        RebuildPrinterConfigs();
        RebuildProgramNames();
        RebuildJobs();
        _farmTimer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(500),
            System.Windows.Threading.DispatcherPriority.Background, (_, _) => UpdateFarm(), _ui);
        _farmTimer.Start();
    }

    System.Windows.Threading.DispatcherTimer? _farmTimer;

    async Task ReconnectPrintersAsync()
    {
        if (_farm == null) return;
        try { await _farm.SetPrintersAsync(_farmData.Printers.ToList()); }
        catch (Exception ex) { AddLog($"Drucker verbinden: {ex.Message}"); }
    }

    void SaveFarm()
    {
        try
        {
            if (_farm != null) _farmData.Jobs = _farm.GetJobs().ToList();
            _farmData.Save(FarmPath);
        }
        catch (Exception ex) { AddLog($"farm.json speichern: {ex.Message}"); }
        RebuildJobs();
    }

    void RebuildProgramNames()
    {
        ProgramNames.Clear();
        foreach (var p in _project.Programs) ProgramNames.Add(p.Name);
    }

    void RebuildPrinterConfigs()
    {
        var sel = SelectedPrinterConfig;
        PrinterConfigs.Clear();
        foreach (var p in _farmData.Printers) PrinterConfigs.Add(p);
        SelectedPrinterConfig = PrinterConfigs.FirstOrDefault(p => p == sel) ?? PrinterConfigs.FirstOrDefault();

        var choice = NewJobPrinter?.Id;
        PrinterChoices.Clear();
        PrinterChoices.Add(new PrinterChoice(null, "Beliebiger Drucker"));
        foreach (var p in _farmData.Printers) PrinterChoices.Add(new PrinterChoice(p.Id, p.Name));
        NewJobPrinter = PrinterChoices.FirstOrDefault(c => c.Id == choice) ?? PrinterChoices[0];
    }

    void RebuildJobs()
    {
        if (_farm == null) return;
        var selId = SelectedJob?.Model.Id;
        JobRows.Clear();
        foreach (var j in _farm.GetJobs()) JobRows.Add(new JobRow(j));
        UpdateJobRows();
        SelectedJob = JobRows.FirstOrDefault(r => r.Model.Id == selId);
    }

    void UpdateJobRows()
    {
        foreach (var r in JobRows)
        {
            var j = r.Model;
            r.Progress = $"{j.Completed} / {j.Quantity}" + (j.InProgress > 0 ? $"  (+{j.InProgress} läuft)" : "");
            r.Printer = j.PrinterId == null ? "beliebig" : _farmData.Printers.FirstOrDefault(p => p.Id == j.PrinterId)?.Name ?? "?";
        }
    }

    static string StateText(SlotState s) => s switch
    {
        SlotState.Offline => "Offline",
        SlotState.CheckPlate => "Platte prüfen",
        SlotState.Ready => "Bereit",
        SlotState.Uploading => "Hochladen",
        SlotState.Starting => "Startet",
        SlotState.Printing => "Druckt",
        SlotState.BedDown => "Bett runter",
        SlotState.Cooling => "Kühlt ab",
        SlotState.Removing => "Roboter entnimmt",
        SlotState.WaitingForOperator => "Bitte abräumen",
        SlotState.Error => "FEHLER",
        _ => s.ToString(),
    };

    void UpdateFarm()
    {
        if (_farm == null) return;
        FarmRunning = _farm.Running;
        var snap = _farm.Snapshot();
        foreach (var r in PrinterRows.Where(r => snap.All(s => s.PrinterId != r.Id)).ToList()) PrinterRows.Remove(r);
        foreach (var s in snap)
        {
            var row = PrinterRows.FirstOrDefault(r => r.Id == s.PrinterId);
            if (row == null) { row = new PrinterRow { Id = s.PrinterId }; PrinterRows.Add(row); }
            row.Name = s.Name;
            row.State = StateText(s.State);
            row.Message = s.Message;
            row.PrinterState = s.Status.Online ? s.Status.GcodeState : "—";
            row.Bed = s.Status.BedTemp is double b ? $"{b:F1} °C" : "—";
            row.Job = s.JobFile ?? "";
            row.IsError = s.State == SlotState.Error;
            row.NeedsAction = s.State is SlotState.CheckPlate or SlotState.WaitingForOperator;
        }
        UpdateJobRows();
    }

    // ================================================================ Farm-Befehle

    [RelayCommand]
    void StartFarm()
    {
        if (_farm == null) return;
        if (_farmData.Printers.Any(p => p.HasRobot && string.IsNullOrEmpty(p.RemovalProgram)))
        {
            MessageBox.Show("Mindestens ein Drucker mit PAROL6 hat kein Entnahme-Programm.", "Farm starten", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _farm.Start();
    }

    [RelayCommand] void StopFarm() => _farm?.Stop();

    [RelayCommand]
    void ConfirmPlateEmpty()
    {
        if (SelectedPrinterRow == null || _farm == null) return;
        if (MessageBox.Show($"Ist die Druckplatte von '{SelectedPrinterRow.Name}' wirklich leer?\nDanach kann sofort ein neuer Druck starten.",
                "Platte leer", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _farm.ConfirmPlateEmpty(SelectedPrinterRow.Id);
    }

    [RelayCommand]
    void RemoveNow()
    {
        if (SelectedPrinterRow == null || _farm == null) return;
        if (MessageBox.Show($"Bett von '{SelectedPrinterRow.Name}' nach unten fahren und nach dem Abkühlen mit dem Roboter abräumen?",
                "Jetzt abräumen", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _farm.RemoveNow(SelectedPrinterRow.Id);
    }

    [RelayCommand] void AcknowledgeFarmError() { if (SelectedPrinterRow != null) _farm?.AcknowledgeError(SelectedPrinterRow.Id); }

    [RelayCommand]
    async Task TestBedGcode()
    {
        if (SelectedPrinterRow == null || _farm == null) return;
        var cfg = _farmData.Printers.FirstOrDefault(p => p.Id == SelectedPrinterRow.Id);
        if (cfg == null || string.IsNullOrWhiteSpace(cfg.BedDownGcode)) return;
        if (MessageBox.Show($"Folgenden G-Code an '{cfg.Name}' senden?\n\n{cfg.BedDownGcode}", "Bett-G-Code testen",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await _farm.SendGcodeAsync(cfg.Id, cfg.BedDownGcode); AddLog($"{cfg.Name}: G-Code gesendet"); }
        catch (Exception ex) { AddLog($"{cfg.Name}: G-Code fehlgeschlagen: {ex.Message}"); }
    }

    [RelayCommand]
    async Task StopPrint()
    {
        if (SelectedPrinterRow == null || _farm == null) return;
        if (MessageBox.Show($"Laufenden Druck auf '{SelectedPrinterRow.Name}' ABBRECHEN?", "Druck abbrechen",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _farm.StopPrintAsync(SelectedPrinterRow.Id); }
        catch (Exception ex) { AddLog($"Abbrechen fehlgeschlagen: {ex.Message}"); }
    }

    // ================================================================ Aufträge

    [RelayCommand]
    void AddJob()
    {
        if (_farm == null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Gesliceten Druck wählen (in Bambu Studio: Datei → Exportieren → Plattengeslicete Datei)",
            Filter = "Bambu-Druckdatei (*.gcode.3mf;*.3mf)|*.3mf|Alle Dateien|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        _farm.AddJob(new FarmJob
        {
            FilePath = dlg.FileName,
            Plate = Math.Max(1, NewJobPlate),
            Quantity = Math.Max(1, NewJobQuantity),
            PrinterId = NewJobPrinter?.Id,
            UseAms = NewJobUseAms,
            BedLeveling = NewJobBedLeveling,
        });
    }

    [RelayCommand]
    void RemoveJob()
    {
        if (SelectedJob == null || _farm == null) return;
        if (!_farm.RemoveJob(SelectedJob.Model.Id)) AddLog("Auftrag wird gerade gedruckt und kann nicht entfernt werden");
    }

    [RelayCommand] void JobUp() { if (SelectedJob != null) _farm?.MoveJob(SelectedJob.Model.Id, -1); }
    [RelayCommand] void JobDown() { if (SelectedJob != null) _farm?.MoveJob(SelectedJob.Model.Id, 1); }

    // ================================================================ Drucker bearbeiten

    partial void OnSelectedPrinterConfigChanged(PrinterConfig? value)
    {
        if (value == null) return;
        EdName = value.Name; EdKind = value.Kind; EdHost = value.Host; EdSerial = value.Serial;
        EdAccessCode = value.AccessCode; EdEnabled = value.Enabled; EdHasRobot = value.HasRobot;
        EdRemovalProgram = value.RemovalProgram; EdReleaseTemp = value.ReleaseTemperatureC;
        EdBedGcode = value.BedDownGcode; EdBedWait = value.BedMoveWaitSeconds;
    }

    [RelayCommand]
    async Task NewPrinter()
    {
        var p = new PrinterConfig { Name = $"Drucker {_farmData.Printers.Count + 1}", Enabled = false };
        _farmData.Printers.Add(p);
        SaveFarm();
        RebuildPrinterConfigs();
        SelectedPrinterConfig = p;
        await Task.CompletedTask;
    }

    [RelayCommand]
    async Task SavePrinter()
    {
        var p = SelectedPrinterConfig;
        if (p == null) return;
        if (EdHasRobot && _farmData.Printers.Any(x => x != p && x.HasRobot))
        {
            MessageBox.Show("Es gibt nur einen PAROL6 – ein anderer Drucker ist bereits als 'mit Roboter' eingetragen.",
                "Drucker speichern", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (FarmRunning && MessageBox.Show("Die Farm läuft. Beim Speichern werden alle Drucker neu verbunden. Fortfahren?",
                "Drucker speichern", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        p.Name = EdName.Trim(); p.Kind = EdKind; p.Host = EdHost.Trim(); p.Serial = EdSerial.Trim();
        p.AccessCode = EdAccessCode.Trim(); p.Enabled = EdEnabled; p.HasRobot = EdHasRobot;
        p.RemovalProgram = EdRemovalProgram; p.ReleaseTemperatureC = Math.Clamp(EdReleaseTemp, 15, 60);
        p.BedDownGcode = EdBedGcode; p.BedMoveWaitSeconds = Math.Clamp(EdBedWait, 0, 300);
        SaveFarm();
        RebuildPrinterConfigs();
        await ReconnectPrintersAsync();
        AddLog($"Drucker '{p.Name}' gespeichert");
    }

    [RelayCommand]
    async Task DeletePrinter()
    {
        var p = SelectedPrinterConfig;
        if (p == null) return;
        if (MessageBox.Show($"Drucker '{p.Name}' entfernen?", "Drucker löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _farmData.Printers.Remove(p);
        SaveFarm();
        RebuildPrinterConfigs();
        await ReconnectPrintersAsync();
    }

    // ================================================================ Entnahme durch den Roboter

    internal Task<RemovalOutcome> RunRemovalAsync(PrinterConfig printer, CancellationToken ct) =>
        _ui.InvokeAsync(async () =>
        {
            if (ProgramRunning) return new RemovalOutcome(RemovalResult.NotReady, "anderes Programm läuft");
            if (_robot.MotionBlockReason() is string reason) return new RemovalOutcome(RemovalResult.NotReady, reason);
            if (_robot.Status.Motion != MotionState.Idle) return new RemovalOutcome(RemovalResult.NotReady, "Roboter wird gerade bewegt");
            var prog = _project.Programs.FirstOrDefault(p => p.Name == printer.RemovalProgram);
            if (prog == null) return new RemovalOutcome(RemovalResult.Failed, $"Programm '{printer.RemovalProgram}' nicht gefunden");

            _programCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ProgramRunning = true;
            try
            {
                bool ok = await _runner.RunAsync(prog, _project, _programCts.Token);
                return ok ? new RemovalOutcome(RemovalResult.Done) : new RemovalOutcome(RemovalResult.Failed, "Programm abgebrochen – Roboter und Platte prüfen");
            }
            finally
            {
                ProgramRunning = false;
                foreach (var s in Steps) s.IsActive = false;
            }
        }).Task.Unwrap();
}
