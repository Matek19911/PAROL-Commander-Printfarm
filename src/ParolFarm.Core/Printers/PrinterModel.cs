namespace ParolFarm.Core.Printers;

using System.Text.Json.Nodes;

public enum PrinterKind { X2D, P2S, P1P, Other, Simulation }

/// <summary>Konfiguration eines Druckers (gespeichert lokal in %APPDATA%\ParolFarm\farm.json).</summary>
public sealed class PrinterConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Drucker";
    public PrinterKind Kind { get; set; } = PrinterKind.X2D;
    public string Host { get; set; } = "";
    public string Serial { get; set; } = "";
    /// <summary>LAN-Zugangscode vom Druckerdisplay. Wird nur lokal gespeichert.</summary>
    public string AccessCode { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Dieser Drucker wird vom PAROL6 abgeräumt.</summary>
    public bool HasRobot { get; set; }
    /// <summary>Name des Roboterprogramms für die Entnahme.</summary>
    public string? RemovalProgram { get; set; }
    /// <summary>Bett-Temperatur, unter der das Teil entnommen wird (°C).</summary>
    public double ReleaseTemperatureC { get; set; } = 30;
    /// <summary>G-Code, der nach Druckende gesendet wird (Bett nach unten). Leer = nichts senden.</summary>
    public string BedDownGcode { get; set; } = "G90\nG1 Z200 F1200";
    /// <summary>Wartezeit nach dem Bett-G-Code, bis die Bewegung sicher fertig ist (s).</summary>
    public int BedMoveWaitSeconds { get; set; } = 20;

    public override string ToString() => Name;
}

/// <summary>Druckerzustand, aus den MQTT-Meldungen zusammengesetzt.</summary>
public sealed record PrinterStatus
{
    public bool Online { get; init; }
    /// <summary>IDLE, PREPARE, RUNNING, PAUSE, FINISH, FAILED, ... (leer = unbekannt)</summary>
    public string GcodeState { get; init; } = "";
    public double? BedTemp { get; init; }
    public double? BedTarget { get; init; }
    public double? NozzleTemp { get; init; }
    public int Progress { get; init; }
    public int RemainingMinutes { get; init; }
    public int Layer { get; init; }
    public int TotalLayers { get; init; }
    public string SubtaskName { get; init; } = "";
    public long PrintError { get; init; }
    public DateTime LastUpdateUtc { get; init; }
    public string? ConnectionError { get; init; }

    public bool IsPrinting => GcodeState is "PREPARE" or "RUNNING" or "PAUSE" or "SLICING" or "INIT";
    public bool IsFinished => GcodeState == "FINISH";
    public bool IsFailed => GcodeState == "FAILED";
    public bool IsIdleLike => GcodeState is "IDLE" or "FINISH" or "FAILED";
}

/// <summary>Führt die (teils inkrementellen) MQTT-Berichte zu einem Gesamtzustand zusammen.</summary>
public sealed class BambuStatusParser
{
    readonly JsonObject _print = new();

    /// <summary>Verarbeitet eine Nachricht von device/&lt;serial&gt;/report.</summary>
    public bool Apply(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch { return false; }
        if (root?["print"] is not JsonObject print) return false;
        Merge(_print, print);
        return true;
    }

    static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject so && target[key] is JsonObject to) Merge(to, so);
            else target[key] = value?.DeepClone();
        }
    }

    static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out string? s) && double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    public PrinterStatus Build(bool online, DateTime lastUpdate)
    {
        double? bed = Num(_print["bed_temper"]);
        double? bedTarget = Num(_print["bed_target_temper"]);
        // Neuere Firmware (X2D, H2D, ...): unteres Wort = Ist, oberes Wort = Soll
        if (Num(_print["device"]?["bed"]?["info"]?["temp"]) is double packed)
        {
            long p = (long)packed;
            bed = p & 0xFFFF;
            bedTarget = (p >> 16) & 0xFFFF;
        }
        double? nozzle = Num(_print["nozzle_temper"]);
        if (_print["device"]?["extruder"]?["info"] is JsonArray ex && ex.Count > 0 && Num(ex[0]?["temp"]) is double nt)
            nozzle = (long)nt & 0xFFFF;

        return new PrinterStatus
        {
            Online = online,
            GcodeState = (_print["gcode_state"]?.GetValue<string>() ?? "").ToUpperInvariant(),
            BedTemp = bed,
            BedTarget = bedTarget,
            NozzleTemp = nozzle,
            Progress = (int)(Num(_print["mc_percent"]) ?? 0),
            RemainingMinutes = (int)(Num(_print["mc_remaining_time"]) ?? 0),
            Layer = (int)(Num(_print["layer_num"]) ?? 0),
            TotalLayers = (int)(Num(_print["total_layer_num"]) ?? 0),
            SubtaskName = _print["subtask_name"]?.ToString() ?? "",
            PrintError = (long)(Num(_print["print_error"]) ?? 0),
            LastUpdateUtc = lastUpdate,
        };
    }
}

/// <summary>Bausteine für MQTT-Befehle (Topic device/&lt;serial&gt;/request).</summary>
public static class BambuCommands
{
    static int _seq;
    static string Seq() => Interlocked.Increment(ref _seq).ToString();

    public static string PushAll() =>
        new JsonObject { ["pushing"] = new JsonObject { ["sequence_id"] = Seq(), ["command"] = "pushall" } }.ToJsonString();

    public static string Gcode(string gcode)
    {
        var text = string.Join("\n", gcode.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)) + "\n";
        return new JsonObject
        {
            ["print"] = new JsonObject { ["sequence_id"] = Seq(), ["command"] = "gcode_line", ["param"] = text }
        }.ToJsonString();
    }

    public static string Simple(string command) =>
        new JsonObject { ["print"] = new JsonObject { ["sequence_id"] = Seq(), ["command"] = command } }.ToJsonString();

    /// <summary>Startet eine per FTPS hochgeladene .3mf-Datei.</summary>
    public static string ProjectFile(PrinterKind kind, string remoteFileName, int plate, bool useAms, bool bedLeveling)
    {
        string url = "ftp:///" + remoteFileName;
        return new JsonObject
        {
            ["print"] = new JsonObject
            {
                ["sequence_id"] = Seq(),
                ["command"] = "project_file",
                ["param"] = $"Metadata/plate_{plate}.gcode",
                ["url"] = url,
                ["subtask_name"] = Path.GetFileNameWithoutExtension(remoteFileName),
                ["bed_type"] = "auto",
                ["timelapse"] = false,
                ["bed_leveling"] = bedLeveling,
                ["auto_bed_leveling"] = bedLeveling ? 1 : 0,
                ["flow_cali"] = false,
                ["extrude_cali_flag"] = 0,
                ["vibration_cali"] = false,
                ["layer_inspect"] = false,
                ["use_ams"] = useAms,
                ["ams_mapping"] = new JsonArray(0),
                ["profile_id"] = "0",
                ["project_id"] = "0",
                ["subtask_id"] = "0",
                ["task_id"] = "0",
            }
        }.ToJsonString();
    }
}
