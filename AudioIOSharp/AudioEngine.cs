namespace AudioIOSharp;

/// <summary>Where the engine is: the stream that's open (if any), its format and latencies, and what went wrong.</summary>
/// <param name="Open">Whether a stream is open.</param>
/// <param name="Driver">The open stream's driver, or the configured one when none is open.</param>
/// <param name="Device">The open stream's device, or the configured one when none is open.</param>
/// <param name="Format">The open stream's format.</param>
/// <param name="BufferSize">The open stream's buffer size.</param>
/// <param name="InputLatency">The open stream's input latency, in samples.</param>
/// <param name="OutputLatency">The open stream's output latency, in samples.</param>
/// <param name="Connections">JACK: the stream's ports and what each is connected to now.</param>
/// <param name="Running">Whether a client is running (a resident stream stays open without one).</param>
/// <param name="Fallback">Running on the internal clock, although another driver is configured: its device couldn't be
/// opened.</param>
/// <param name="Error">Why the configured device isn't the one running, or why it stopped.</param>
public record AudioState(
    bool Open,
    string Driver,
    string? Device,
    AudioFormat? Format,
    int BufferSize,
    int InputLatency,
    int OutputLatency,
    IReadOnlyList<PortConnection>? Connections,
    bool Running,
    bool Fallback,
    string? Error);

