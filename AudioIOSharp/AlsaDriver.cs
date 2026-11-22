using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AudioIOSharp;

/// <summary>
/// ALSA hardware devices (Linux), through libasound, for the lowest latency: a card's PCM device opened directly
/// (<c>hw:CARD=...,DEV=...</c>), in its native sample format and with all its channels, with no ALSA plugins between.
/// Capture and playback run duplex, linked so they start together on the card's clock; a device with only one
/// direction runs that one. Each period, a thread of the stream's own (real-time priority where allowed) waits for a
/// period of input, converts it from the device's mmap buffer into the client's buffers, runs the client, and converts
/// its outputs into the playback buffer: one copy each way, and nothing allocated. Playback is buffered by
/// <see cref="Periods"/> periods of the buffer size.
/// <para>A hardware device has one user at a time: a sound server (PipeWire, PulseAudio) holding it makes it busy.
/// Real-time priority needs the right to it (the <c>audio</c> group, or an <c>rtprio</c> limit); without it the
/// stream still runs, at normal priority.</para>
/// </summary>
[SupportedOSPlatform("linux")]
public class AlsaDriver : AudioDriver
{
    public const string Name = "ALSA";

    private static readonly int[] StandardRates = { 44100, 48000, 88200, 96000, 176400, 192000 };
    private static readonly int[] StandardSizes = { 16, 32, 64, 128, 256, 512, 1024, 2048, 4096 };
    private const int PreferredSize = 256;

    private readonly object sync = new();
    private readonly Dictionary<string, AlsaStream> open = new(StringComparer.Ordinal);   // By ALSA name.

    /// <param name="periods">Periods of the buffer size in the playback buffer: 3 by default, which USB devices
    /// need (their packets don't line up with ALSA's periods, so 2 drops out even on an idle machine); 2 for the lowest
    /// latency on PCI, PCIe and onboard devices, one period less.</param>
    /// <param name="realtimePriority">The audio thread's SCHED_FIFO priority (1-99), or 0 to leave it at normal
    /// priority.</param>
    public AlsaDriver(int periods = 3, int realtimePriority = 80)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(periods, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(realtimePriority);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(realtimePriority, 99);

        Periods = periods;
        RealtimePriority = realtimePriority;
    }

    public int Periods { get; }

    public int RealtimePriority { get; }

    /// <summary>Whether ALSA's library (libasound) is installed.</summary>
    public static bool IsAvailable
    {
        get
        {
            try { return AlsaNative.IsAvailable; }
            catch (Exception) { return false; }
        }
    }

    public override string DriverName => Name;

    public override Task<IReadOnlyList<string>> Devices() => Task.Run<IReadOnlyList<string>>(() =>
    {
        if (!IsAvailable)
            return Array.Empty<string>();

        return HardwareDevices();
    });

    public override Task<AudioDeviceInfo> Info(string device) => Task.Run(() =>
    {
        CheckAvailable();

        string name = AlsaName(device);

        lock (sync)
            if (open.TryGetValue(name, out AlsaStream? stream))
                return stream.Info;

        return Probe(device, name);
    });

    public override Task<AudioStream> Open(AudioConfig config, IAudioClient? client) => Task.Run<AudioStream>(async () =>
    {
        ArgumentNullException.ThrowIfNull(client);

        CheckAvailable();

        string device = await DeviceOf(config);
        string name = AlsaName(device);

        lock (sync)
            if (open.ContainsKey(name))
                throw new AudioException(AudioErrorKind.Busy, $"The ALSA device '{device}' is already open.");

        AudioDeviceInfo info = Probe(device, name);

        config = config.Checked(info);

        var stream = new AlsaStream(this, name, info, config, client);

        lock (sync)
            open[name] = stream;

        return stream;
    });

    private void Closed(AlsaStream stream)
    {
        lock (sync)
            if (open.TryGetValue(stream.AlsaDevice, out AlsaStream? s) && s == stream)
                open.Remove(stream.AlsaDevice);
    }

    private static void CheckAvailable()
    {
        if (!IsAvailable)
            throw new AudioException(AudioErrorKind.Unavailable, "ALSA isn't installed: its library (libasound.so.2) wasn't found.");
    }

