namespace ParolFarm.Core.Settings;

using System.Text.Json;
using ParolFarm.Core.Math;

public sealed class SpaceMouseSettings
{
    /// <summary>Totzone 0..1 (Anteil des Vollausschlags).</summary>
    public double Deadband { get; set; } = 0.10;
    /// <summary>Rohwert für Vollausschlag.</summary>
    public double FullScale { get; set; } = 350;
    /// <summary>Exponent der Kennlinie (1 = linear, 2 = feinfühliger um die Mitte).</summary>
    public double Exponent { get; set; } = 2.0;
    /// <summary>Nur die stärkste Achse verwenden.</summary>
    public bool DominantAxisOnly { get; set; }
    public bool TranslationEnabled { get; set; } = true;
    public bool RotationEnabled { get; set; } = true;

    // Vorzeichen je Achse (+1/-1), bezogen auf Roboter X,Y,Z / Rx,Ry,Rz
    public int[] TranslationSign { get; set; } = { 1, -1, -1 };
    public int[] RotationSign { get; set; } = { 1, -1, -1 };
}

/// <summary>Programmeinstellungen, gespeichert unter %APPDATA%\ParolFarm\settings.json.</summary>
public sealed class AppSettings
{
    public string? ComPort { get; set; }
    /// <summary>TCP-Versatz zum Flansch (mm / Rotationsvektor in Grad).</summary>
    public PoseMmDeg Tool { get; set; }
    public int GripperOutput { get; set; } = 1;
    public bool GripperClosedWhenHigh { get; set; } = true;
    public double SpeedOverridePercent { get; set; } = 25;
    public double JogMaxLinearMmS { get; set; } = 60;
    public double JogMaxAngularDegS { get; set; } = 30;
    public double JogMaxJointDegS { get; set; } = 30;
    public SpaceMouseSettings SpaceMouse { get; set; } = new();

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ParolFarm");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static string ProjectPath => Path.Combine(DataDirectory, "robot.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new();
        }
        catch { /* defekte Datei -> Standardwerte */ }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Json));
    }
}
