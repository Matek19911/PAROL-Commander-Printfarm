using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParolFarm.App.Input;
using ParolFarm.Core.Kinematics;
using ParolFarm.Core.Math;
using ParolFarm.Core.Robot;
using ParolFarm.Core.Settings;
using ParolFarm.Core.Teach;

namespace ParolFarm.App;

public sealed partial class WaypointItem : ObservableObject
{
    public Waypoint Model { get; }
    [ObservableProperty] string _summary = "";
    public WaypointItem(Waypoint model) => Model = model;
    public string Name => Model.Name;
    public void Refresh(Parol6Kinematics kin)
    {
        var p = kin.Forward(Model.JointsRad).ToMmDeg();
        Summary = $"X {p.X:F1}  Y {p.Y:F1}  Z {p.Z:F1}  |  Rx {p.Rx:F1}  Ry {p.Ry:F1}  Rz {p.Rz:F1}";
        OnPropertyChanged(nameof(Name));
    }
}

public sealed partial class StepItem : ObservableObject
{
    public ProgramStep Model { get; }
    [ObservableProperty] bool _isActive;
    [ObservableProperty] int _number;
    public StepItem(ProgramStep model) => Model = model;
    public string Display => Model.Display;
    public void Refresh() => OnPropertyChanged(nameof(Display));
}

public sealed partial class ProgramItem : ObservableObject
{
    public RobotProgram Model { get; }
    public ProgramItem(RobotProgram model) => Model = model;
    public string Name => Model.Name;
    public void Refresh() => OnPropertyChanged(nameof(Name));
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    readonly RobotController _robot = new();
    readonly ProgramRunner _runner;
    readonly DispatcherTimer _statusTimer;
    readonly DispatcherTimer _jogTimer;
    readonly Dispatcher _ui;
    SpaceMouseInput? _spaceMouse;
    uint _lastButtons;
    RobotProject _project;
    CancellationTokenSource? _programCts;
    bool _windowActive = true;

    public AppSettings Settings { get; }