    // ---- Devices ----------------------------------------------------------------------------------------------------

    /// <summary>A device's name as the driver lists it: the card and device description, then the ALSA name it opens
    /// (by card id, which stays the same across reboots, unlike the card's number).</summary>
    internal static string DisplayName(string? description, string alsaName)
    {
        string first = description?.Split('\n')[0].Trim() ?? "";

        return first.Length > 0 ? $"{first} ({alsaName})" : alsaName;
    }

    /// <summary>The ALSA name a device opens: the hardware name at the end of a listed name, or the name itself (any
    /// ALSA PCM name, for devices that aren't listed).</summary>
    internal static string AlsaName(string device)
    {
        int open = device.LastIndexOf(" (hw:", StringComparison.Ordinal);

        if ((open >= 0) && device.EndsWith(')'))
            return device.Substring(open + 2, device.Length - open - 3);

        return device;
    }

    // The hardware PCM devices (hw:CARD=...,DEV=...), from ALSA's device name hints.
    private static List<string> HardwareDevices()
    {
        var devices = new List<string>();

        if (AlsaNative.DeviceNameHint(-1, "pcm", out nint hints) < 0)
            return devices;

        try
        {
            for (nint entry = hints; Marshal.ReadIntPtr(entry) != 0; entry += nint.Size)
            {
                nint hint = Marshal.ReadIntPtr(entry);
                string? name = Hint(hint, "NAME");

                if ((name == null) || !name.StartsWith("hw:", StringComparison.Ordinal))
                    continue;

                string display = DisplayName(Hint(hint, "DESC"), name);

                if (!devices.Contains(display))
                    devices.Add(display);
            }
        }
        finally
        {
            AlsaNative.DeviceNameFreeHint(hints);
        }

        return devices;
    }

    private static string? Hint(nint hint, string id)
    {
        nint value = AlsaNative.DeviceNameGetHint(hint, id);

        if (value == 0)
            return null;

        try
        {
            return Marshal.PtrToStringUTF8(value);
        }
        finally
        {
            AlsaNative.Free(value);
        }
    }

    /// <summary>What one direction of a device offers.</summary>
    private record Capabilities(int Channels, List<int> Rates, List<int> Sizes);

    private static AudioDeviceInfo Probe(string device, string name)
    {
        Capabilities? capture = Query(name, AlsaNative.StreamCapture, out int captureError);
        Capabilities? playback = Query(name, AlsaNative.StreamPlayback, out int playbackError);

        if ((capture == null) && (playback == null))
            throw OpenError(device, (playbackError != 0) ? playbackError : captureError);

        IEnumerable<int> rates = (capture?.Rates ?? playback!.Rates).Intersect(playback?.Rates ?? capture!.Rates);
        IEnumerable<int> sizes = (capture?.Sizes ?? playback!.Sizes).Intersect(playback?.Sizes ?? capture!.Sizes);

        List<int> rateList = rates.OrderBy(r => r).ToList();
        List<int> sizeList = sizes.OrderBy(s => s).ToList();

        if ((rateList.Count == 0) || (sizeList.Count == 0))
            throw new AudioException(AudioErrorKind.Unavailable,
                $"The ALSA device '{device}' has no sample rate or buffer size that both its capture and playback take.");

        int defaultRate = rateList.Contains(ClockDriver.DefaultSampleRate) ? ClockDriver.DefaultSampleRate : rateList[0];
        int defaultSize = sizeList.OrderBy(s => Math.Abs(s - PreferredSize)).First();

        return new AudioDeviceInfo(
            name.StartsWith("hw:", StringComparison.Ordinal) ? DisplayFor(device, name) : device,
            Enumerable.Range(1, capture?.Channels ?? 0).Select(i => $"In {i}").ToList(),
            Enumerable.Range(1, playback?.Channels ?? 0).Select(i => $"Out {i}").ToList(),
            rateList,
            sizeList,
            defaultRate,
            defaultSize,
            ControlPanel: false);
    }

    // The listed name of a hardware device, if it was asked for by its ALSA name alone.
    private static string DisplayFor(string device, string name) =>
        device != name ? device : HardwareDevices().FirstOrDefault(d => AlsaName(d) == name) ?? device;

