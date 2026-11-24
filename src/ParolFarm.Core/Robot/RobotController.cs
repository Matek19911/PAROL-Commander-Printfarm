namespace ParolFarm.Core.Robot;

using System.Diagnostics;
using System.Runtime.InteropServices;
using ParolFarm.Core.Kinematics;
using ParolFarm.Core.Math;
using static System.Math;

public enum JogFrame { Base, Tool }

public enum MotionState { Idle, Jog, Trajectory, Stopping }

/// <summary>Momentaufnahme des Roboterzustands für die Oberfläche.</summary>
public sealed record RobotStatus
{
    public bool Connected { get; init; }
    public string LinkName { get; init; } = "";
    public bool FeedbackOk { get; init; }
    public bool AllHomed { get; init; }
    public bool Enabled { get; init; }
    public bool EStop { get; init; }
    public MotionState Motion { get; init; }
    public double[] JointsRad { get; init; } = new double[6];
    public Pose Tcp { get; init; } = Pose.Identity;
    public bool[] Inputs { get; init; } = new bool[2];
    public bool[] Outputs { get; init; } = new bool[2];
    public bool GripperClosed { get; init; }
    public bool[] TemperatureError { get; init; } = new bool[6];
    public bool[] PositionError { get; init; } = new bool[6];
    public string? JogWarning { get; init; }
    public double LoopTimeMs { get; init; }
}

/// <summary>
/// Steuert den PAROL6 mit fester 100-Hz-Schleife.
/// Bewegungen werden immer als kommandierter Sollpfad (Position + Geschwindigkeits-Vorsteuerung,
/// Befehl 156) gestreamt – nicht wie im Original-Commander aus der gemessenen Position.
/// </summary>
public sealed class RobotController : IDisposable
{
    public const double Dt = 0.01;

    public Parol6Kinematics Kinematics { get; } = new();
    public TrajectoryPlanner Planner { get; }

    /// <summary>Geschwindigkeits-Override 0.01..1 (wie der Speed-Slider am UR).</summary>
    public double SpeedOverride
    {
        get => _override;
        set => _override = System.Math.Clamp(value, 0.01, 1.0);
    }
    double _override = 0.25;

    /// <summary>Digitaler Ausgang (1 oder 2), an dem das Greiferventil hängt.</summary>
    public int GripperOutput { get; set; } = 1;
    /// <summary>true: Ausgang HIGH = Greifer zu.</summary>
    public bool GripperClosedWhenHigh { get; set; } = true;

    /// <summary>Maximale Jog-Geschwindigkeit bei 100 % Override.</summary>
    public double JogMaxLinear { get; set; } = 0.06;           // m/s
    public double JogMaxAngular { get; set; } = 30 * PI / 180; // rad/s
    public double JogMaxJoint { get; set; } = 30 * PI / 180;   // rad/s

    public event Action<string>? Log;

    public RobotStatus Status => _status;
    volatile RobotStatus _status = new();

    IRobotLink? _link;
    Thread? _thread;
    volatile bool _run;
    readonly object _lock = new();

    // Rückmeldung
    FeedbackFrame? _fb;
    long _fbTicks;
    readonly double[] _qMeas = new double[6];

    // Kommando
    readonly double[] _qCmd = new double[6];
    readonly double[] _qPrev = new double[6];
    readonly bool[] _out = new bool[2];
    bool _enabled;
    bool _estop;
    byte? _oneShot;
    int _oneShotRepeat;
    MotionState _motion = MotionState.Idle;

    // Jog
    Vec3 _jogLin, _jogAng;              // Sollwert (m/s, rad/s)
    Vec3 _jogLinF, _jogAngF;            // gefiltert (Rampe)
    JogFrame _jogFrame;
    int _jogJoint = -1; double _jogJointVel, _jogJointVelF;
    long _jogStamp;
    string? _jogWarning;

    // Bahn
    Trajectory? _traj;
    double _trajU;
    double _trajRate;
    double _trajRateTarget;
    TaskCompletionSource<bool>? _trajDone;

    public RobotController()
    {
        Planner = new TrajectoryPlanner(Kinematics, Dt);
    }

    // ------------------------------------------------------------------ Verbindung

