namespace ParolFarm.Core.Printers;

using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentFTP;
using MQTTnet;
using MQTTnet.Client;

/// <summary>Verbindung zu einem Drucker (echt oder simuliert).</summary>
public interface IPrinterConnection : IAsyncDisposable
{
    PrinterConfig Config { get; }
    PrinterStatus Status { get; }
    event Action<IPrinterConnection>? StatusChanged;
    Task StartAsync(CancellationToken ct);
    Task SendGcodeAsync(string gcode, CancellationToken ct);
    Task UploadAsync(string localPath, string remoteName, IProgress<double>? progress, CancellationToken ct);
    Task StartPrintAsync(string remoteName, int plate, bool useAms, bool bedLeveling, CancellationToken ct);
    Task StopPrintAsync(CancellationToken ct);
}

/// <summary>
/// Bambu Lab im LAN-Modus: MQTT über TLS (Port 8883, Benutzer bblp, Passwort = Zugangscode)
/// und implizites FTPS (Port 990) für den Datei-Upload.
/// </summary>
public sealed class BambuPrinterConnection : IPrinterConnection
{
    readonly BambuStatusParser _parser = new();
    readonly IMqttClient _mqtt;
    readonly CancellationTokenSource _cts = new();
    Task? _loop;
    DateTime _lastMsg = DateTime.MinValue;
    DateTime _lastPushAll = DateTime.MinValue;
    string? _error;

    public PrinterConfig Config { get; }
    public PrinterStatus Status { get; private set; } = new();
    public event Action<IPrinterConnection>? StatusChanged;

    string RequestTopic => $"device/{Config.Serial}/request";
    string ReportTopic => $"device/{Config.Serial}/report";

    public BambuPrinterConnection(PrinterConfig config)
    {
        Config = config;
        _mqtt = new MqttFactory().CreateMqttClient();
        _mqtt.ApplicationMessageReceivedAsync += e =>
        {
            var json = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            if (_parser.Apply(json))
            {
                _lastMsg = DateTime.UtcNow;
                Publish();
            }
            return Task.CompletedTask;
        };
    }

    void Publish()
    {
        Status = _parser.Build(_mqtt.IsConnected, _lastMsg) with { ConnectionError = _error };
        StatusChanged?.Invoke(this);
    }

    public Task StartAsync(CancellationToken ct)
    {
        _loop ??= Task.Run(() => Loop(_cts.Token));
        return Task.CompletedTask;
    }

    bool ValidateCertificate(MqttClientCertificateValidationEventArgs e)
    {
        // Der Drucker nutzt ein eigenes Zertifikat; wir prüfen, dass es zur Seriennummer passt.
        if (e.Certificate is not { } cert) return false;
        var cn = new X509Certificate2(cert).GetNameInfo(X509NameType.SimpleName, false);
        return string.Equals(cn, Config.Serial, StringComparison.OrdinalIgnoreCase);
    }

    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_mqtt.IsConnected)
                {
                    var options = new MqttClientOptionsBuilder()
                        .WithTcpServer(Config.Host, 8883)
                        .WithCredentials("bblp", Config.AccessCode)
                        .WithClientId("parolfarm-" + Guid.NewGuid().ToString("N")[..8])
                        .WithCleanSession()
                        .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                        .WithTlsOptions(o => o.UseTls().WithCertificateValidationHandler(ValidateCertificate))
                        .Build();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await _mqtt.ConnectAsync(options, timeout.Token);
                    await _mqtt.SubscribeAsync(ReportTopic, cancellationToken: ct);
                    _error = null;
                    await PublishRaw(BambuCommands.PushAll(), ct);
                    _lastPushAll = DateTime.UtcNow;
                }
                // Vollständigen Zustand nur selten anfordern (P1-Serie verträgt häufiges pushall schlecht)
                if ((DateTime.UtcNow - _lastPushAll).TotalMinutes > 5)
                {
                    await PublishRaw(BambuCommands.PushAll(), ct);
                    _lastPushAll = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _error = ex.Message;
            }
            Publish();
            try { await Task.Delay(_mqtt.IsConnected ? 2000 : 10000, ct); } catch { break; }
        }
    }

    Task PublishRaw(string json, CancellationToken ct)
    {
        if (!_mqtt.IsConnected) throw new InvalidOperationException($"{Config.Name}: nicht verbunden");
        var msg = new MqttApplicationMessageBuilder().WithTopic(RequestTopic).WithPayload(json).Build();
        return _mqtt.PublishAsync(msg, ct);
    }

    public Task SendGcodeAsync(string gcode, CancellationToken ct) => PublishRaw(BambuCommands.Gcode(gcode), ct);

    public Task StartPrintAsync(string remoteName, int plate, bool useAms, bool bedLeveling, CancellationToken ct) =>
        PublishRaw(BambuCommands.ProjectFile(Config.Kind, remoteName, plate, useAms, bedLeveling), ct);

    public Task StopPrintAsync(CancellationToken ct) => PublishRaw(BambuCommands.Simple("stop"), ct);

    public async Task UploadAsync(string localPath, string remoteName, IProgress<double>? progress, CancellationToken ct)
    {
        using var ftp = new AsyncFtpClient(Config.Host, "bblp", Config.AccessCode, 990);
        ftp.Config.EncryptionMode = FtpEncryptionMode.Implicit;
        ftp.Config.DataConnectionEncryption = true;
        ftp.Config.ValidateAnyCertificate = true;
        ftp.Config.DataConnectionType = FtpDataConnectionType.PASV;
        ftp.Config.ConnectTimeout = 15000;
        await ftp.Connect(ct);
        var p = progress == null ? null : new Progress<FtpProgress>(x => progress.Report(x.Progress / 100.0));
        var status = await ftp.UploadFile(localPath, "/" + remoteName, FtpRemoteExists.Overwrite, false, FtpVerify.None, p, ct);
        if (status == FtpStatus.Failed) throw new IOException($"Upload von {remoteName} fehlgeschlagen");
        await ftp.Disconnect(ct);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null) { try { await _loop; } catch { } }
        try { if (_mqtt.IsConnected) await _mqtt.DisconnectAsync(); } catch { }
        _mqtt.Dispose();
    }
}

