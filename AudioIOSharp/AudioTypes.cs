namespace AudioIOSharp;

/// <summary>What an open stream runs at: its rate, the most frames a callback can carry, and the channels it opened,
/// by name, in the order of <see cref="AudioBuffers.Input"/> and <see cref="AudioBuffers.Output"/>.</summary>
public record AudioFormat(double SampleRate, int MaxFrames, IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs)
{
    /// <summary>The MIDI inputs the stream opened, by name, in the order of <see cref="MidiEvent.Port"/>.</summary>
    public IReadOnlyList<string> MidiInputs { get; init; } = Array.Empty<string>();
}

/// <summary>
/// One callback's audio: a span per channel, exactly <see cref="Frames"/> long. Valid only during the callback: the
/// spans can be the device's own buffers (JACK's), which the next callback reuses, and a <c>ref struct</c> can't be
/// kept. Nothing is allocated: the stream reuses its buffers and channel tables every callback.
/// </summary>
public readonly unsafe ref struct AudioBuffers
{
    private readonly ReadOnlySpan<nint> inputs, outputs;

    /// <summary>
    /// Audio for a client, from a table of channel pointers per direction. Each pointer must point at
    /// <paramref name="frames"/> floats that can't move for as long as the buffers are used: native memory, or a
    /// pinned array.
    /// </summary>
    public AudioBuffers(ReadOnlySpan<nint> inputs, ReadOnlySpan<nint> outputs, int frames, long skipped = 0, int xruns = 0,
        ReadOnlySpan<MidiEvent> midi = default)
    {
        this.inputs = inputs;
        this.outputs = outputs;
        Frames = frames;
        Skipped = skipped;
        Xruns = xruns;
        Midi = midi;
    }

    public int Frames { get; }

    /// <summary>The block's MIDI input, from every MIDI input the stream opened, in order of
    /// <see cref="MidiEvent.Offset"/>. From JACK, at the frames JACK gives; from other drivers' (system) MIDI inputs,
    /// one block late, at the positions the messages arrived in the previous block.</summary>
    public ReadOnlySpan<MidiEvent> Midi { get; }

    /// <summary>Frames the stream's clock skipped before this block, after falling behind (the internal clock only).</summary>
    public long Skipped { get; }

    /// <summary>Dropouts the stream noticed since the previous block: driver resyncs, and callbacks that overran.</summary>
    public int Xruns { get; }

    public int InputCount => inputs.Length;

    public int OutputCount => outputs.Length;

    public ReadOnlySpan<float> Input(int channel) => new((void*)inputs[channel], Frames);

    public Span<float> Output(int channel) => new((void*)outputs[channel], Frames);

    /// <summary>Silences every output.</summary>
    public void ClearOutputs()
    {
        for (int o = 0; o < outputs.Length; o++)
            Output(o).Clear();
    }
}

/// <summary>What runs on an audio stream: it reads the inputs and writes the outputs of every block.</summary>
public interface IAudioClient
{
    /// <summary>Called while the stream is stopped, before it starts and again after its format changes, so the client
    /// can size its buffers (and rebuild, if the rate changed).</summary>
    void Prepare(AudioFormat format);

    /// <summary>Called on the audio thread for every block. It must fill every output, and must not allocate, lock or
    /// block. The buffers are valid only until it returns.</summary>
    void Process(in AudioBuffers buffers);

    /// <summary>The latency the client adds, in samples, for drivers that report it on (JACK).</summary>
    int Latency => 0;
}

/// <summary>What a device offers, for choosing a configuration and for checking one.</summary>
public record AudioDeviceInfo(
    string Name,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<int> SampleRates,
    IReadOnlyList<int> BufferSizes,
    int DefaultSampleRate,
    int DefaultBufferSize,
    bool ControlPanel)
{
    /// <summary>The MIDI inputs a configuration can open with the device: JACK's MIDI ports, for JACK; the system's MIDI
    /// inputs, for the other drivers.</summary>
    public IReadOnlyList<string> MidiInputs { get; init; } = Array.Empty<string>();
}

/// <summary>
/// An audio configuration: a driver, one of its devices, and the channels, rate and buffer size to open it with.
/// Channels are named, so a configuration still means the same channels when a device's list changes. A null field
/// means the device's default: its first device; its first input and first two outputs; its current rate; its
/// preferred buffer size.
/// <para>MIDI inputs are named too, and none is the default. Unlike channels, a MIDI input that isn't there (a keyboard
/// unplugged) doesn't stop the configuration from being used: the stream opens without it, and says so
/// (<see cref="AudioStream.MidiErrors"/>).</para>
/// </summary>
public record AudioConfig
{
    public string Driver { get; init; } = ClockDriver.Name;
    public string? Device { get; init; }
    public IReadOnlyList<string>? Inputs { get; init; }
    public IReadOnlyList<string>? Outputs { get; init; }
    public int? SampleRate { get; init; }
    public int? BufferSize { get; init; }
    public IReadOnlyList<string>? MidiInputs { get; init; }