    // What one direction of the device offers, or null (with the error) if it can't be opened.
    private static Capabilities? Query(string name, int stream, out int error)
    {
        error = AlsaNative.PcmOpen(out nint pcm, name, stream, AlsaNative.OpenNonBlock);

        if (error < 0)
            return null;

        nint parameters = 0;

        try
        {
            AlsaNative.HwParamsMalloc(out parameters);

            if ((error = AlsaNative.HwParamsAny(pcm, parameters)) < 0)
                return null;

            AlsaNative.HwParamsSetRateResample(pcm, parameters, 0);

            if ((error = SetAccess(pcm, parameters)) < 0)
                return null;

            if (AlsaSamples.Preference.All(f => AlsaNative.HwParamsTestFormat(pcm, parameters, f) < 0))
            {
                error = 0;

                return null;
            }

            AlsaNative.HwParamsGetChannelsMax(parameters, out uint channels);

            var rates = StandardRates.Where(r => AlsaNative.HwParamsTestRate(pcm, parameters, (uint)r, 0) == 0).ToList();
            var sizes = StandardSizes.Where(s => AlsaNative.HwParamsTestPeriodSize(pcm, parameters, (nuint)s, 0) == 0).ToList();

            return new Capabilities((int)Math.Min(channels, 256), rates, sizes);
        }
        finally
        {
            if (parameters != 0)
                AlsaNative.HwParamsFree(parameters);

            AlsaNative.PcmClose(pcm);
        }
    }

    private static int SetAccess(nint pcm, nint parameters)
    {
        int error = AlsaNative.HwParamsSetAccess(pcm, parameters, AlsaNative.AccessMmapInterleaved);

        if (error < 0)
            error = AlsaNative.HwParamsSetAccess(pcm, parameters, AlsaNative.AccessMmapNonInterleaved);

        return error;
    }

    private static AudioException OpenError(string device, int error) => -error switch
    {
        AlsaNative.EBUSY => new AudioException(AudioErrorKind.Busy,
            $"The ALSA device '{device}' is in use by another program (a sound server such as PipeWire or PulseAudio, say)."),
        AlsaNative.ENOENT or AlsaNative.ENODEV => new AudioException(AudioErrorKind.NotFound, $"There's no ALSA device '{device}'."),
        _ => new AudioException(AudioErrorKind.Unavailable,
            $"Couldn't open the ALSA device '{device}': {(error != 0 ? AlsaNative.Error(error) : "it takes none of the sample formats the driver converts")}."),
    };

    // ---- The stream -------------------------------------------------------------------------------------------------

    /// <summary>One direction of an open device: its PCM, the format and channel count it was opened with.</summary>
    private struct Direction
    {
        public nint Pcm;
        public int Format;
        public int Channels;
        public int BufferFrames;
        public int[] Selected;   // The device channels the client sees, in order.
    }

    private unsafe class AlsaStream : AudioStream
    {
        private readonly AlsaDriver owner;
        private readonly IAudioClient client;
        private readonly Thread thread;
        private Direction capture, playback;
        private readonly bool linked;
        private readonly int period;
        private readonly StreamBuffers buffers;
        private volatile bool stopping;
        private int xruns;
        private bool closed;

        public AlsaStream(AlsaDriver owner, string alsaDevice, AudioDeviceInfo info, AudioConfig config, IAudioClient client)
        {
            this.owner = owner;
            this.client = client;
            AlsaDevice = alsaDevice;
            Info = info;

            var (inputs, outputs) = config.Channels(info);
            int rate = config.SampleRate ?? info.DefaultSampleRate;

            period = config.BufferSize ?? info.DefaultBufferSize;

            try
            {
                // Capture when inputs are used; playback when outputs are, or as the clock when nothing is
                if ((inputs.Length > 0) && (info.Inputs.Count > 0))
                    capture = OpenDirection(AlsaNative.StreamCapture, rate, inputs, info);

                if ((outputs.Length > 0) || (capture.Pcm == 0))
                {
                    if (info.Outputs.Count > 0)
                        playback = OpenDirection(AlsaNative.StreamPlayback, rate, outputs, info);
                    else if (capture.Pcm == 0)
                        capture = OpenDirection(AlsaNative.StreamCapture, rate, inputs, info);
                }

                // Linked, they start and stop together; a device that can't be linked starts one after the other
                if ((capture.Pcm != 0) && (playback.Pcm != 0))
                    linked = AlsaNative.PcmLink(capture.Pcm, playback.Pcm) == 0;
            }
            catch
            {
                CloseDevices();

                throw;
            }

            Format = new AudioFormat(rate, period,
                inputs.Select(i => info.Inputs[i]).ToArray(),
                outputs.Select(o => info.Outputs[o]).ToArray());

            buffers = new StreamBuffers(Format);

            client.Prepare(Format);

            thread = new Thread(Run) { IsBackground = true, Name = "ALSA audio", Priority = ThreadPriority.Highest };
            thread.Start();
        }

