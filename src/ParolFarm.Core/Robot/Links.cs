namespace ParolFarm.Core.Robot;

using System.IO.Ports;
using ParolFarm.Core.Kinematics;

/// <summary>Verbindung zur Robotersteuerung (echte Platine oder Simulator).</summary>
public interface IRobotLink : IDisposable
{
    string Name { get; }
    bool IsOpen { get; }
    void Open();
    /// <summary>Sendet ein Befehlspaket.</summary>
    void Send(CommandFrame frame);
    /// <summary>Liest alle verfügbaren Rückmeldungen und gibt die neueste zurück (oder null).</summary>
    FeedbackFrame? ReadLatest();
}

/// <summary>USB-Verbindung zur originalen PAROL6-Platine (virtueller COM-Port).</summary>
public sealed class SerialRobotLink : IRobotLink
{
    readonly SerialPort _port;
    readonly FrameParser _parser = new();
    readonly byte[] _rx = new byte[4096];

    public SerialRobotLink(string portName)
    {
        _port = new SerialPort(portName, 3_000_000)
        {
            ReadTimeout = 1,
            WriteTimeout = 50,
            DtrEnable = true,
        };
    }

    public string Name => _port.PortName;
    public bool IsOpen => _port.IsOpen;

    public static string[] AvailablePorts() => SerialPort.GetPortNames().OrderBy(p => p).ToArray();

    public void Open() => _port.Open();

    public void Send(CommandFrame frame)
    {
        var bytes = Protocol.PackCommand(frame);
        _port.Write(bytes, 0, bytes.Length);
    }

    public FeedbackFrame? ReadLatest()
    {
        FeedbackFrame? latest = null;
        while (_port.BytesToRead > 0)
        {
            int n = _port.Read(_rx, 0, System.Math.Min(_rx.Length, _port.BytesToRead));
            for (int i = 0; i < n; i++)
            {
                var payload = _parser.Push(_rx[i]);
                if (payload != null && payload.Length >= Protocol.FeedbackLength)
                    latest = Protocol.UnpackFeedback(payload);
            }
        }
        return latest;
    }

    public void Dispose()
    {
        try { if (_port.IsOpen) _port.Close(); } catch { /* ignorieren */ }
        _port.Dispose();
    }
}

/// <summary>
/// Simuliert die PAROL6-Platine auf Byte-Ebene: Befehle werden echt kodiert/dekodiert,
/// damit das Protokoll mitgetestet wird.
/// </summary>
public sealed class SimulatedRobotLink : IRobotLink
{
    readonly FrameParser _parser = new();
    readonly Queue<byte[]> _outgoing = new();
    readonly double[] _pos = new double[6];
    readonly double[] _vel = new double[6];
    bool _homed;
    bool _enabled;
    DateTime? _homingUntil;
    readonly bool[] _outputs = new bool[2];
    readonly object _lock = new();

    public string Name => "Simulation";
    public bool IsOpen { get; private set; }

    /// <summary>Simulierter Not-Aus (true = gedrückt).</summary>
    public bool EStopPressed { get; set; }

    /// <summary>Regeltakt der Simulation (s).</summary>
    public double Dt { get; set; } = 0.01;

    public SimulatedRobotLink()
    {
        var standby = Parol6Model.StandbyRad;
        for (int j = 0; j < 6; j++) _pos[j] = Parol6Model.RadToSteps(standby[j], j);
    }

    public void Open() => IsOpen = true;

    public void Send(CommandFrame frame)
    {
        var bytes = Protocol.PackCommand(frame);
        lock (_lock)
        {
            foreach (var b in bytes)
            {
                var payload = _parser.Push(b);
                if (payload != null) Process(Protocol.UnpackCommand(payload));
            }
        }
    }

    void Process(CommandFrame c)
    {
        if (EStopPressed) _enabled = false;
        switch (c.Command)
        {
            case BoardCommand.Home:
                if (!EStopPressed) _homingUntil = DateTime.UtcNow.AddSeconds(1.5);
                break;
            case BoardCommand.Enable:
                if (!EStopPressed) _enabled = true;
                break;
            case BoardCommand.Disable:
                _enabled = false;
                break;
            case BoardCommand.Position when _enabled && _homingUntil == null:
                for (int j = 0; j < 6; j++)
                {
                    // Geschwindigkeitsbegrenzung wie ein Schrittmotor
                    double max = Parol6Model.MaxSpeedSteps[j] * Dt * 1.05;
                    double d = System.Math.Clamp(c.Position[j] - _pos[j], -max, max);
                    _vel[j] = d / Dt;
                    _pos[j] += d;
                }
                break;
            case BoardCommand.Speed when _enabled && _homingUntil == null:
                for (int j = 0; j < 6; j++)
                {
                    _vel[j] = c.Speed[j];
                    _pos[j] += c.Speed[j] * Dt;
                }
                break;
            default:
                for (int j = 0; j < 6; j++) _vel[j] = 0;
                break;
        }
        if (_homingUntil != null && DateTime.UtcNow >= _homingUntil)
        {
            var standby = Parol6Model.StandbyRad;
            for (int j = 0; j < 6; j++) _pos[j] = Parol6Model.RadToSteps(standby[j], j);
            _homed = true;
            _homingUntil = null;
        }
        _outputs[0] = c.InOut[IoIndex.Out1];
        _outputs[1] = c.InOut[IoIndex.Out2];

        var f = new FeedbackFrame();
        for (int j = 0; j < 6; j++)
        {
            f.Position[j] = (int)System.Math.Round(_pos[j]);
            f.Speed[j] = (int)System.Math.Round(_vel[j]);
            f.Homed[j] = _homed;
        }
        f.InOut[IoIndex.Out1] = _outputs[0];
        f.InOut[IoIndex.Out2] = _outputs[1];
        f.InOut[IoIndex.EStop] = !EStopPressed;
        _outgoing.Enqueue(Protocol.PackFeedback(f));
        while (_outgoing.Count > 10) _outgoing.Dequeue();
    }

    public FeedbackFrame? ReadLatest()
    {
        lock (_lock)
        {
            FeedbackFrame? latest = null;
            var parser = new FrameParser();
            while (_outgoing.Count > 0)
                foreach (var b in _outgoing.Dequeue())
                {
                    var p = parser.Push(b);
                    if (p != null) latest = Protocol.UnpackFeedback(p);
                }
            return latest;
        }
    }

    public void Dispose() => IsOpen = false;
}
