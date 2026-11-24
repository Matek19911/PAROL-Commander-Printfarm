namespace ParolFarm.Core.Farm;

using System.Text.Json;
using System.Text.Json.Serialization;
using ParolFarm.Core.Printers;

public enum SlotState
{
    Offline,
    /// <summary>Unklar, ob die Platte leer ist – Bediener muss bestätigen.</summary>
    CheckPlate,
    Ready,
    Uploading,
    Starting,
    Printing,
    BedDown,
    Cooling,
    Removing,
    /// <summary>Drucker ohne Roboter: Bediener räumt ab.</summary>
    WaitingForOperator,
    Error,
}

/// <summary>Druckauftrag: Datei, Platte, Stückzahl.</summary>
public sealed class FarmJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = "";
    public int Plate { get; set; } = 1;
    public int Quantity { get; set; } = 1;
    public int Completed { get; set; }
    /// <summary>null = beliebiger Drucker.</summary>
    public string? PrinterId { get; set; }
    public bool UseAms { get; set; }
    public bool BedLeveling { get; set; } = true;
    [JsonIgnore] public int InProgress { get; set; }
    [JsonIgnore] public int Remaining => Quantity - Completed - InProgress;
    [JsonIgnore] public string FileName => Path.GetFileName(FilePath);
}

public sealed class FarmData
{
    public List<PrinterConfig> Printers { get; set; } = new();
    public List<FarmJob> Jobs { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static FarmData Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<FarmData>(File.ReadAllText(path), Json) ?? new() : new();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, true);
    }
}

public enum RemovalResult { Done, NotReady, Failed }

public sealed record RemovalOutcome(RemovalResult Result, string? Message = null);

/// <summary>Führt die Entnahme mit dem Roboter aus (in der App: Roboterprogramm starten).</summary>
public interface IRemovalService
{
    Task<RemovalOutcome> RemoveAsync(PrinterConfig printer, CancellationToken ct);
}

/// <summary>Zustand eines Druckers in der Farm (Momentaufnahme für die Oberfläche).</summary>
public sealed record SlotInfo(string PrinterId, string Name, SlotState State, string Message, PrinterStatus Status, string? JobFile);

/// <summary>Steuert alle Drucker der Farm. Ein Takt pro Sekunde, Aktionen laufen als Hintergrundaufgaben.</summary>
public sealed class FarmCoordinator : IAsyncDisposable
{
    sealed class Slot
    {
        public required PrinterConfig Config;
        public required IPrinterConnection Conn;
        public SlotState State = SlotState.Offline;
        public string Message = "";
        public DateTime Since = DateTime.UtcNow;
        public FarmJob? Job;
        public Task? Pending;
        public Task<RemovalOutcome>? Removal;
        public DateTime RetryAt = DateTime.MinValue;
        public bool RemoveRequested;
        public string? UploadedKey;
    }

    readonly Func<PrinterConfig, IPrinterConnection> _factory;
    readonly IRemovalService _removal;
    readonly object _lock = new();
    readonly List<Slot> _slots = new();
    readonly CancellationTokenSource _cts = new();
    Task? _loop;
    bool _robotBusy;

    readonly List<FarmJob> Jobs = new();
    public bool Running { get; private set; }
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(120);

    public event Action<string>? Log;
    /// <summary>Aufträge haben sich geändert (zum Speichern).</summary>
    public event Action? JobsChanged;

    public FarmCoordinator(Func<PrinterConfig, IPrinterConnection> factory, IRemovalService removal)
    {
        _factory = factory;
        _removal = removal;
    }

    // ------------------------------------------------------------------ Verwaltung

    public async Task SetPrintersAsync(IEnumerable<PrinterConfig> printers)
    {
        List<Slot> old;
        lock (_lock) { old = _slots.ToList(); _slots.Clear(); }
        foreach (var s in old) await s.Conn.DisposeAsync();
        var fresh = new List<Slot>();
        foreach (var p in printers.Where(p => p.Enabled))
        {
            var conn = _factory(p);
            fresh.Add(new Slot { Config = p, Conn = conn });
            await conn.StartAsync(_cts.Token);
        }
        lock (_lock) _slots.AddRange(fresh);
        _loop ??= Task.Run(() => Loop(_cts.Token));
    }