        public string AlsaDevice { get; }

        public AudioDeviceInfo Info { get; }

        public override string Driver => Name;

        public override string Device => Info.Name;

        public override AudioFormat Format { get; }

        public override int BufferSize => period;

        public override int InputLatency => (capture.Pcm != 0) ? period : 0;

        public override int OutputLatency => (playback.Pcm != 0) ? playback.BufferFrames : 0;

        // Opens one direction with all its channels, at the rate and period size, in the best format it takes
        private Direction OpenDirection(int stream, int rate, int[] selected, AudioDeviceInfo info)
        {
            string what = (stream == AlsaNative.StreamCapture) ? "capture" : "playback";
            int error = AlsaNative.PcmOpen(out nint pcm, AlsaDevice, stream, AlsaNative.OpenNonBlock);

            if (error < 0)
                throw OpenError(Device, error);

            var direction = new Direction { Pcm = pcm, Selected = selected };
            nint hw = 0, sw = 0;

            try
            {
                AlsaNative.HwParamsMalloc(out hw);

                Check(AlsaNative.HwParamsAny(pcm, hw), what, "read its settings");

                AlsaNative.HwParamsSetRateResample(pcm, hw, 0);

                Check(SetAccess(pcm, hw), what, "use direct (mmap) access");

                direction.Format = AlsaSamples.Preference.First(f => AlsaNative.HwParamsTestFormat(pcm, hw, f) == 0);

                Check(AlsaNative.HwParamsSetFormat(pcm, hw, direction.Format), what, "set the sample format");

                // The fewest channels the device opens that include the highest one chosen (all of them, for a device
                // that only runs with all)
                uint needed = (uint)Math.Max(1, (selected.Length > 0) ? selected.Max() + 1 : 1);

                Check(AlsaNative.HwParamsSetChannelsMin(pcm, hw, ref needed), what, $"open {needed} channels");
                Check(AlsaNative.HwParamsSetChannelsFirst(pcm, hw, out uint channels), what, $"open {needed} channels");

                direction.Channels = (int)channels;

                if (AlsaNative.HwParamsSetRate(pcm, hw, (uint)rate, 0) < 0)
                    throw new AudioException(AudioErrorKind.InvalidValue, $"The ALSA device '{Device}' can't {what} at {rate} Hz.", info.SampleRates);

                if (AlsaNative.HwParamsSetPeriodSize(pcm, hw, (nuint)period, 0) < 0)
                    throw new AudioException(AudioErrorKind.InvalidValue, $"The ALSA device '{Device}' can't {what} in periods of {period} samples.", info.BufferSizes);

                uint periods = (uint)owner.Periods;
                int dir = 0;

                Check(AlsaNative.HwParamsSetPeriodsNear(pcm, hw, ref periods, ref dir), what, $"use {owner.Periods} periods");

                Check(AlsaNative.HwParams(pcm, hw), what, "apply its settings");

                AlsaNative.HwParamsGetBufferSize(hw, out nuint bufferFrames);

                direction.BufferFrames = (int)bufferFrames;

                // Started by the stream only (never by the first write), woken a period at a time
                AlsaNative.SwParamsMalloc(out sw);

                Check(AlsaNative.SwParamsCurrent(pcm, sw), what, "read its software settings");

                AlsaNative.SwParamsGetBoundary(sw, out nuint boundary);

                Check(AlsaNative.SwParamsSetStartThreshold(pcm, sw, boundary), what, "set its start threshold");
                Check(AlsaNative.SwParamsSetAvailMin(pcm, sw, (nuint)period), what, "set its wake-up size");
                Check(AlsaNative.SwParams(pcm, sw), what, "apply its software settings");

                return direction;
            }
            catch
            {
                AlsaNative.PcmClose(pcm);

                throw;
            }
            finally
            {
                if (hw != 0)
                    AlsaNative.HwParamsFree(hw);

                if (sw != 0)
                    AlsaNative.SwParamsFree(sw);
            }
        }

