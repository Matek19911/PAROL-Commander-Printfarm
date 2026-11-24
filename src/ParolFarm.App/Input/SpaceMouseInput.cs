using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ParolFarm.Core.Settings;

namespace ParolFarm.App.Input;

/// <summary>
/// Liest eine 3Dconnexion SpaceMouse direkt über Windows Raw Input (HID) – ohne 3DxWare-Treiber.
/// Eingaben kommen nur an, solange das Programmfenster im Vordergrund ist (Sicherheit).
/// </summary>
public sealed class SpaceMouseInput : IDisposable
{
    const int WM_INPUT = 0x00FF;
    const uint RID_INPUT = 0x10000003;
    const uint RIM_TYPEHID = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    HwndSource? _source;
    public SpaceMouseState State { get; } = new();
    public DateTime LastReport { get; private set; } = DateTime.MinValue;
    public bool DeviceSeen { get; private set; }

    /// <summary>Wird bei jeder neuen Eingabe aufgerufen (UI-Thread).</summary>
    public event Action? Changed;

    public bool Attach(Window window)
    {
        _source = PresentationSource.FromVisual(window) as HwndSource;
        if (_source == null) return false;
        _source.AddHook(WndProc);
        var dev = new[]
        {
            // Generic Desktop / Multi-axis Controller (SpaceMouse)
            new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x08, Flags = 0, Target = _source.Handle },
        };
        return RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT) Read(lParam);
        return IntPtr.Zero;
    }

    void Read(IntPtr hRaw)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(hRaw, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRaw, RID_INPUT, buf, ref size, headerSize) != size) return;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            if (header.Type != RIM_TYPEHID) return;
            int sizeHid = Marshal.ReadInt32(buf, (int)headerSize);
            int count = Marshal.ReadInt32(buf, (int)headerSize + 4);
            int offset = (int)headerSize + 8;
            if (sizeHid <= 0 || count <= 0 || offset + sizeHid * count > size) return;
            var report = new byte[sizeHid];
            bool changed = false;
            for (int i = 0; i < count; i++)
            {
                Marshal.Copy(buf + offset + i * sizeHid, report, 0, sizeHid);
                changed |= State.ApplyReport(report);
            }
            if (changed)
            {
                DeviceSeen = true;
                LastReport = DateTime.UtcNow;
                Changed?.Invoke();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>Setzt die Achsen auf 0 (z. B. wenn das Fenster den Fokus verliert).</summary>
    public void Reset()
    {
        State.Tx = State.Ty = State.Tz = State.Rx = State.Ry = State.Rz = 0;
    }

    public void Dispose()
    {
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