/// <summary>Simulierter Drucker zum Testen der Farm ohne Hardware.</summary>
public sealed class SimulatedPrinterConnection : IPrinterConnection
{
    readonly CancellationTokenSource _cts = new();
    Task? _loop;
    string _state = "IDLE";
    double _bed = 22;
    double _bedTarget;
    DateTime _printStart;
    string _subtask = "";

    public PrinterConfig Config { get; }
    public PrinterStatus Status { get; private set; } = new();
    public event Action<IPrinterConnection>? StatusChanged;

    /// <summary>Druckdauer der Simulation.</summary>
    public TimeSpan PrintDuration { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Abkühlrate in K/s.</summary>
    public double CoolingRate { get; set; } = 1.0;
    public TimeSpan Tick { get; set; } = TimeSpan.FromMilliseconds(250);
    public bool FailNextPrint { get; set; }
    public List<string> SentGcode { get; } = new();
    public List<string> Uploaded { get; } = new();

    public SimulatedPrinterConnection(PrinterConfig config) => Config = config;

    public Task StartAsync(CancellationToken ct)
    {
        _loop ??= Task.Run(() => Loop(_cts.Token));
        return Task.CompletedTask;
    }

    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            lock (this)
            {
                double dt = Tick.TotalSeconds;
                if (_state is "PREPARE" or "RUNNING")
                {
                    _bedTarget = 55;
                    _bed = System.Math.Min(_bedTarget, _bed + 10 * dt);
                    var el = DateTime.UtcNow - _printStart;
                    _state = el.TotalSeconds < PrintDuration.TotalSeconds * 0.1 ? "PREPARE" : "RUNNING";
                    if (FailNextPrint && el > PrintDuration / 2) { _state = "FAILED"; _bedTarget = 0; FailNextPrint = false; }
                    else if (el >= PrintDuration) { _state = "FINISH"; _bedTarget = 0; }
                }
                else
                {
                    _bed = System.Math.Max(22, _bed - CoolingRate * dt);
                }
                int progress = _state == "RUNNING"
                    ? (int)System.Math.Clamp((DateTime.UtcNow - _printStart) / PrintDuration * 100, 0, 99)
                    : _state == "FINISH" ? 100 : 0;
                Status = new PrinterStatus
                {
                    Online = true, GcodeState = _state, BedTemp = System.Math.Round(_bed, 1), BedTarget = _bedTarget,
                    NozzleTemp = _state == "RUNNING" ? 220 : 25, Progress = progress, SubtaskName = _subtask,
                    LastUpdateUtc = DateTime.UtcNow,
                };
            }
            StatusChanged?.Invoke(this);
            try { await Task.Delay(Tick, ct); } catch { break; }
        }
    }

    public Task SendGcodeAsync(string gcode, CancellationToken ct)
    {
        lock (this) SentGcode.Add(gcode);
        return Task.CompletedTask;
    }

    public async Task UploadAsync(string localPath, string remoteName, IProgress<double>? progress, CancellationToken ct)
    {
        for (int i = 1; i <= 4; i++) { await Task.Delay(Tick / 2, ct); progress?.Report(i / 4.0); }
        lock (this) Uploaded.Add(remoteName);
    }

    public Task StartPrintAsync(string remoteName, int plate, bool useAms, bool bedLeveling, CancellationToken ct)
    {
        lock (this)
        {
            if (_state is "PREPARE" or "RUNNING") throw new InvalidOperationException("Drucker druckt bereits");
            _state = "PREPARE";
            _printStart = DateTime.UtcNow;
            _subtask = Path.GetFileNameWithoutExtension(remoteName);
        }
        return Task.CompletedTask;
    }

    public Task StopPrintAsync(CancellationToken ct)
    {
        lock (this) { _state = "FAILED"; _bedTarget = 0; }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null) { try { await _loop; } catch { } }
    }
}