public enum AudioLogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Runs one client at a time on the configured audio device, and keeps it running whatever the device does:
/// <list type="bullet">
/// <item>The device opens when a client attaches and closes when it detaches, so an idle application doesn't hold an
/// ASIO driver another program wants. A driver whose stream stays open (JACK's) opens at <see cref="Start"/> instead, so
/// its ports are in the graph from the start, and clients swap in and out of it.</item>
/// <item>If the configured device can't be opened, the client runs on the internal clock instead
/// (<see cref="AudioState.Fallback"/>), and the state says why.</item>
/// <item>If the device stops while a client runs (unplugged, say), the configuration goes back to no device at the same
/// rate (<see cref="ConfigChanged"/>, for the application to save), and the client carries on on the clock.</item>
/// <item>A new configuration is checked against its device before the running one closes; if it can't be used, the
/// previous one comes back.</item>
/// </list>
/// Saving configurations is the application's: it gives the engine the configuration last used with each driver
/// (<c>lastUsed</c>), and saves what <see cref="SetConfig"/> returns and what <see cref="ConfigChanged"/> reports.
/// </summary>
public class AudioEngine : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ClockDriver clock = new();
    private readonly List<AudioDriver> drivers;
    private readonly Func<string, AudioConfig?> lastUsed;

    // Guarded by gate.
    private AudioConfig config;
    private AudioStream? stream;
    private IAudioClient? client;
    private string? error;

    /// <param name="initial">The configuration to start with.</param>
    /// <param name="devices">The drivers besides the internal clock, which the engine always has (see
    /// <see cref="PlatformDrivers"/>).</param>
    /// <param name="lastUsed">The configuration last used with a driver, or null for its defaults.</param>
    public AudioEngine(AudioConfig initial, IEnumerable<AudioDriver>? devices = null, Func<string, AudioConfig?>? lastUsed = null)
    {
        config = initial;
        drivers = new List<AudioDriver> { clock };
        drivers.AddRange(devices ?? Array.Empty<AudioDriver>());
        this.lastUsed = lastUsed ?? (_ => null);
    }

    /// <summary>The drivers this platform has: ASIO on Windows, and JACK where it's installed (its client named
    /// <paramref name="jackClientName"/> in the graph).</summary>
    public static IReadOnlyList<AudioDriver> PlatformDrivers(string jackClientName)
    {
        var list = new List<AudioDriver>();
        if (OperatingSystem.IsWindows())
            list.Add(new AsioDriver());
        if (JackDriver.IsAvailable)
            list.Add(new JackDriver(jackClientName));
        return list;
    }

    /// <summary>Raised when the state changes: a stream opening, closing, failing or changing its format, latencies or
    /// connections; a client attaching or detaching. Not on the audio thread.</summary>
    public event Action? StateChanged;

    /// <summary>Raised when the engine changes the configuration itself: back to no device after the device failed.
    /// The application saves it.</summary>
    public event Action<AudioConfig>? ConfigChanged;

    /// <summary>What the engine has to say: devices that couldn't be opened or failed, and how it carried on.</summary>
    public event Action<AudioLogLevel, string>? Log;

    /// <summary>The drivers, the internal clock first.</summary>
    public IReadOnlyList<AudioDriver> Drivers => drivers;

    public AudioDriver Driver(string name) =>
        drivers.FirstOrDefault(d => string.Equals(d.DriverName, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new AudioException(AudioErrorKind.NotFound, $"There's no audio driver '{name}'. The drivers are: {string.Join(", ", drivers.Select(d => d.DriverName))}.");

    /// <summary>The client running on the device, if any.</summary>
    public IAudioClient? Client => client;

    public AudioConfig Config => config;

    /// <summary>The configuration last used with a driver (from the application), or the driver's defaults.</summary>
    public AudioConfig ConfigFor(string driver) =>
        lastUsed(driver) is AudioConfig last ? last with { Driver = driver } : new AudioConfig { Driver = driver };

    /// <summary>Opens a stream that stays open (JACK's) when it's the configured driver, so its ports are in the graph
    /// from the start. Other drivers open when a client attaches.</summary>
    public async Task Start()
    {
        await gate.WaitAsync();
        try
        {
            if (stream == null && client == null)
                await OpenLocked();
        }
        finally
        {
            gate.Release();
        }
        RaiseStateChanged();
    }

    // ---- Clients ----

    /// <summary>Runs <paramref name="newClient"/> on the device, preparing it and starting it, falling back to the
    /// internal clock if the device can't be opened. Any other client is detached first. A stream that stays open
    /// (JACK's) takes the client in; any other opens for it.</summary>
    public async Task Attach(IAudioClient newClient)
    {
        await gate.WaitAsync();
        try
        {
            if (client != null)
                await DetachLocked();
            client = newClient;
            if (stream != null && stream.Resident && stream.Driver == config.Driver)
                stream.SetClient(newClient);
            else
            {
                await CloseStreamLocked();
                await OpenLocked();
            }
        }
        finally
        {
            gate.Release();
        }
        RaiseStateChanged();
    }

    public async Task Detach(IAudioClient oldClient)
    {
        await gate.WaitAsync();
        try
        {
            if (client != oldClient)
                return;
            await DetachLocked();
        }
        finally
        {
            gate.Release();
        }
        RaiseStateChanged();
    }

    // The client stops running: a resident stream carries on in silence; any other closes.
    private async Task DetachLocked()
    {
        client = null;
        if (stream != null && stream.Resident)
            stream.SetClient(null);
        else
            await CloseStreamLocked();
    }

    /// <summary>The running client's latency changed (a rebuild): drivers that report it pass it on.</summary>
    public void LatencyChanged(IAudioClient changed)
    {
        if (client == changed)
            stream?.LatencyChanged(changed.Latency);
    }

    // Opens the configured device for the client; with no client, only a driver that keeps its stream open does.
    private async Task OpenLocked()
    {
        error = null;
        AudioDriver? driver = drivers.FirstOrDefault(d => string.Equals(d.DriverName, config.Driver, StringComparison.OrdinalIgnoreCase));
        if (client == null)
        {
            if (driver?.KeepsOpen != true)
                return;
            try
            {
                stream = await driver.Open(config, null);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Write(AudioLogLevel.Info, $"Couldn't open {Describe(config)}: {ex.Message}");
                return;
            }
        }
        else
        {
            try
            {
                stream = await Driver(config.Driver).Open(config, client);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Write(AudioLogLevel.Warning, $"Couldn't open {Describe(config)}, so running on the internal clock: {ex.Message}");
                stream = await clock.Open(config, client);
            }
        }
        stream.Failed += OnFailed;
        stream.Changed += OnChanged;
    }

    private async Task CloseStreamLocked()
    {
        AudioStream? s = stream;
        stream = null;
        if (s != null)
        {
            s.Failed -= OnFailed;
            s.Changed -= OnChanged;
            await s.Close();
        }
    }

    // The device stopped on its own (unplugged, say). With a client running, the configuration goes back to no device
    // (the internal clock), at the same rate, so the client carries on and the application shows no device rather than
    // one that's gone; choosing the device again brings it back. With no client, the stream just closes.
    private void OnFailed(AudioStream failed, string message) => _ = Task.Run(async () =>
    {
        AudioConfig? reset = null;
        await gate.WaitAsync();
        try
        {
            if (stream != failed)
                return;
            if (client == null)
            {
                Write(AudioLogLevel.Warning, message);
                error = message;
                await CloseStreamLocked();
                return;
            }
            Write(AudioLogLevel.Error, $"{message} Switched to no audio device (the internal clock); the audio carries on.");
            failed.Failed -= OnFailed;
            failed.Changed -= OnChanged;
            stream = null;
            try { await failed.Close(); }
            catch (Exception ex) { Write(AudioLogLevel.Warning, $"Closing the failed device: {ex.Message}"); }
            config = ConfigFor(ClockDriver.Name) with { SampleRate = (int)failed.Format.SampleRate };
            reset = config;
            stream = await clock.Open(config, client);
            stream.Failed += OnFailed;
            stream.Changed += OnChanged;
            error = message;
        }
        catch (Exception ex)
        {
            Write(AudioLogLevel.Error, $"Couldn't recover from the device failing: {ex.Message}");
        }
        finally
        {
            gate.Release();
        }
        RaiseStateChanged();
        if (reset != null)
            ConfigChanged?.Invoke(reset);
    });

    private void OnChanged(AudioStream changed) => RaiseStateChanged();

    // ---- State ----

    public AudioState State
    {
        get
        {
            AudioStream? s = stream;
            AudioConfig c = config;
            return s != null
                ? new AudioState(true, s.Driver, s.Device, s.Format, s.BufferSize, s.InputLatency, s.OutputLatency,
                    s.Connections(), client != null, s.Driver != c.Driver, error)
                : new AudioState(false, c.Driver, c.Device, null, 0, 0, 0, null, client != null, false, error);
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke();

    private void Write(AudioLogLevel level, string message) => Log?.Invoke(level, message);

    private static string Describe(AudioConfig c) => c.Device != null ? $"the {c.Driver} device '{c.Device}'" : $"the first {c.Driver} device";

    // ---- Configuration ----

    /// <summary>
    /// Checks a configuration against its device and moves to it: a running client carries on on the new device, and a
    /// stream that stays open (JACK's) reopens with it, or closes for another driver. A null device means the driver's
    /// first. If the new configuration can't be used, the previous one is kept (and reopened) and the exception says
    /// why. Returns the configuration now in use, with its device filled in, for the application to save.
    /// </summary>
    public async Task<AudioConfig> SetConfig(AudioConfig next)
    {
        await gate.WaitAsync();
        try
        {
            AudioDriver driver = Driver(next.Driver);
            string device = next.Device ?? (await driver.Devices()).FirstOrDefault()
                ?? throw new AudioException(AudioErrorKind.NotFound, $"There are no {driver.DriverName} devices.");
            next = next with { Driver = driver.DriverName, Device = device };
            if (client == null)
            {
                config = next.Checked(await driver.Info(device));
                await CloseStreamLocked();
                await OpenLocked();
            }
            else
            {
                AudioDeviceInfo? info = null;
                try
                {
                    // The running device, or one the running one doesn't stop us looking at: check before closing it.
                    info = await driver.Info(device);
                    next = next.Checked(info);
                }
                catch (AudioException ex) when (ex.Kind == AudioErrorKind.Busy)
                {
                    // Another ASIO driver is running: it has to close before this one can be looked at.
                }
                AudioConfig previous = config;
                await CloseStreamLocked();
                try
                {
                    if (info == null)
                        next = next.Checked(await driver.Info(device));
                    config = next;
                }
                catch
                {
                    config = previous;
                    await OpenLocked();
                    throw;
                }
                await OpenLocked();
            }
            return config;
        }
        finally
        {
            gate.Release();
            RaiseStateChanged();
        }
    }

    /// <summary>Shows a device's own settings (ASIO's control panel): the running device's, unless another is named.</summary>
    public async Task ShowControlPanel(string? driverName = null, string? device = null)
    {
        AudioDriver driver = Driver(driverName ?? config.Driver);
        string name = device ?? stream?.Device ?? config.Device ?? (await driver.Devices()).FirstOrDefault()
            ?? throw new AudioException(AudioErrorKind.NotFound, $"There are no {driver.DriverName} devices.");
        await driver.ShowControlPanel(name);
    }

    public void Dispose()
    {
        gate.Wait();
        try
        {
            client = null;
            CloseStreamLocked().Wait(TimeSpan.FromSeconds(5));
        }
        finally
        {
            gate.Release();
        }
        foreach (AudioDriver driver in drivers)
            (driver as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }
}
