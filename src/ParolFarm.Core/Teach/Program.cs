namespace ParolFarm.Core.Teach;

using System.Text.Json;
using System.Text.Json.Serialization;
using ParolFarm.Core.Kinematics;
using ParolFarm.Core.Robot;

/// <summary>Geteachter Punkt. Gespeichert werden Gelenkwinkel (eindeutig, inkl. Konfiguration).</summary>
public sealed class Waypoint
{
    public string Name { get; set; } = "";
    public double[] JointsDeg { get; set; } = new double[6];

    [JsonIgnore]
    public double[] JointsRad => JointsDeg.Select(d => d * System.Math.PI / 180).ToArray();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MoveJStep), "movej")]
[JsonDerivedType(typeof(MoveLStep), "movel")]
[JsonDerivedType(typeof(GripperStep), "gripper")]
[JsonDerivedType(typeof(WaitStep), "wait")]
public abstract class ProgramStep
{
    [JsonIgnore] public abstract string Display { get; }
}

public sealed class MoveJStep : ProgramStep
{
    public string Waypoint { get; set; } = "";
    /// <summary>Geschwindigkeit in % der Maximalgeschwindigkeit (vor Override).</summary>
    public double SpeedPercent { get; set; } = 50;
    public override string Display => $"MoveJ  → {Waypoint}  ({SpeedPercent:F0} %)";
}

public sealed class MoveLStep : ProgramStep
{
    public string Waypoint { get; set; } = "";
    /// <summary>Bahngeschwindigkeit in mm/s (vor Override).</summary>
    public double SpeedMmS { get; set; } = 50;
    public override string Display => $"MoveL  → {Waypoint}  ({SpeedMmS:F0} mm/s)";
}

public sealed class GripperStep : ProgramStep
{
    public bool Close { get; set; }
    /// <summary>Wartezeit nach dem Schalten (Ventil + Zylinder).</summary>
    public int WaitMs { get; set; } = 400;
    public override string Display => $"Greifer {(Close ? "ZU" : "AUF")}  (+{WaitMs} ms)";
}

public sealed class WaitStep : ProgramStep
{
    public int Ms { get; set; } = 500;
    public override string Display => $"Warten  {Ms} ms";
}

public sealed class RobotProgram
{
    public string Name { get; set; } = "Neues Programm";
    public List<ProgramStep> Steps { get; set; } = new();
}

/// <summary>Alle geteachten Punkte und Programme (eine Datei).</summary>
public sealed class RobotProject
{
    public List<Waypoint> Waypoints { get; set; } = new();
    public List<RobotProgram> Programs { get; set; } = new();

    public Waypoint? FindWaypoint(string name) => Waypoints.FirstOrDefault(w => w.Name == name);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static RobotProject Load(string path)
    {
        if (!File.Exists(path)) return new RobotProject();
        return JsonSerializer.Deserialize<RobotProject>(File.ReadAllText(path), Json) ?? new RobotProject();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, true);
    }
}

/// <summary>Führt ein Roboterprogramm Schritt für Schritt aus.</summary>
public sealed class ProgramRunner
{
    readonly RobotController _robot;

    public ProgramRunner(RobotController robot) => _robot = robot;

    /// <summary>Wird vor jedem Schritt mit dessen Index aufgerufen.</summary>
    public event Action<int>? StepStarted;
    public event Action<string>? Log;

    public async Task<bool> RunAsync(RobotProgram program, RobotProject project, CancellationToken ct)
    {
        // Vorab prüfen, ob alle Punkte existieren
        foreach (var s in program.Steps)
        {
            string? wp = s switch { MoveJStep m => m.Waypoint, MoveLStep l => l.Waypoint, _ => null };
            if (wp != null && project.FindWaypoint(wp) == null)
            {
                Log?.Invoke($"Programm '{program.Name}': Punkt '{wp}' existiert nicht");
                return false;
            }
        }

        Log?.Invoke($"Programm '{program.Name}' gestartet");
        for (int i = 0; i < program.Steps.Count; i++)
        {
            if (ct.IsCancellationRequested) { Log?.Invoke("Programm abgebrochen"); return false; }
            StepStarted?.Invoke(i);
            var step = program.Steps[i];
            bool ok;
            try
            {
                ok = await RunStep(step, project, ct);
            }
            catch (OperationCanceledException)
            {
                ok = false;
            }
            if (!ok)
            {
                Log?.Invoke($"Programm bei Schritt {i + 1} ({step.Display}) angehalten");
                return false;
            }
        }
        StepStarted?.Invoke(-1);
        Log?.Invoke($"Programm '{program.Name}' fertig");
        return true;
    }

    async Task<bool> RunStep(ProgramStep step, RobotProject project, CancellationToken ct)
    {
        switch (step)
        {
            case MoveJStep m:
                return await _robot.MoveJAsync(project.FindWaypoint(m.Waypoint)!.JointsRad, m.SpeedPercent / 100.0, ct);
            case MoveLStep l:
            {
                var target = _robot.Kinematics.Forward(project.FindWaypoint(l.Waypoint)!.JointsRad);
                return await _robot.MoveLAsync(target, l.SpeedMmS / 1000.0, ct);
            }
            case GripperStep g:
                if (_robot.MotionBlockReason() is string r) { Log?.Invoke(r); return false; }
                _robot.SetGripper(g.Close);
                await Task.Delay(g.WaitMs, ct);
                return true;
            case WaitStep w:
                await Task.Delay(w.Ms, ct);
                return true;
            default:
                return false;
        }
    }
}