    public IReadOnlyList<SlotInfo> Snapshot()
    {
        lock (_lock)
            return _slots.Select(s => new SlotInfo(s.Config.Id, s.Config.Name, s.State, s.Message, s.Conn.Status, s.Job?.FileName)).ToList();
    }

    // ------------------------------------------------------------------ Aufträge (thread-sicher)

    public IReadOnlyList<FarmJob> GetJobs() { lock (_lock) return Jobs.ToList(); }

    public void SetJobs(IEnumerable<FarmJob> jobs) { lock (_lock) { Jobs.Clear(); Jobs.AddRange(jobs); } }

    public void AddJob(FarmJob job) { lock (_lock) Jobs.Add(job); JobsChanged?.Invoke(); }

    public bool RemoveJob(string jobId)
    {
        lock (_lock)
        {
            var j = Jobs.FirstOrDefault(x => x.Id == jobId);
            if (j == null || j.InProgress > 0) return false;
            Jobs.Remove(j);
        }
        JobsChanged?.Invoke();
        return true;
    }

    public void MoveJob(string jobId, int dir)
    {
        lock (_lock)
        {
            int i = Jobs.FindIndex(x => x.Id == jobId), k = i + dir;
            if (i < 0 || k < 0 || k >= Jobs.Count) return;
            (Jobs[i], Jobs[k]) = (Jobs[k], Jobs[i]);
        }
        JobsChanged?.Invoke();
    }

    public void Start() { lock (_lock) Running = true; Log?.Invoke("Farm gestartet"); }
    public void Stop() { lock (_lock) Running = false; Log?.Invoke("Farm angehalten (laufende Drucke laufen weiter)"); }

    Slot? Find(string id) => _slots.FirstOrDefault(s => s.Config.Id == id);

    /// <summary>Bediener bestätigt: Platte ist leer.</summary>
    public void ConfirmPlateEmpty(string printerId)
    {
        lock (_lock)
        {
            var s = Find(printerId);
            if (s == null) return;
            if (s.State is SlotState.CheckPlate or SlotState.WaitingForOperator or SlotState.Cooling or SlotState.BedDown)
                Set(s, SlotState.Ready, "Platte leer (bestätigt)");
        }
    }

    /// <summary>Entnahme mit dem Roboter sofort anstoßen (auch bei angehaltener Farm).</summary>
    public void RemoveNow(string printerId)
    {
        lock (_lock)
        {
            var s = Find(printerId);
            if (s == null) return;
            if (!s.Config.HasRobot) { Log?.Invoke($"{s.Config.Name}: hat keinen Roboter"); return; }
            if (s.Conn.Status.IsPrinting) { Log?.Invoke($"{s.Config.Name}: druckt gerade"); return; }
            if (s.State is SlotState.CheckPlate or SlotState.WaitingForOperator or SlotState.Cooling or SlotState.Error or SlotState.Ready)
            {
                s.RemoveRequested = true;
                BeginBedDown(s);
            }
        }
    }

    public void AcknowledgeError(string printerId)
    {
        lock (_lock)
        {
            var s = Find(printerId);
            if (s?.State == SlotState.Error) Set(s, SlotState.CheckPlate, "Fehler quittiert – Platte prüfen");
        }
    }

    public Task SendGcodeAsync(string printerId, string gcode)
    {
        IPrinterConnection? c;
        lock (_lock) c = Find(printerId)?.Conn;
        return c?.SendGcodeAsync(gcode, _cts.Token) ?? Task.CompletedTask;
    }

    public Task StopPrintAsync(string printerId)
    {
        IPrinterConnection? c;
        lock (_lock) c = Find(printerId)?.Conn;
        return c?.StopPrintAsync(_cts.Token) ?? Task.CompletedTask;
    }