        private void Check(int error, string what, string action)
        {
            if (error < 0)
                throw new AudioException(AudioErrorKind.Unavailable,
                    $"The ALSA device '{Device}' couldn't {action} for {what}: {AlsaNative.Error(error)}.");
        }

        public override Task Close() => Task.Run(() =>
        {
            lock (this)
            {
                if (closed)
                    return;

                closed = true;
            }

            stopping = true;

            thread.Join();

            CloseDevices();

            owner.Closed(this);
        });

        private void CloseDevices()
        {
            if (linked)
                AlsaNative.PcmUnlink(capture.Pcm);

            foreach (nint pcm in new[] { capture.Pcm, playback.Pcm })
            {
                if (pcm == 0)
                    continue;

                AlsaNative.PcmDrop(pcm);
                AlsaNative.PcmClose(pcm);
            }

            capture.Pcm = 0;
            playback.Pcm = 0;
        }

        // ---- The audio thread ---------------------------------------------------------------------------------------

        private void Run()
        {
            if (owner.RealtimePriority > 0)
                AlsaNative.PthreadSetSchedParam(AlsaNative.PthreadSelf(), AlsaNative.SchedFifo, owner.RealtimePriority);

            nint clock = (capture.Pcm != 0) ? capture.Pcm : playback.Pcm;

            // Long enough for a few periods: a wait that runs out means the device has stalled
            int timeout = Math.Max(20, (int)(period * 4 * 1000L / (long)Format.SampleRate));

            int error = StartDevices();

            while (!stopping)
            {
                // Waits for the clock's next period (0: timed out, the device stalled; waits again)
                if (error >= 0)
                {
                    error = AlsaNative.PcmWait(clock, timeout);

                    if (error > 0)
                        error = ProcessReady(timeout);
                }

                if (error < 0)
                {
                    if (!Recover(error))
                        return;

                    error = 0;
                }
            }
        }

        // Runs a block for every period both directions have ready
        private int ProcessReady(int timeout)
        {
            while (!stopping)
            {
                nint ready = (capture.Pcm != 0) ? AlsaNative.PcmAvailUpdate(capture.Pcm) : period;

                if (ready < 0)
                    return (int)ready;

                if (ready < period)
                    return 0;

                if (playback.Pcm != 0)
                {
                    nint space = AlsaNative.PcmAvailUpdate(playback.Pcm);

                    if (space < 0)
                        return (int)space;

                    // Input ready before the playback has room (the two directions' periods aren't aligned)
                    if (space < period)
                    {
                        int waited = AlsaNative.PcmWait(playback.Pcm, timeout);

                        if (waited <= 0)
                            return waited;

                        continue;
                    }
                }

                int error = Cycle();

                if (error < 0)
                    return error;
            }

            return 0;
        }

        // One period: the input in, the client, the output out
        private int Cycle()
        {
            StreamBuffers b = buffers;
            int error;

            if (capture.Pcm != 0)
            {
                if ((error = Transfer(ref capture, b.InputArrays, read: true)) < 0)
                    return error;
            }

            try
            {
                client.Process(b.Buffers(period, xruns: Interlocked.Exchange(ref xruns, 0)));
            }
            catch (Exception)
            {
                // The client reports its own failures; the device plays silence for the block.
                for (int o = 0; o < b.Outputs.Length; o++)
                    Array.Clear(b.Outputs[o], 0, period);
            }

            if (playback.Pcm != 0)
            {
                if ((error = Transfer(ref playback, b.OutputArrays, read: false)) < 0)
                    return error;
            }

            return 0;
        }

