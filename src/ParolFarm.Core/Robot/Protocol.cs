namespace ParolFarm.Core.Robot;

/// <summary>Befehlscodes der PAROL6-Platine (aus dem Original-Commander).</summary>
public static class BoardCommand
{
    public const byte Home = 100;
    public const byte Enable = 101;
    public const byte Disable = 102;
    public const byte ClearError = 103;
    /// <summary>Geschwindigkeitsregelung (Speed_out).</summary>
    public const byte Speed = 123;
    /// <summary>Positionsregelung mit Geschwindigkeits-Vorsteuerung (Position_out + Speed_out).</summary>
    public const byte Position = 156;
    /// <summary>Kein Befehl / Halten.</summary>
    public const byte Idle = 255;
}

/// <summary>Daten PC -> Roboter.</summary>
public sealed class CommandFrame
{
    public int[] Position { get; } = new int[6];   // Schritte
    public int[] Speed { get; } = new int[6];      // Schritte/s
    public byte Command { get; set; } = BoardCommand.Idle;
    /// <summary>Bitfeld (8), Index 0 = MSB. Original sendet immer alle 1.</summary>
    public bool[] AffectedJoints { get; } = Enumerable.Repeat(true, 8).ToArray();
    /// <summary>Bitfeld IN1, IN2, OUT1, OUT2, ESTOP, ... (Index 0 = MSB).</summary>
    public bool[] InOut { get; } = new bool[8];
    public byte Timeout { get; set; }
    public int GripperPosition { get; set; }
    public int GripperSpeed { get; set; }
    public int GripperCurrent { get; set; }
    public byte GripperCommand { get; set; }
    public byte GripperMode { get; set; }
    public byte GripperId { get; set; }

    public CommandFrame Clone()
    {
        var c = new CommandFrame
        {
            Command = Command, Timeout = Timeout, GripperPosition = GripperPosition, GripperSpeed = GripperSpeed,
            GripperCurrent = GripperCurrent, GripperCommand = GripperCommand, GripperMode = GripperMode, GripperId = GripperId,
        };
        Position.CopyTo(c.Position, 0); Speed.CopyTo(c.Speed, 0);
        AffectedJoints.CopyTo(c.AffectedJoints, 0); InOut.CopyTo(c.InOut, 0);
        return c;
    }
}

/// <summary>Daten Roboter -> PC.</summary>
public sealed class FeedbackFrame
{
    public int[] Position { get; } = new int[6];
    public int[] Speed { get; } = new int[6];
    public bool[] Homed { get; } = new bool[8];
    /// <summary>IN1, IN2, OUT1, OUT2, ESTOP (1 = nicht gedrückt), ...</summary>
    public bool[] InOut { get; } = new bool[8];
    public bool[] TemperatureError { get; } = new bool[8];
    public bool[] PositionError { get; } = new bool[8];
    public int Timing { get; set; }
    public byte TimeoutError { get; set; }
    public byte Extra { get; set; }
    public byte GripperId { get; set; }
    public int GripperPosition { get; set; }
    public int GripperSpeed { get; set; }
    public int GripperCurrent { get; set; }
    public byte GripperStatus { get; set; }
    public byte GripperObjectDetection { get; set; }

    public bool EStopReleased => InOut[IoIndex.EStop];
}

public static class IoIndex
{
    public const int In1 = 0, In2 = 1, Out1 = 2, Out2 = 3, EStop = 4;
}

/// <summary>
/// Paketformat: FF FF FF | LEN | Daten ... | CRC | 01 02.
/// LEN zählt alle Bytes nach dem LEN-Byte (inkl. CRC und Endbytes).
/// </summary>
public static class Protocol
{
    public const byte Start = 0xFF;
    public const byte End1 = 0x01, End2 = 0x02;
    public const byte CommandLength = 52;
    public const byte FeedbackLength = 56;
    const byte DummyCrc = 228; // Original sendet festen Wert

    static void Put3(List<byte> b, int v)
    {
        uint u = (uint)v & 0xFFFFFF;
        b.Add((byte)(u >> 16)); b.Add((byte)(u >> 8)); b.Add((byte)u);
    }

    static void Put2(List<byte> b, int v)
    {
        uint u = (uint)v & 0xFFFF;
        b.Add((byte)(u >> 8)); b.Add((byte)u);
    }

    public static byte FuseBits(IReadOnlyList<bool> bits)
    {
        int n = 0;
        for (int i = 0; i < 8; i++) n = (n << 1) | (bits[i] ? 1 : 0);
        return (byte)n;
    }

    public static void SplitBits(byte value, bool[] target)
    {
        for (int i = 0; i < 8; i++) target[i] = ((value >> (7 - i)) & 1) == 1;
    }

    public static int Signed24(byte a, byte b, byte c)
    {
        int v = (a << 16) | (b << 8) | c;
        return v >= 1 << 23 ? v - (1 << 24) : v;
    }

    public static int Signed16(byte a, byte b)
    {
        int v = (a << 8) | b;
        return v >= 1 << 15 ? v - (1 << 16) : v;
    }