    // ------------------------------------------------------------------ Ablauf

    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            lock (_lock)
            {
                foreach (var s in _slots)
                {
                    try { Step(s); }
                    catch (Exception ex) { Set(s, SlotState.Error, ex.Message); }
                }
            }
            try { await Task.Delay(TickInterval, ct); } catch { break; }
        }
    }

    void Set(Slot s, SlotState state, string msg)
    {
        if (s.State != state || s.Message != msg)
        {
            if (s.State != state) Log?.Invoke($"{s.Config.Name}: {state}{(msg.Length > 0 ? " – " + msg : "")}");
            s.State = state;
            s.Message = msg;
            s.Since = DateTime.UtcNow;
        }
    }

    FarmJob? NextJob(Slot s) =>
        Jobs.FirstOrDefault(j => j.Remaining > 0 && (j.PrinterId == null || j.PrinterId == s.Config.Id) && File.Exists(j.FilePath));

    static string RemoteName(FarmJob j) =>
        string.Concat(Path.GetFileName(j.FilePath).Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));

    void ReleaseJob(Slot s)
    {
        if (s.Job != null) { s.Job.InProgress = System.Math.Max(0, s.Job.InProgress - 1); s.Job = null; JobsChanged?.Invoke(); }
    }

    void Step(Slot s)
    {
        var st = s.Conn.Status;
        var age = DateTime.UtcNow - s.Since;

        // Verbindung
        if (!st.Online)
        {
            if (s.State is SlotState.Offline or SlotState.CheckPlate or SlotState.Ready)
                Set(s, SlotState.Offline, st.ConnectionError ?? "keine Verbindung");
            else if (!s.Message.StartsWith("offline")) s.Message = "offline – " + s.Message;
            return;
        }
        if (s.Message.StartsWith("offline – ")) s.Message = s.Message["offline – ".Length..];
        if (string.IsNullOrEmpty(st.GcodeState)) return; // noch kein Zustand empfangen

        switch (s.State)
        {
            case SlotState.Offline:
                if (st.IsPrinting) Set(s, SlotState.Printing, "laufender Druck übernommen");
                else Set(s, SlotState.CheckPlate, "Ist die Druckplatte leer?");
                break;

            case SlotState.CheckPlate:
            case SlotState.WaitingForOperator:
                if (st.IsPrinting) Set(s, SlotState.Printing, "Druck wurde am Drucker gestartet");
                break;

            case SlotState.Ready:
                if (st.IsPrinting) { Set(s, SlotState.Printing, "Druck wurde am Drucker gestartet"); break; }
                if (!Running || !st.IsIdleLike) break;
                var job = NextJob(s);
                if (job == null) { s.Message = "wartet auf Auftrag"; break; }
                s.Job = job;
                job.InProgress++;
                JobsChanged?.Invoke();
                var remote = RemoteName(job);
                var key = $"{job.FilePath}|{File.GetLastWriteTimeUtc(job.FilePath).Ticks}";
                if (s.UploadedKey == key)
                {
                    s.Pending = s.Conn.StartPrintAsync(remote, job.Plate, job.UseAms, job.BedLeveling, _cts.Token);
                    Set(s, SlotState.Starting, $"starte {job.FileName}");
                }
                else
                {
                    s.UploadedKey = null;
                    s.Pending = s.Conn.UploadAsync(job.FilePath, remote, null, _cts.Token);
                    Set(s, SlotState.Uploading, $"lade {job.FileName} hoch");
                }
                break;

            case SlotState.Uploading:
                if (s.Pending is not { IsCompleted: true } up) break;
                if (up.IsFaulted || up.IsCanceled)
                {
                    ReleaseJob(s);
                    Set(s, SlotState.Error, $"Upload fehlgeschlagen: {up.Exception?.GetBaseException().Message}");
                    break;
                }
                s.UploadedKey = $"{s.Job!.FilePath}|{File.GetLastWriteTimeUtc(s.Job.FilePath).Ticks}";
                s.Pending = s.Conn.StartPrintAsync(RemoteName(s.Job), s.Job.Plate, s.Job.UseAms, s.Job.BedLeveling, _cts.Token);
                Set(s, SlotState.Starting, $"starte {s.Job.FileName}");
                break;

            case SlotState.Starting:
                if (s.Pending is { IsFaulted: true } sp)
                {
                    ReleaseJob(s);
                    Set(s, SlotState.Error, $"Start fehlgeschlagen: {sp.Exception?.GetBaseException().Message}");
                    break;
                }
                if (st.IsPrinting) { Set(s, SlotState.Printing, s.Job?.FileName ?? ""); break; }
                if (age > StartTimeout)
                {
                    ReleaseJob(s);
                    Set(s, SlotState.Error, "Drucker hat den Druck nicht gestartet (Meldung am Display prüfen)");
                }
                break;

            case SlotState.Printing:
                if (st.IsFinished)
                {
                    if (s.Job != null)
                    {
                        s.Job.InProgress = System.Math.Max(0, s.Job.InProgress - 1);
                        s.Job.Completed++;
                        Log?.Invoke($"{s.Config.Name}: {s.Job.FileName} fertig ({s.Job.Completed}/{s.Job.Quantity})");
                        s.Job = null;
                        JobsChanged?.Invoke();
                    }
                    if (s.Config.HasRobot) BeginBedDown(s);
                    else Set(s, SlotState.WaitingForOperator, "Druck fertig – bitte Teil entnehmen und bestätigen");
                }
                else if (st.IsFailed)
                {
                    ReleaseJob(s);
                    Set(s, SlotState.Error, $"Druck fehlgeschlagen (Fehlercode {st.PrintError:X})");
                }
                else if (st.GcodeState == "IDLE" && age > TimeSpan.FromSeconds(30))
                {
                    ReleaseJob(s);
                    Set(s, SlotState.Error, "Druck abgebrochen");
                }
                else s.Message = $"{st.Progress} %  ·  Schicht {st.Layer}/{st.TotalLayers}  ·  noch {st.RemainingMinutes} min";
                break;

            case SlotState.BedDown:
                if (s.Pending is { IsFaulted: true } bd)
                {
                    Set(s, SlotState.Error, $"Bett-G-Code fehlgeschlagen: {bd.Exception?.GetBaseException().Message}");
                    break;
                }
                if (age.TotalSeconds >= s.Config.BedMoveWaitSeconds)
                    Set(s, SlotState.Cooling, "kühlt ab");
                break;

            case SlotState.Cooling:
            {
                double bed = st.BedTemp ?? double.MaxValue;
                bool cool = bed <= s.Config.ReleaseTemperatureC && (st.BedTarget ?? 0) <= 0;
                if (!cool) { s.Message = $"Bett {bed:F1} °C → warte auf ≤ {s.Config.ReleaseTemperatureC:F1} °C"; break; }
                if (!Running && !s.RemoveRequested) { s.Message = $"abgekühlt ({bed:F1} °C) – Farm angehalten"; break; }
                if (_robotBusy) { s.Message = "abgekühlt – Roboter ist beschäftigt"; break; }
                if (DateTime.UtcNow < s.RetryAt) break;
                _robotBusy = true;
                s.Removal = _removal.RemoveAsync(s.Config, _cts.Token);
                Set(s, SlotState.Removing, "Roboter entnimmt Teil");
                break;
            }

            case SlotState.Removing:
                if (s.Removal is not { IsCompleted: true } rm) break;
                _robotBusy = false;
                s.RemoveRequested = false;
                var outcome = rm.IsCompletedSuccessfully ? rm.Result : new RemovalOutcome(RemovalResult.Failed, rm.Exception?.GetBaseException().Message);
                switch (outcome.Result)
                {
                    case RemovalResult.Done:
                        Set(s, SlotState.Ready, "Platte abgeräumt");
                        break;
                    case RemovalResult.NotReady:
                        s.RetryAt = DateTime.UtcNow.AddSeconds(10);
                        Set(s, SlotState.Cooling, $"Roboter nicht bereit: {outcome.Message}");
                        break;
                    default:
                        Set(s, SlotState.Error, $"Entnahme fehlgeschlagen: {outcome.Message}");
                        break;
                }
                break;

            case SlotState.Error:
                break;
        }
    }

    void BeginBedDown(Slot s)
    {
        if (string.IsNullOrWhiteSpace(s.Config.BedDownGcode))
        {
            Set(s, SlotState.Cooling, "kühlt ab");
            return;
        }
        s.Pending = s.Conn.SendGcodeAsync(s.Config.BedDownGcode, _cts.Token);
        Set(s, SlotState.BedDown, "Bett fährt nach unten");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null) { try { await _loop; } catch { } }
        List<Slot> slots;
        lock (_lock) slots = _slots.ToList();
        foreach (var s in slots) await s.Conn.DisposeAsync();
    }
}