    /// <summary>The configuration's MIDI inputs that are among <paramref name="available"/>, as those spell them, and
    /// the ones that aren't.</summary>
    public (string[] Present, string[] Missing) MidiInputsIn(IReadOnlyList<string> available)
    {
        var present = new List<string>();
        var missing = new List<string>();

        foreach (string name in MidiInputs ?? Array.Empty<string>())
        {
            string? found = available.FirstOrDefault(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

            if (found == null)
                missing.Add(name);
            else if (!present.Contains(found))
                present.Add(found);
        }

        return (present.ToArray(), missing.ToArray());
    }

    /// <summary>The device's channels this configuration opens, by index into the device's own lists.</summary>
    public (int[] Inputs, int[] Outputs) Channels(AudioDeviceInfo info)
    {
        static int[] Pick(IReadOnlyList<string>? names, IReadOnlyList<string> available, int fallback, string kind)
        {
            if (names == null)
                return Enumerable.Range(0, Math.Min(fallback, available.Count)).ToArray();
            return names.Select(name =>
            {
                int i = available.ToList().FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
                return i >= 0 ? i : throw new AudioException(AudioErrorKind.InvalidValue,
                    $"The device has no {kind} channel '{name}'. Its {kind}s are: {List(available)}.");
            }).Distinct().ToArray();
        }
        return (Pick(Inputs, info.Inputs, 1, "input"), Pick(Outputs, info.Outputs, 2, "output"));
    }

    /// <summary>Checks the configuration against what the device offers, and fills in the device's name.</summary>
    public AudioConfig Checked(AudioDeviceInfo info)
    {
        Channels(info);
        if (SampleRate is int rate && !info.SampleRates.Contains(rate))
            throw new AudioException(AudioErrorKind.InvalidValue,
                $"The device doesn't run at {rate} Hz. It supports {List(info.SampleRates.Select(r => r.ToString()))} Hz.");
        if (BufferSize is int size && !info.BufferSizes.Contains(size))
            throw new AudioException(AudioErrorKind.InvalidValue,
                $"The device doesn't take buffers of {size} samples. It supports {List(info.BufferSizes.Select(b => b.ToString()))}.");
        return this with { Device = info.Name };
    }

    private static string List(IEnumerable<string> items) => items.Any() ? string.Join(", ", items) : "(none)";
}

/// <summary>One of a stream's ports and what it is connected to now (JACK).</summary>
/// <param name="Port">The port's full name.</param>
/// <param name="Channel">The channel the configuration opened it for: what it was connected to when it opened.</param>
/// <param name="Connected">The ports it is connected to now, whoever connected them.</param>
public record PortConnection(string Port, string Channel, IReadOnlyList<string> Connected);

/// <summary>A running stream on a device. Made by <see cref="AudioDriver.Open"/>, already started.</summary>
public abstract class AudioStream
{
    public abstract string Driver { get; }
    public abstract string Device { get; }
    public abstract AudioFormat Format { get; }
    public abstract int BufferSize { get; }
    public virtual int InputLatency => 0;
    public virtual int OutputLatency => 0;

    /// <summary>Why MIDI inputs the configuration names weren't opened (not there, or in use by another program).</summary>
    public virtual IReadOnlyList<string> MidiErrors => Array.Empty<string>();

    /// <summary>Raised, off the audio thread, when the device stops on its own: unplugged, or a driver fault.</summary>
    public event Action<AudioStream, string>? Failed;

    /// <summary>Raised, off the audio thread, after the driver changed the format (the client has been prepared for
    /// the new one), its latencies, or its connections.</summary>
    public event Action<AudioStream>? Changed;

    protected void OnFailed(string message) => Failed?.Invoke(this, message);

    /// <summary>Whether the stream stays open with no client, playing silence, so a client can be swapped in and out
    /// (<see cref="SetClient"/>) without closing it: JACK's, whose ports and their connections stay in the graph.</summary>
    public virtual bool Resident => false;

    /// <summary>Swaps the client a resident stream runs (null for none), preparing the new one first.</summary>
    public virtual void SetClient(IAudioClient? client) => throw new NotSupportedException($"{Driver} streams don't swap clients.");

    /// <summary>The client's latency changed (a rebuild): drivers that report latency pass it on.</summary>
    public virtual void LatencyChanged(int samples) { }

    /// <summary>The stream's ports and what each is connected to now, for drivers whose connections can change outside
    /// the application (JACK, from a patchbay); null for the others.</summary>
    public virtual IReadOnlyList<PortConnection>? Connections() => null;

    protected void OnChanged() => Changed?.Invoke(this);

    /// <summary>Stops the stream and closes the device. No callback runs after it returns.</summary>
    public abstract Task Close();
}

/// <summary>A kind of audio device: the internal clock, ASIO or JACK.</summary>
public abstract class AudioDriver
{
    public abstract string DriverName { get; }

    public abstract Task<IReadOnlyList<string>> Devices();

    /// <summary>What a device offers. For ASIO this loads the driver, unless it's the one already open.</summary>
    public abstract Task<AudioDeviceInfo> Info(string device);

    /// <summary>Opens the configuration's device (null: the first), prepares the client for its format, and starts
    /// it. Only a driver that <see cref="KeepsOpen"/> takes no client.</summary>
    public abstract Task<AudioStream> Open(AudioConfig config, IAudioClient? client);

    /// <summary>Whether the driver's stream stays open while it's the configured driver, with or without a client
    /// (see <see cref="AudioStream.Resident"/>).</summary>
    public virtual bool KeepsOpen => false;

    public virtual Task ShowControlPanel(string device) =>
        throw new AudioException(AudioErrorKind.Unsupported, $"{DriverName} devices have no control panel.");

    /// <summary>The configuration's device, or the driver's first.</summary>
    protected async Task<string> DeviceOf(AudioConfig config)
    {
        if (config.Device != null)
            return config.Device;
        IReadOnlyList<string> devices = await Devices();
        return devices.Count > 0 ? devices[0] : throw new AudioException(AudioErrorKind.Unavailable, $"There are no {DriverName} devices.");
    }
}