    public ObservableCollection<string> Ports { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<WaypointItem> Waypoints { get; } = new();
    public ObservableCollection<ProgramItem> Programs { get; } = new();
    public ObservableCollection<StepItem> Steps { get; } = new();
    public ObservableCollection<string> WaypointNames { get; } = new();

    // ---------------- Verbindung / Status
    [ObservableProperty] string? _selectedPort;
    [ObservableProperty] bool _connected;
    [ObservableProperty] bool _homed;
    [ObservableProperty] bool _enabled;
    [ObservableProperty] bool _eStop;
    [ObservableProperty] bool _feedbackOk;
    [ObservableProperty] string _motionText = "";
    [ObservableProperty] string _jogWarning = "";
    [ObservableProperty] string _linkName = "";
    [ObservableProperty] bool _gripperClosed;
    [ObservableProperty] string _errorText = "";

    // ---------------- Position
    [ObservableProperty] string _tcpX = "-";
    [ObservableProperty] string _tcpY = "-";
    [ObservableProperty] string _tcpZ = "-";
    [ObservableProperty] string _tcpRx = "-";
    [ObservableProperty] string _tcpRy = "-";
    [ObservableProperty] string _tcpRz = "-";
    [ObservableProperty] string _j1 = "-";
    [ObservableProperty] string _j2 = "-";
    [ObservableProperty] string _j3 = "-";
    [ObservableProperty] string _j4 = "-";
    [ObservableProperty] string _j5 = "-";
    [ObservableProperty] string _j6 = "-";

    // ---------------- Jog
    [ObservableProperty] double _speedOverride = 25;
    [ObservableProperty] bool _toolFrame;
    [ObservableProperty] bool _spaceMouseActive = true;
    [ObservableProperty] string _spaceMouseText = "SpaceMouse: nicht erkannt";
    /// <summary>Gedrückte Bildschirm-Jogtaste, z. B. "X+", "Rz-", "J3+".</summary>
    public string? HeldJogButton { get; set; }

    // ---------------- Punkte
    [ObservableProperty] WaypointItem? _selectedWaypoint;
    [ObservableProperty] string _newWaypointName = "Punkt 1";
    [ObservableProperty] double _moveSpeedPercent = 30;

    // ---------------- Programme
    [ObservableProperty] ProgramItem? _selectedProgram;
    [ObservableProperty] StepItem? _selectedStep;
    [ObservableProperty] string _programName = "";
    [ObservableProperty] string? _stepWaypoint;
    [ObservableProperty] double _stepSpeed;
    [ObservableProperty] int _stepWaitMs;
    [ObservableProperty] bool _programRunning;

    // ---------------- Einstellungen (Bearbeitung)
    [ObservableProperty] double _toolX;
    [ObservableProperty] double _toolY;
    [ObservableProperty] double _toolZ;
    [ObservableProperty] double _toolRx;
    [ObservableProperty] double _toolRy;
    [ObservableProperty] double _toolRz;
    [ObservableProperty] int _gripperOutput;
    [ObservableProperty] bool _gripperClosedWhenHigh;
    [ObservableProperty] double _jogLinear;
    [ObservableProperty] double _jogAngular;
    [ObservableProperty] double _jogJoint;
    [ObservableProperty] double _smDeadband;
    [ObservableProperty] double _smExponent;
    [ObservableProperty] bool _smDominant;
    [ObservableProperty] bool _smTranslation;
    [ObservableProperty] bool _smRotation;
    [ObservableProperty] bool _smInvX;
    [ObservableProperty] bool _smInvY;
    [ObservableProperty] bool _smInvZ;
    [ObservableProperty] bool _smInvRx;
    [ObservableProperty] bool _smInvRy;
    [ObservableProperty] bool _smInvRz;

    public MainViewModel()
    {
        _ui = Dispatcher.CurrentDispatcher;
        Settings = AppSettings.Load();
        _project = LoadProject();
        _runner = new ProgramRunner(_robot);
        _robot.Log += AddLog;
        _runner.Log += AddLog;
        _runner.StepStarted += i => _ui.BeginInvoke(() =>
        {
            for (int k = 0; k < Steps.Count; k++) Steps[k].IsActive = k == i;
        });

        ApplySettingsToRobot();
        LoadSettingsIntoEditor();
        SpeedOverride = Settings.SpeedOverridePercent;
        RefreshPorts();
        SelectedPort = Settings.ComPort != null && Ports.Contains(Settings.ComPort) ? Settings.ComPort : Ports.FirstOrDefault();
        RebuildWaypoints();
        RebuildPrograms();

        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => UpdateStatus(), _ui);
        _jogTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Input, (_, _) => JogTick(), _ui);
        _statusTimer.Start();
        _jogTimer.Start();
        AddLog($"Daten: {AppSettings.DataDirectory}");
    }

    RobotProject LoadProject()
    {
        try { return RobotProject.Load(AppSettings.ProjectPath); }
        catch (Exception ex)
        {
            AddLog($"Projektdatei defekt ({ex.Message}) – starte leer, alte Datei bleibt erhalten");
            try { File.Copy(AppSettings.ProjectPath, AppSettings.ProjectPath + ".defekt", true); } catch { }
            return new RobotProject();
        }
    }

    void SaveProject()
    {
        try { _project.Save(AppSettings.ProjectPath); }
        catch (Exception ex) { AddLog($"Speichern fehlgeschlagen: {ex.Message}"); }
    }

    public void AttachSpaceMouse(Window w)
    {
        _spaceMouse = new SpaceMouseInput();
        if (!_spaceMouse.Attach(w)) AddLog("SpaceMouse: Registrierung fehlgeschlagen");
        _spaceMouse.Changed += OnSpaceMouseChanged;
    }

    public void SetWindowActive(bool active)
    {
        _windowActive = active;
        if (!active)
        {
            _spaceMouse?.Reset();
            HeldJogButton = null;
            _robot.StopJog();
        }
    }

    void AddLog(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {msg}";
        _ui.BeginInvoke(() =>
        {
            LogLines.Insert(0, line);
            while (LogLines.Count > 500) LogLines.RemoveAt(LogLines.Count - 1);
        });
    }

    // ================================================================ Verbindung

    [RelayCommand]
    void RefreshPorts()
    {
        var sel = SelectedPort;
        Ports.Clear();
        foreach (var p in SerialRobotLink.AvailablePorts()) Ports.Add(p);
        SelectedPort = sel != null && Ports.Contains(sel) ? sel : Ports.FirstOrDefault();
    }

    [RelayCommand]
    void Connect()
    {
        if (string.IsNullOrEmpty(SelectedPort)) { AddLog("Kein COM-Port ausgewählt"); return; }
        try
        {
            _robot.Connect(new SerialRobotLink(SelectedPort));
            Settings.ComPort = SelectedPort;
            Settings.Save();
        }
        catch (Exception ex)
        {
            AddLog($"Verbindung zu {SelectedPort} fehlgeschlagen: {ex.Message}");
        }
    }

    [RelayCommand]
    void ConnectSimulation() => _robot.Connect(new SimulatedRobotLink());

    [RelayCommand]
    void Disconnect()
    {
        StopProgram();
        _robot.Disconnect();
    }

    [RelayCommand] void Home() => _robot.Home();
    [RelayCommand] void Enable() => _robot.Enable();
    [RelayCommand] void Disable() { StopProgram(); _robot.Disable(); }
    [RelayCommand] void ClearError() => _robot.ClearError();
    [RelayCommand] void GripperClose() => _robot.SetGripper(true);
    [RelayCommand] void GripperOpen() => _robot.SetGripper(false);
    [RelayCommand] void Stop() { StopProgram(); _robot.StopMotion(); }

    void UpdateStatus()
    {
        var s = _robot.Status;
        Connected = s.Connected;
        LinkName = s.LinkName;
        FeedbackOk = s.FeedbackOk;
        Homed = s.AllHomed;
        Enabled = s.Enabled;
        EStop = s.EStop;
        GripperClosed = s.GripperClosed;
        MotionText = s.Motion switch
        {
            MotionState.Jog => "Jog",
            MotionState.Trajectory => "Fährt",
            MotionState.Stopping => "Stoppt",
            _ => "Steht",
        };
        JogWarning = s.JogWarning ?? "";

        var errs = new List<string>();
        for (int j = 0; j < 6; j++)
        {
            if (s.TemperatureError[j]) errs.Add($"Temperatur J{j + 1}");
            if (s.PositionError[j]) errs.Add($"Positionsfehler J{j + 1}");
        }
        ErrorText = string.Join(", ", errs);

        if (s.Connected && s.FeedbackOk)
        {
            var p = s.Tcp.ToMmDeg();
            TcpX = $"{p.X:F2}"; TcpY = $"{p.Y:F2}"; TcpZ = $"{p.Z:F2}";
            TcpRx = $"{p.Rx:F2}"; TcpRy = $"{p.Ry:F2}"; TcpRz = $"{p.Rz:F2}";
            var d = s.JointsRad.Select(r => r * 180 / Math.PI).ToArray();
            J1 = $"{d[0]:F2}"; J2 = $"{d[1]:F2}"; J3 = $"{d[2]:F2}"; J4 = $"{d[3]:F2}"; J5 = $"{d[4]:F2}"; J6 = $"{d[5]:F2}";
        }

        if (_spaceMouse == null || !_spaceMouse.DeviceSeen) SpaceMouseText = "SpaceMouse: noch keine Eingabe (einmal bewegen)";
        else
        {
            var st = _spaceMouse.State;
            SpaceMouseText = $"SpaceMouse: T {st.Tx,4} {st.Ty,4} {st.Tz,4}  R {st.Rx,4} {st.Ry,4} {st.Rz,4}";
        }
    }

    partial void OnSpeedOverrideChanged(double value)
    {
        _robot.SpeedOverride = value / 100.0;
        Settings.SpeedOverridePercent = value;
    }

    // ================================================================ Jog

    void OnSpaceMouseChanged()
    {
        var b = _spaceMouse!.State.Buttons;
        uint pressed = b & ~_lastButtons;
        _lastButtons = b;
        if (!SpaceMouseActive || !_windowActive) return;
        if ((pressed & 1) != 0) _robot.SetGripper(!_robot.GripperClosed); // linke Taste: Greifer umschalten
        if ((pressed & 2) != 0) ToolFrame = !ToolFrame;                    // rechte Taste: Basis/Werkzeug
    }

    void JogTick()
    {
        if (ProgramRunning || !_windowActive) return;
        var frame = ToolFrame ? JogFrame.Tool : JogFrame.Base;

        // Bildschirmtasten haben Vorrang
        var held = HeldJogButton;
        if (held != null)
        {
            double sign = held.EndsWith('+') ? 1 : -1;
            string axis = held[..^1];
            if (axis.StartsWith('J'))
            {
                _robot.JogJoint(int.Parse(axis[1..]) - 1, sign);
                return;
            }
            Vec3 lin = Vec3.Zero, ang = Vec3.Zero;
            switch (axis)
            {
                case "X": lin = new(sign, 0, 0); break;
                case "Y": lin = new(0, sign, 0); break;
                case "Z": lin = new(0, 0, sign); break;
                case "Rx": ang = new(sign, 0, 0); break;
                case "Ry": ang = new(0, sign, 0); break;
                case "Rz": ang = new(0, 0, sign); break;
            }
            _robot.JogCartesian(lin, ang, frame);
            return;
        }

        if (SpaceMouseActive && _spaceMouse != null && (DateTime.UtcNow - _spaceMouse.LastReport).TotalMilliseconds < 500)
        {
            var (lin, ang) = SpaceMouseMapper.Map(_spaceMouse.State, Settings.SpaceMouse);
            if (lin.Length > 0 || ang.Length > 0)
            {
                _robot.JogCartesian(lin, ang, frame);
                return;
            }
        }
        if (_robot.Status.Motion == MotionState.Jog) _robot.StopJog();
    }

    public void ReleaseJogButton()
    {
        HeldJogButton = null;
        _robot.StopJog();
    }

    // ================================================================ Punkte

    void RebuildWaypoints()
    {
        var sel = SelectedWaypoint?.Model;
        Waypoints.Clear();
        WaypointNames.Clear();
        foreach (var w in _project.Waypoints)
        {
            var item = new WaypointItem(w);
            item.Refresh(_robot.Kinematics);
            Waypoints.Add(item);
            WaypointNames.Add(w.Name);
        }
        SelectedWaypoint = Waypoints.FirstOrDefault(w => w.Model == sel);
    }

    bool CanTeach()
    {
        var s = _robot.Status;
        if (!s.Connected || !s.FeedbackOk) { AddLog("Teachen nicht möglich: keine Verbindung zum Roboter"); return false; }
        if (!s.AllHomed) { AddLog("Teachen nicht möglich: Referenzfahrt fehlt"); return false; }
        return true;
    }

    [RelayCommand]
    void AddWaypoint()
    {
        if (!CanTeach()) return;
        var name = NewWaypointName.Trim();
        if (name.Length == 0) { AddLog("Bitte einen Namen eingeben"); return; }
        if (_project.FindWaypoint(name) != null) { AddLog($"Punkt '{name}' existiert schon – 'Überschreiben' verwenden"); return; }
        var wp = new Waypoint { Name = name, JointsDeg = _robot.CurrentJoints().Select(r => r * 180 / Math.PI).ToArray() };
        _project.Waypoints.Add(wp);
        SaveProject();
        RebuildWaypoints();
        SelectedWaypoint = Waypoints.FirstOrDefault(w => w.Model == wp);
        AddLog($"Punkt '{name}' gespeichert");
        NewWaypointName = NextName(name);
    }

    static string NextName(string name)
    {
        int i = name.Length;
        while (i > 0 && char.IsDigit(name[i - 1])) i--;
        if (i < name.Length && int.TryParse(name[i..], out int n)) return name[..i] + (n + 1);
        return name + " 2";
    }

    [RelayCommand]
    void OverwriteWaypoint()
    {
        if (SelectedWaypoint == null || !CanTeach()) return;
        var r = MessageBox.Show($"Punkt '{SelectedWaypoint.Name}' mit der aktuellen Position überschreiben?", "Punkt überschreiben",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        SelectedWaypoint.Model.JointsDeg = _robot.CurrentJoints().Select(v => v * 180 / Math.PI).ToArray();
        SaveProject();
        SelectedWaypoint.Refresh(_robot.Kinematics);
        AddLog($"Punkt '{SelectedWaypoint.Name}' überschrieben");
    }

    [RelayCommand]
    void RenameWaypoint()
    {
        if (SelectedWaypoint == null) return;
        var name = NewWaypointName.Trim();
        if (name.Length == 0 || _project.FindWaypoint(name) != null) { AddLog("Name leer oder schon vergeben"); return; }
        var old = SelectedWaypoint.Model.Name;
        SelectedWaypoint.Model.Name = name;
        // Programme mitziehen
        foreach (var p in _project.Programs)
            foreach (var s in p.Steps)
            {
                if (s is MoveJStep mj && mj.Waypoint == old) mj.Waypoint = name;
                if (s is MoveLStep ml && ml.Waypoint == old) ml.Waypoint = name;
            }
        SaveProject();
        RebuildWaypoints();
        RebuildSteps();
        AddLog($"Punkt '{old}' → '{name}'");
    }

    [RelayCommand]
    void DeleteWaypoint()
    {
        if (SelectedWaypoint == null) return;
        var name = SelectedWaypoint.Name;
        bool used = _project.Programs.Any(p => p.Steps.Any(s => s is MoveJStep mj && mj.Waypoint == name || s is MoveLStep ml && ml.Waypoint == name));
        var msg = used ? $"Punkt '{name}' wird in Programmen verwendet. Trotzdem löschen?" : $"Punkt '{name}' löschen?";
        if (MessageBox.Show(msg, "Punkt löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _project.Waypoints.Remove(SelectedWaypoint.Model);
        SaveProject();
        RebuildWaypoints();
    }

    /// <summary>Anfahren per Gedrückthalten (wie "Move here" am UR). Loslassen stoppt sanft.</summary>
    public async void StartMoveToSelected(bool linear)
    {
        if (SelectedWaypoint == null) return;
        var wp = SelectedWaypoint.Model;
        if (linear)
            await _robot.MoveLAsync(_robot.Kinematics.Forward(wp.JointsRad), 0.1 * MoveSpeedPercent / 100.0); // 100 % = 100 mm/s
        else
            await _robot.MoveJAsync(wp.JointsRad, MoveSpeedPercent / 100.0);
    }

    public void StopMoveToSelected() => _robot.StopMotion();

    // ================================================================ Programme

    void RebuildPrograms()
    {
        var sel = SelectedProgram?.Model;
        Programs.Clear();
        foreach (var p in _project.Programs) Programs.Add(new ProgramItem(p));
        SelectedProgram = Programs.FirstOrDefault(p => p.Model == sel) ?? Programs.FirstOrDefault();
    }

    partial void OnSelectedProgramChanged(ProgramItem? value)
    {
        ProgramName = value?.Name ?? "";
        RebuildSteps();
    }

    void RebuildSteps()
    {
        var sel = SelectedStep?.Model;
        Steps.Clear();
        if (SelectedProgram == null) return;
        int n = 1;
        foreach (var s in SelectedProgram.Model.Steps) Steps.Add(new StepItem(s) { Number = n++ });
        SelectedStep = Steps.FirstOrDefault(s => s.Model == sel);
    }

    partial void OnSelectedStepChanged(StepItem? value)
    {
        switch (value?.Model)
        {
            case MoveJStep m: StepWaypoint = m.Waypoint; StepSpeed = m.SpeedPercent; StepWaitMs = 0; break;
            case MoveLStep l: StepWaypoint = l.Waypoint; StepSpeed = l.SpeedMmS; StepWaitMs = 0; break;
            case GripperStep g: StepWaypoint = null; StepSpeed = 0; StepWaitMs = g.WaitMs; break;
            case WaitStep w: StepWaypoint = null; StepSpeed = 0; StepWaitMs = w.Ms; break;
        }
    }

    [RelayCommand]
    void NewProgram()
    {
        var p = new RobotProgram { Name = string.IsNullOrWhiteSpace(ProgramName) || _project.Programs.Any(x => x.Name == ProgramName) ? $"Programm {_project.Programs.Count + 1}" : ProgramName.Trim() };
        _project.Programs.Add(p);
        SaveProject();
        RebuildPrograms();
        SelectedProgram = Programs.First(x => x.Model == p);
    }

    [RelayCommand]
    void RenameProgram()
    {
        if (SelectedProgram == null || string.IsNullOrWhiteSpace(ProgramName)) return;
        SelectedProgram.Model.Name = ProgramName.Trim();
        SelectedProgram.Refresh();
        SaveProject();
    }

    [RelayCommand]
    void DeleteProgram()
    {
        if (SelectedProgram == null) return;
        if (MessageBox.Show($"Programm '{SelectedProgram.Name}' löschen?", "Programm löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _project.Programs.Remove(SelectedProgram.Model);
        SaveProject();
        RebuildPrograms();
    }

    void AddStep(ProgramStep step)
    {
        if (SelectedProgram == null) { AddLog("Erst ein Programm anlegen"); return; }
        var list = SelectedProgram.Model.Steps;
        int idx = SelectedStep != null ? list.IndexOf(SelectedStep.Model) + 1 : list.Count;
        list.Insert(idx, step);
        SaveProject();
        RebuildSteps();
        SelectedStep = Steps.FirstOrDefault(s => s.Model == step);
    }

    string? WaypointForNewStep() => SelectedWaypoint?.Name ?? _project.Waypoints.FirstOrDefault()?.Name;

    [RelayCommand]
    void AddMoveJ()
    {
        var wp = WaypointForNewStep();
        if (wp == null) { AddLog("Erst einen Punkt teachen"); return; }
        AddStep(new MoveJStep { Waypoint = wp, SpeedPercent = 50 });
    }

    [RelayCommand]
    void AddMoveL()
    {
        var wp = WaypointForNewStep();
        if (wp == null) { AddLog("Erst einen Punkt teachen"); return; }
        AddStep(new MoveLStep { Waypoint = wp, SpeedMmS = 50 });
    }

    [RelayCommand] void AddGripperClose() => AddStep(new GripperStep { Close = true });
    [RelayCommand] void AddGripperOpen() => AddStep(new GripperStep { Close = false });
    [RelayCommand] void AddWait() => AddStep(new WaitStep { Ms = 500 });

    [RelayCommand]
    void ApplyStep()
    {
        if (SelectedStep == null) return;
        switch (SelectedStep.Model)
        {
            case MoveJStep m:
                if (StepWaypoint != null) m.Waypoint = StepWaypoint;
                m.SpeedPercent = Math.Clamp(StepSpeed, 1, 100);
                break;
            case MoveLStep l:
                if (StepWaypoint != null) l.Waypoint = StepWaypoint;
                l.SpeedMmS = Math.Clamp(StepSpeed, 1, 100);
                break;
            case GripperStep g: g.WaitMs = Math.Max(0, StepWaitMs); break;
            case WaitStep w: w.Ms = Math.Max(0, StepWaitMs); break;
        }
        SelectedStep.Refresh();
        SaveProject();
    }

    [RelayCommand]
    void StepUp() => MoveStep(-1);

    [RelayCommand]
    void StepDown() => MoveStep(1);

    void MoveStep(int dir)
    {
        if (SelectedProgram == null || SelectedStep == null) return;
        var list = SelectedProgram.Model.Steps;
        int i = list.IndexOf(SelectedStep.Model), j = i + dir;
        if (j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        SaveProject();
        RebuildSteps();
    }

    [RelayCommand]
    void DeleteStep()
    {
        if (SelectedProgram == null || SelectedStep == null) return;
        SelectedProgram.Model.Steps.Remove(SelectedStep.Model);
        SaveProject();
        RebuildSteps();
    }

    [RelayCommand]
    async Task RunProgram()
    {
        if (SelectedProgram == null || ProgramRunning) return;
        _programCts = new CancellationTokenSource();
        ProgramRunning = true;
        try
        {
            await _runner.RunAsync(SelectedProgram.Model, _project, _programCts.Token);
        }
        finally
        {
            ProgramRunning = false;
            foreach (var s in Steps) s.IsActive = false;
        }
    }

    [RelayCommand]
    void StopProgram()
    {
        _programCts?.Cancel();
        _robot.StopMotion();
    }

    // ================================================================ Einstellungen

    void LoadSettingsIntoEditor()
    {
        var s = Settings;
        ToolX = s.Tool.X; ToolY = s.Tool.Y; ToolZ = s.Tool.Z; ToolRx = s.Tool.Rx; ToolRy = s.Tool.Ry; ToolRz = s.Tool.Rz;
        GripperOutput = s.GripperOutput;
        GripperClosedWhenHigh = s.GripperClosedWhenHigh;
        JogLinear = s.JogMaxLinearMmS; JogAngular = s.JogMaxAngularDegS; JogJoint = s.JogMaxJointDegS;
        var m = s.SpaceMouse;
        SmDeadband = m.Deadband * 100; SmExponent = m.Exponent; SmDominant = m.DominantAxisOnly;
        SmTranslation = m.TranslationEnabled; SmRotation = m.RotationEnabled;
        SmInvX = m.TranslationSign[0] < 0; SmInvY = m.TranslationSign[1] < 0; SmInvZ = m.TranslationSign[2] < 0;
        SmInvRx = m.RotationSign[0] < 0; SmInvRy = m.RotationSign[1] < 0; SmInvRz = m.RotationSign[2] < 0;
    }

    void ApplySettingsToRobot()
    {
        var s = Settings;
        _robot.Kinematics.Tool = Pose.FromMmDeg(s.Tool);
        _robot.GripperOutput = s.GripperOutput is 1 or 2 ? s.GripperOutput : 1;
        _robot.GripperClosedWhenHigh = s.GripperClosedWhenHigh;
        _robot.JogMaxLinear = s.JogMaxLinearMmS / 1000.0;
        _robot.JogMaxAngular = s.JogMaxAngularDegS * Math.PI / 180;
        _robot.JogMaxJoint = s.JogMaxJointDegS * Math.PI / 180;
        _robot.SpeedOverride = s.SpeedOverridePercent / 100.0;
    }

    [RelayCommand]
    void SaveSettings()
    {
        var s = Settings;
        s.Tool = new PoseMmDeg(ToolX, ToolY, ToolZ, ToolRx, ToolRy, ToolRz);
        s.GripperOutput = GripperOutput is 1 or 2 ? GripperOutput : 1;
        s.GripperClosedWhenHigh = GripperClosedWhenHigh;
        s.JogMaxLinearMmS = Math.Clamp(JogLinear, 1, 150);
        s.JogMaxAngularDegS = Math.Clamp(JogAngular, 1, 90);
        s.JogMaxJointDegS = Math.Clamp(JogJoint, 1, 90);
        var m = s.SpaceMouse;
        m.Deadband = Math.Clamp(SmDeadband / 100, 0, 0.5);
        m.Exponent = Math.Clamp(SmExponent, 1, 4);
        m.DominantAxisOnly = SmDominant;
        m.TranslationEnabled = SmTranslation;
        m.RotationEnabled = SmRotation;
        m.TranslationSign = new[] { SmInvX ? -1 : 1, SmInvY ? -1 : 1, SmInvZ ? -1 : 1 };
        m.RotationSign = new[] { SmInvRx ? -1 : 1, SmInvRy ? -1 : 1, SmInvRz ? -1 : 1 };
        s.Save();
        ApplySettingsToRobot();
        RebuildWaypoints(); // Anzeige mit neuem TCP
        AddLog("Einstellungen gespeichert");
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        _jogTimer.Stop();
        _programCts?.Cancel();
        _robot.Dispose();
        _spaceMouse?.Dispose();
        try { Settings.Save(); } catch { }
    }
}