    public static byte[] PackCommand(CommandFrame f)
    {
        var b = new List<byte>(56) { Start, Start, Start, CommandLength };
        for (int i = 0; i < 6; i++) Put3(b, f.Position[i]);
        for (int i = 0; i < 6; i++) Put3(b, f.Speed[i]);
        b.Add(f.Command);
        b.Add(FuseBits(f.AffectedJoints));
        b.Add(FuseBits(f.InOut));
        b.Add(f.Timeout);
        Put2(b, f.GripperPosition);
        Put2(b, f.GripperSpeed);
        Put2(b, f.GripperCurrent);
        b.Add(f.GripperCommand);
        b.Add(f.GripperMode);
        b.Add(f.GripperId);
        b.Add(DummyCrc);
        b.Add(End1); b.Add(End2);
        return b.ToArray();
    }

    /// <summary>Dekodiert die Nutzdaten (nach dem LEN-Byte) eines Rückmeldepakets.</summary>
    public static FeedbackFrame UnpackFeedback(ReadOnlySpan<byte> d)
    {
        var f = new FeedbackFrame();
        for (int i = 0; i < 6; i++) f.Position[i] = Signed24(d[i * 3], d[i * 3 + 1], d[i * 3 + 2]);
        for (int i = 0; i < 6; i++) f.Speed[i] = Signed24(d[18 + i * 3], d[19 + i * 3], d[20 + i * 3]);
        SplitBits(d[36], f.Homed);
        SplitBits(d[37], f.InOut);
        SplitBits(d[38], f.TemperatureError);
        SplitBits(d[39], f.PositionError);
        f.Timing = (d[40] << 8) | d[41];
        f.TimeoutError = d[42];
        f.Extra = d[43];
        f.GripperId = d[44];
        f.GripperPosition = Signed16(d[45], d[46]);
        f.GripperSpeed = Signed16(d[47], d[48]);
        f.GripperCurrent = Signed16(d[49], d[50]);
        f.GripperStatus = d[51];
        f.GripperObjectDetection = d[52];
        return f;
    }

    /// <summary>Erzeugt ein Rückmeldepaket (für Simulator und Tests).</summary>
    public static byte[] PackFeedback(FeedbackFrame f)
    {
        var b = new List<byte>(60) { Start, Start, Start, FeedbackLength };
        for (int i = 0; i < 6; i++) Put3(b, f.Position[i]);
        for (int i = 0; i < 6; i++) Put3(b, f.Speed[i]);
        b.Add(FuseBits(f.Homed));
        b.Add(FuseBits(f.InOut));
        b.Add(FuseBits(f.TemperatureError));
        b.Add(FuseBits(f.PositionError));
        Put2(b, f.Timing);
        b.Add(f.TimeoutError);
        b.Add(f.Extra);
        b.Add(f.GripperId);
        Put2(b, f.GripperPosition);
        Put2(b, f.GripperSpeed);
        Put2(b, f.GripperCurrent);
        b.Add(f.GripperStatus);
        b.Add(f.GripperObjectDetection);
        b.Add(DummyCrc);
        b.Add(End1); b.Add(End2);
        return b.ToArray();
    }

    /// <summary>Dekodiert die Nutzdaten eines Befehlspakets (für Simulator und Tests).</summary>
    public static CommandFrame UnpackCommand(ReadOnlySpan<byte> d)
    {
        var f = new CommandFrame();
        for (int i = 0; i < 6; i++) f.Position[i] = Signed24(d[i * 3], d[i * 3 + 1], d[i * 3 + 2]);
        for (int i = 0; i < 6; i++) f.Speed[i] = Signed24(d[18 + i * 3], d[19 + i * 3], d[20 + i * 3]);
        f.Command = d[36];
        SplitBits(d[37], f.AffectedJoints);
        SplitBits(d[38], f.InOut);
        f.Timeout = d[39];
        f.GripperPosition = Signed16(d[40], d[41]);
        f.GripperSpeed = Signed16(d[42], d[43]);
        f.GripperCurrent = Signed16(d[44], d[45]);
        f.GripperCommand = d[46];
        f.GripperMode = d[47];
        f.GripperId = d[48];
        return f;
    }
}

/// <summary>
/// Zustandsautomat zum Finden von Paketen im Bytestrom (wie im Original:
/// drei Startbytes, Längenbyte, Daten, Prüfung der Endbytes).
/// </summary>
public sealed class FrameParser
{
    int _startCount;
    int _len = -1;
    readonly byte[] _buf = new byte[256];
    int _count;

    /// <summary>Verarbeitet ein Byte. Gibt die Nutzdaten zurück, wenn ein gültiges Paket komplett ist.</summary>
    public byte[]? Push(byte b)
    {
        if (_len < 0)
        {
            if (_startCount == 3)
            {
                // Weitere 0xFF: Fenster verschieben (Länge ist nie 0xFF)
                if (b == Protocol.Start) return null;
                if (b < 3) { _startCount = 0; return null; }
                _len = b; _count = 0;
                return null;
            }
            _startCount = b == Protocol.Start ? _startCount + 1 : 0;
            return null;
        }
        _buf[_count++] = b;
        if (_count < _len) return null;
        byte[]? result = null;
        if (_buf[_len - 2] == Protocol.End1 && _buf[_len - 1] == Protocol.End2)
            result = _buf.AsSpan(0, _len).ToArray();
        _len = -1; _startCount = 0; _count = 0;
        return result;
    }
}