        // A period between the device's mmap buffer and the client's channels (the buffer can wrap partway)
        private int Transfer(ref Direction direction, nint[] channels, bool read)
        {
            int done = 0;

            while (done < period)
            {
                nuint frames = (nuint)(period - done);
                int error = AlsaNative.PcmMmapBegin(direction.Pcm, out AlsaNative.ChannelArea* areas, out nuint offset, ref frames);

                if (error < 0)
                    return error;

                int count = (int)frames;

                for (int c = 0; c < direction.Selected.Length; c++)
                {
                    ref AlsaNative.ChannelArea area = ref areas[direction.Selected[c]];
                    float* samples = (float*)channels[c] + done;

                    if (read)
                        AlsaSamples.Read(direction.Format, area, offset, count, samples);
                    else
                        AlsaSamples.Write(direction.Format, area, offset, count, samples);
                }

                nint committed = AlsaNative.PcmMmapCommit(direction.Pcm, offset, frames);

                if (committed < 0)
                    return (int)committed;

                if (committed != (nint)frames)
                    return -AlsaNative.EPIPE;

                done += count;
            }

            return 0;
        }

        // Prepares both directions, fills the playback buffer with silence (the output latency), and starts them
        private int StartDevices()
        {
            int error;

            if ((capture.Pcm != 0) && ((error = AlsaNative.PcmPrepare(capture.Pcm)) < 0))
                return error;

            if (playback.Pcm != 0)
            {
                if ((error = AlsaNative.PcmPrepare(playback.Pcm)) < 0)
                    return error;

                if ((error = FillSilence()) < 0)
                    return error;
            }

            if ((error = AlsaNative.PcmStart((capture.Pcm != 0) ? capture.Pcm : playback.Pcm)) < 0)
                return error;

            if (!linked && (capture.Pcm != 0) && (playback.Pcm != 0))
                return AlsaNative.PcmStart(playback.Pcm);

            return 0;
        }

        // The whole playback buffer, every channel (the ones the client doesn't use stay silent)
        private int FillSilence()
        {
            nint space = AlsaNative.PcmAvailUpdate(playback.Pcm);

            if (space < 0)
                return (int)space;

            int left = (int)space;

            while (left > 0)
            {
                nuint frames = (nuint)left;
                int error = AlsaNative.PcmMmapBegin(playback.Pcm, out AlsaNative.ChannelArea* areas, out nuint offset, ref frames);

                if (error < 0)
                    return error;

                for (int c = 0; c < playback.Channels; c++)
                    AlsaSamples.Silence(playback.Format, areas[c], offset, (int)frames);

                nint committed = AlsaNative.PcmMmapCommit(playback.Pcm, offset, frames);

                if (committed < 0)
                    return (int)committed;

                left -= (int)frames;
            }

            return 0;
        }

        // An xrun (or a suspend) restarts both directions, counted for the client; a device that's gone fails the stream
        private bool Recover(int error)
        {
            if (stopping)
                return false;

            if ((-error == AlsaNative.ENODEV) || (-error == AlsaNative.EBADFD) || (-error == AlsaNative.ENOENT))
            {
                Fail("The ALSA device was disconnected.");

                return false;
            }

            Interlocked.Increment(ref xruns);

            if (-error == AlsaNative.ESTRPIPE)
            {
                // Suspended (the machine slept): resume, or restart if the device can't
                for (int tries = 0; (tries < 100) && !stopping; tries++)
                {
                    int resumed = AlsaNative.PcmResume((capture.Pcm != 0) ? capture.Pcm : playback.Pcm);

                    if (resumed != -AlsaNative.EAGAIN)
                        break;

                    Thread.Sleep(10);
                }
            }

            if (capture.Pcm != 0)
                AlsaNative.PcmDrop(capture.Pcm);

            if (playback.Pcm != 0)
                AlsaNative.PcmDrop(playback.Pcm);

            int restarted = StartDevices();

            if (restarted < 0)
            {
                Fail($"The ALSA device couldn't restart after a dropout: {AlsaNative.Error(restarted)}.");

                return false;
            }

            return true;
        }

        // Reported off the audio thread, which then ends
        private void Fail(string message)
        {
            stopping = true;

            ThreadPool.QueueUserWorkItem(_ => OnFailed(message));
        }
    }
}