    public void Connect(IRobotLink link)
    {
        Disconnect();
        link.Open();
        lock (_lock)
        {
            _link = link;
            _fb = null;
            _enabled = false;
            _estop = false;
            _motion = MotionState.Idle;
        }
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "PAROL6 100Hz", Priority = ThreadPriority.Highest };
        _thread.Start();
        Log?.Invoke($"Verbunden mit {link.Name}");
    }

    public void Disconnect()
    {
        _run = false;
        _thread?.Join(1000);
        _thread = null;
        lock (_lock)
        {
            AbortTrajectory(false);
            _link?.Dispose();
            if (_link != null) Log?.Invoke("Verbindung getrennt");
            _link = null;
            _fb = null;
            _enabled = false;
        }
        _status = new RobotStatus();
    }

    public void Dispose() => Disconnect();

    // ------------------------------------------------------------------ Befehle

    public void Home()
    {
        lock (_lock)
        {
            if (!CheckConnected()) return;
            AbortTrajectory(false);
            _motion = MotionState.Idle;
            SendOneShot(BoardCommand.Home, 1);
        }
        Log?.Invoke("Referenzfahrt gestartet");
    }

    public void Enable()
    {
        lock (_lock)
        {
            if (!CheckConnected()) return;
            if (_estop) { Log?.Invoke("Not-Aus ist gedrückt – erst entriegeln"); return; }
            SendOneShot(BoardCommand.Enable, 5);
            _enabled = true;
        }
        Log?.Invoke("Roboter freigegeben");
    }

    public void Disable()
    {
        lock (_lock)
        {
            if (_link == null) return;
            AbortTrajectory(false);
            _motion = MotionState.Idle;
            SendOneShot(BoardCommand.Disable, 5);
            _enabled = false;
        }
        Log?.Invoke("Roboter gesperrt");
    }

    public void ClearError()
    {
        lock (_lock)
        {
            if (!CheckConnected()) return;
            SendOneShot(BoardCommand.ClearError, 5);
        }
        Log?.Invoke("Fehler quittiert");
    }

    void SendOneShot(byte cmd, int repeat)
    {
        _oneShot = cmd;
        _oneShotRepeat = repeat;
    }

    bool CheckConnected()
    {
        if (_link == null) { Log?.Invoke("Nicht verbunden"); return false; }
        return true;
    }

    public void SetOutput(int output, bool value)
    {
        if (output is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(output));
        lock (_lock) _out[output - 1] = value;
    }

    public void SetGripper(bool closed)
    {
        SetOutput(GripperOutput, closed == GripperClosedWhenHigh);
        Log?.Invoke(closed ? "Greifer zu" : "Greifer auf");
    }

    public bool GripperClosed
    {
        get { lock (_lock) return _out[GripperOutput - 1] == GripperClosedWhenHigh; }
    }

    /// <summary>Gibt einen Grund zurück, warum nicht verfahren werden darf, sonst null.</summary>
    public string? MotionBlockReason()
    {
        lock (_lock) return MotionBlockReasonUnlocked();
    }

    string? MotionBlockReasonUnlocked()
    {
        if (_link == null) return "Nicht verbunden";
        if (_fb == null || !FeedbackFresh()) return "Keine Rückmeldung vom Roboter";
        if (_estop) return "Not-Aus gedrückt";
        if (!_fb.Homed.Take(6).All(h => h)) return "Referenzfahrt fehlt";
        if (!_enabled) return "Roboter nicht freigegeben";
        return null;
    }

    bool FeedbackFresh() => Stopwatch.GetElapsedTime(_fbTicks).TotalMilliseconds < 500;

    public double[] CurrentJoints()
    {
        lock (_lock) return _qMeas.ToArray();
    }

    // ------------------------------------------------------------------ Jog

    /// <summary>
    /// Kartesisches Jogging. Muss laufend (mind. alle 250 ms) aufgerufen werden, sonst stoppt der Roboter.
    /// lin/ang sind normierte Werte -1..1 je Achse.
    /// </summary>
    public void JogCartesian(Vec3 lin, Vec3 ang, JogFrame frame)
    {
        lock (_lock)
        {
            if (_motion == MotionState.Trajectory || _motion == MotionState.Stopping) return;
            _jogLin = Clamp1(lin) * JogMaxLinear;
            _jogAng = Clamp1(ang) * JogMaxAngular;
            _jogFrame = frame;
            _jogJoint = -1;
            _jogStamp = Stopwatch.GetTimestamp();
            StartJogIfNeeded(lin.Length > 0 || ang.Length > 0);
        }
    }

    /// <summary>Gelenk-Jogging, vel normiert -1..1. Muss laufend aufgerufen werden.</summary>
    public void JogJoint(int joint, double vel)
    {
        lock (_lock)
        {
            if (_motion == MotionState.Trajectory || _motion == MotionState.Stopping) return;
            _jogLin = _jogAng = Vec3.Zero;
            _jogJoint = joint;
            _jogJointVel = System.Math.Clamp(vel, -1, 1) * JogMaxJoint;
            _jogStamp = Stopwatch.GetTimestamp();
            StartJogIfNeeded(vel != 0);
        }
    }

    public void StopJog()
    {
        lock (_lock)
        {
            _jogLin = _jogAng = Vec3.Zero;
            _jogJointVel = 0;
        }
    }

    static Vec3 Clamp1(Vec3 v) => new(System.Math.Clamp(v.X, -1, 1), System.Math.Clamp(v.Y, -1, 1), System.Math.Clamp(v.Z, -1, 1));

    void StartJogIfNeeded(bool nonZero)
    {
        if (_motion != MotionState.Idle || !nonZero) return;
        var reason = MotionBlockReasonUnlocked();
        if (reason != null) { _jogWarning = reason; return; }
        _qMeas.CopyTo(_qCmd, 0);
        _qMeas.CopyTo(_qPrev, 0);
        _jogLinF = _jogAngF = Vec3.Zero;
        _jogJointVelF = 0;
        _jogWarning = null;
        _motion = MotionState.Jog;
    }

    // ------------------------------------------------------------------ Bahnen

    /// <summary>Gelenkfahrt zu q (rad). speed 0..1 relativ zur Maximalgeschwindigkeit (wird mit Override multipliziert).</summary>
    public Task<bool> MoveJAsync(double[] q, double speed, CancellationToken ct = default)
    {
        double[] start;
        lock (_lock)
        {
            var reason = MotionBlockReasonUnlocked();
            if (reason != null) { Log?.Invoke($"Bewegung nicht möglich: {reason}"); return Task.FromResult(false); }
            start = _qMeas.ToArray();
        }
        var plan = Planner.PlanJoint(start, q, speed * SpeedOverride);
        return Execute(plan, ct);
    }

    /// <summary>Lineare Fahrt zu einer TCP-Pose. linSpeed in m/s (wird mit Override multipliziert).</summary>
    public Task<bool> MoveLAsync(Pose target, double linSpeed, CancellationToken ct = default)
    {
        double[] start;
        lock (_lock)
        {
            var reason = MotionBlockReasonUnlocked();
            if (reason != null) { Log?.Invoke($"Bewegung nicht möglich: {reason}"); return Task.FromResult(false); }
            start = _qMeas.ToArray();
        }
        var plan = Planner.PlanLinear(start, target, linSpeed * SpeedOverride, JogMaxAngular * SpeedOverride);
        return Execute(plan, ct);
    }

    Task<bool> Execute(PlanResult plan, CancellationToken ct)
    {
        if (!plan.Success) { Log?.Invoke($"Bahnplanung: {plan.Error}"); return Task.FromResult(false); }
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            var reason = MotionBlockReasonUnlocked();
            if (reason != null) { Log?.Invoke($"Bewegung nicht möglich: {reason}"); return Task.FromResult(false); }
            if (_motion != MotionState.Idle) { Log?.Invoke("Roboter bewegt sich bereits"); return Task.FromResult(false); }
            _qMeas.CopyTo(_qCmd, 0);
            _qMeas.CopyTo(_qPrev, 0);
            _traj = plan.Trajectory;
            _trajU = 0;
            _trajRate = 1;
            _trajRateTarget = 1;
            _trajDone = tcs;
            _motion = MotionState.Trajectory;
        }
        if (ct.CanBeCanceled) ct.Register(StopMotion);
        return tcs.Task;
    }

    /// <summary>Kontrollierter Stopp (Bahn wird sanft abgebremst).</summary>
    public void StopMotion()
    {
        lock (_lock)
        {
            if (_motion == MotionState.Trajectory) { _trajRateTarget = 0; _motion = MotionState.Stopping; }
            _jogLin = _jogAng = Vec3.Zero;
            _jogJointVel = 0;
        }
    }

    void AbortTrajectory(bool success)
    {
        _traj = null;
        var done = _trajDone;
        _trajDone = null;
        done?.TrySetResult(success);
    }

    // ------------------------------------------------------------------ Regelschleife

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint p);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint p);

    void Loop()
    {
        bool win = OperatingSystem.IsWindows();
        if (win) timeBeginPeriod(1);
        try
        {
            long period = (long)(Dt * Stopwatch.Frequency);
            long next = Stopwatch.GetTimestamp();
            long last = next;
            while (_run)
            {
                long now = Stopwatch.GetTimestamp();
                double loopMs = (now - last) * 1000.0 / Stopwatch.Frequency;
                last = now;
                try
                {
                    Tick(loopMs);
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"Kommunikationsfehler: {ex.Message}");
                    lock (_lock) { AbortTrajectory(false); _motion = MotionState.Idle; _enabled = false; }
                    Thread.Sleep(200);
                }
                next += period;
                long wait = next - Stopwatch.GetTimestamp();
                if (wait < -period * 5) next = Stopwatch.GetTimestamp(); // zu weit hinten -> neu synchronisieren
                while (true)
                {
                    long rem = next - Stopwatch.GetTimestamp();
                    if (rem <= 0) break;
                    double ms = rem * 1000.0 / Stopwatch.Frequency;
                    if (ms > 2) Thread.Sleep(1); else Thread.SpinWait(50);
                }
            }
        }
        finally
        {
            if (win) timeEndPeriod(1);
        }
    }

    void Tick(double loopMs)
    {
        IRobotLink? link;
        CommandFrame frame = new();
        lock (_lock)
        {
            link = _link;
            if (link == null) return;

            var fb = link.ReadLatest();
            if (fb != null)
            {
                _fb = fb;
                _fbTicks = Stopwatch.GetTimestamp();
                for (int j = 0; j < 6; j++) _qMeas[j] = Parol6Model.StepsToRad(fb.Position[j], j);

                bool estop = !fb.EStopReleased;
                if (estop && !_estop)
                {
                    AbortTrajectory(false);
                    _motion = MotionState.Idle;
                    _enabled = false;
                    Log?.Invoke("NOT-AUS ausgelöst");
                }
                if (!estop && _estop) Log?.Invoke("Not-Aus entriegelt – Roboter neu freigeben");
                _estop = estop;
            }
            if (_fb != null && !FeedbackFresh() && _motion != MotionState.Idle)
            {
                AbortTrajectory(false);
                _motion = MotionState.Idle;
                Log?.Invoke("Keine Rückmeldung vom Roboter – Bewegung abgebrochen");
            }

            BuildFrame(frame);
            PublishStatus(loopMs);
        }
        link.Send(frame);
    }

    void BuildFrame(CommandFrame f)
    {
        f.InOut[IoIndex.Out1] = _out[0];
        f.InOut[IoIndex.Out2] = _out[1];

        if (_oneShot is byte cmd)
        {
            f.Command = cmd;
            FillPosition(f, _qMeas, null);
            if (--_oneShotRepeat <= 0) _oneShot = null;
            return;
        }
        if (_estop)
        {
            f.Command = BoardCommand.Disable;
            FillPosition(f, _qMeas, null);
            return;
        }

        switch (_motion)
        {
            case MotionState.Jog:
                StepJog();
                break;
            case MotionState.Trajectory:
            case MotionState.Stopping:
                StepTrajectory();
                break;
        }

        if (_motion == MotionState.Idle)
        {
            _qMeas.CopyTo(_qCmd, 0);
            _qMeas.CopyTo(_qPrev, 0);
            f.Command = BoardCommand.Idle;
            FillPosition(f, _qMeas, null);
            return;
        }

        var vel = new double[6];
        for (int j = 0; j < 6; j++) vel[j] = (_qCmd[j] - _qPrev[j]) / Dt;
        _qCmd.CopyTo(_qPrev, 0);
        f.Command = BoardCommand.Position;
        FillPosition(f, _qCmd, vel);
    }

    static void FillPosition(CommandFrame f, double[] q, double[]? vel)
    {
        for (int j = 0; j < 6; j++)
        {
            f.Position[j] = (int)Round(Parol6Model.RadToSteps(q[j], j));
            f.Speed[j] = vel == null ? 0 : (int)Round(Parol6Model.RadToSteps(vel[j], j));
        }
    }

    static Vec3 Approach(Vec3 cur, Vec3 target, double maxStep)
    {
        var d = target - cur;
        double l = d.Length;
        return l <= maxStep ? target : cur + d * (maxStep / l);
    }

    void StepJog()
    {
        // Sicherheitsabschaltung, wenn keine neuen Jog-Vorgaben kommen
        if (Stopwatch.GetElapsedTime(_jogStamp).TotalMilliseconds > 250)
        {
            _jogLin = _jogAng = Vec3.Zero;
            _jogJointVel = 0;
        }

        double ov = _override;
        // Rampen: volle Geschwindigkeit nach ca. 0.3 s
        _jogLinF = Approach(_jogLinF, _jogLin * ov, JogMaxLinear * ov / 0.3 * Dt);
        _jogAngF = Approach(_jogAngF, _jogAng * ov, JogMaxAngular * ov / 0.3 * Dt);
        double jt = _jogJointVel * ov;
        double js = JogMaxJoint * ov / 0.3 * Dt;
        _jogJointVelF = _jogJointVelF + System.Math.Clamp(jt - _jogJointVelF, -js, js);

        bool cartActive = _jogLinF.Length > 1e-7 || _jogAngF.Length > 1e-7;
        bool jointActive = _jogJoint >= 0 && Abs(_jogJointVelF) > 1e-7;
        bool requested = _jogLin.Length > 0 || _jogAng.Length > 0 || (_jogJoint >= 0 && _jogJointVel != 0);
        if (!cartActive && !jointActive && !requested)
        {
            _motion = MotionState.Idle;
            return;
        }

        if (_jogJoint >= 0)
        {
            int j = _jogJoint;
            double nq = _qCmd[j] + _jogJointVelF * Dt;
            double min = Parol6Model.MinRad(j), max = Parol6Model.MaxRad(j);
            if (nq < min || nq > max)
            {
                nq = System.Math.Clamp(nq, min, max);
                _jogJointVelF = 0;
                _jogWarning = $"Gelenk {j + 1} an der Grenze";
            }
            else _jogWarning = null;
            _qCmd[j] = nq;
            return;
        }

        var t = Kinematics.Forward(_qCmd);
        var dp = _jogLinF * Dt;
        var dr = Rot3.FromRotationVector(_jogAngF * Dt);
        Pose target = _jogFrame == JogFrame.Base
            ? new Pose((dr * t.R).Orthonormalized(), t.P + dp)
            : new Pose((t.R * dr).Orthonormalized(), t.P + t.R * dp);

        var ik = Kinematics.Inverse(target, _qCmd, 20, 1e-6, 1e-5);
        bool ok = ik.Success;
        if (ok)
        {
            for (int j = 0; j < 6; j++)
                if (Abs(ik.Q[j] - _qCmd[j]) / Dt > Parol6Model.MaxSpeedRad(j) * 0.9) { ok = false; break; }
        }
        if (!ok)
        {
            // Grenze/Singularität: anhalten, aber Jog-Modus behalten (andere Richtung bleibt möglich)
            _jogLinF = _jogAngF = Vec3.Zero;
            _jogWarning = ik.Success ? "Singularität – Gelenk würde zu schnell" : $"Grenze erreicht ({ik.Error})";
            return;
        }
        _jogWarning = null;
        ik.Q.CopyTo(_qCmd, 0);
    }

    void StepTrajectory()
    {
        var traj = _traj;
        if (traj == null) { _motion = MotionState.Idle; return; }

        // Abspielrate sanft ändern (Stopp/Pause innerhalb ~0.3 s)
        double rs = Dt / 0.3;
        _trajRate += System.Math.Clamp(_trajRateTarget - _trajRate, -rs, rs);
        _trajU += _trajRate;

        int n = traj.Points.Count;
        if (_trajU >= n - 1)
        {
            traj.Points[n - 1].CopyTo(_qCmd, 0);
            _motion = MotionState.Idle;
            AbortTrajectory(true);
            return;
        }
        int i = (int)Floor(_trajU);
        double frac = _trajU - i;
        var a = traj.Points[i];
        var b = traj.Points[System.Math.Min(i + 1, n - 1)];
        for (int j = 0; j < 6; j++) _qCmd[j] = a[j] + (b[j] - a[j]) * frac;

        if (_motion == MotionState.Stopping && _trajRate <= 1e-6)
        {
            _motion = MotionState.Idle;
            AbortTrajectory(false);
            Log?.Invoke("Bewegung gestoppt");
        }
    }

    void PublishStatus(double loopMs)
    {
        var fb = _fb;
        var q = _qMeas.ToArray();
        _status = new RobotStatus
        {
            Connected = _link != null,
            LinkName = _link?.Name ?? "",
            FeedbackOk = fb != null && FeedbackFresh(),
            AllHomed = fb != null && fb.Homed.Take(6).All(h => h),
            Enabled = _enabled,
            EStop = _estop,
            Motion = _motion,
            JointsRad = q,
            Tcp = Kinematics.Forward(q),
            Inputs = fb == null ? new bool[2] : new[] { fb.InOut[IoIndex.In1], fb.InOut[IoIndex.In2] },
            Outputs = _out.ToArray(),
            GripperClosed = _out[GripperOutput - 1] == GripperClosedWhenHigh,
            TemperatureError = fb?.TemperatureError.Take(6).ToArray() ?? new bool[6],
            PositionError = fb?.PositionError.Take(6).ToArray() ?? new bool[6],
            JogWarning = _jogWarning,
            LoopTimeMs = loopMs,
        };
    }
}
