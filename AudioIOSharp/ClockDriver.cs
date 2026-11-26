using System.Diagnostics;

namespace AudioIOSharp;

/// <summary>
/// No audio device: a thread that is the clock. It calls the client for a block whenever the wall clock says one is
/// due, with no input or output channels, so the client runs in real time without a device. It's the default, and what
/// a client falls back to when its device can't be opened or fails.
/// </summary>
public class ClockDriver : AudioDriver
{
    public const string Name = "None";
    public const string DeviceName = "Internal clock";
    public const int DefaultSampleRate = 48000;
    public const int DefaultBufferSize = 256;

    private static readonly AudioDeviceInfo info = new(
        DeviceName, Array.Empty<string>(), Array.Empty<string>(),
        new[] { 44100, 48000, 88200, 96000, 176400, 192000 },
        new[] { 32, 64, 128, 256, 512, 1024, 2048 },
        DefaultSampleRate, DefaultBufferSize, ControlPanel: false);

    public override string DriverName => Name;

    public override Task<IReadOnlyList<string>> Devices() => Task.FromResult<IReadOnlyList<string>>(new[] { DeviceName });

    public override Task<AudioDeviceInfo> Info(string device) => Task.FromResult(info with { MidiInputs = SystemMidi.Inputs() });

    public override Task<AudioStream> Open(AudioConfig config, IAudioClient? client)
    {
        ArgumentNullException.ThrowIfNull(client);
        // A rate or size this clock doesn't offer (a fallback from a device's settings) gets the default.
        int rate = config.SampleRate is int r && info.SampleRates.Contains(r) ? r : DefaultSampleRate;
        int size = config.BufferSize is int b && info.BufferSizes.Contains(b) ? b : DefaultBufferSize;
        return Task.FromResult<AudioStream>(new ClockStream(rate, size, client, new TimedMidiInputs(config)));
    }

    private class ClockStream : AudioStream
    {
        private readonly IAudioClient client;
        private readonly TimedMidiInputs midi;
        private readonly MidiBuffer midiBuffer = new();
        private readonly Thread thread;
        private volatile bool stopping;

        public ClockStream(int sampleRate, int bufferSize, IAudioClient client, TimedMidiInputs midi)
        {
            this.client = client;
            this.midi = midi;
            BufferSize = bufferSize;
            Format = new AudioFormat(sampleRate, bufferSize, Array.Empty<string>(), Array.Empty<string>()) { MidiInputs = midi.Names };

            try
            {
                client.Prepare(Format);
            }
            catch
            {
                midi.Dispose();

                throw;
            }

            thread = new Thread(Run) { IsBackground = true, Name = "Audio clock", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        public override string Driver => Name;
        public override string Device => DeviceName;
        public override AudioFormat Format { get; }
        public override int BufferSize { get; }
        public override IReadOnlyList<string> MidiErrors => midi.Errors;

        public override Task Close()
        {
            stopping = true;
            thread.Join();

            midi.Dispose();

            return Task.CompletedTask;
        }

        private void Run()
        {
            double rate = Format.SampleRate;
            int frames = BufferSize;
            var clock = Stopwatch.StartNew();
            long sample = 0;   // The next sample due, counted from when the stream started.
            while (!stopping)
            {
                double due = clock.Elapsed.TotalSeconds * rate;
                if (sample + frames > due)
                {
                    Thread.Sleep(1);
                    continue;
                }
                long skipped = 0;
                if (due - sample > rate / 4)
                {
                    // More than a quarter second behind: skip ahead rather than spiral, and say so.
                    long ahead = (long)due - frames;
                    skipped = ahead - sample;
                    sample = ahead;
                }

                midi.Fill(midiBuffer, frames, rate);

                var buffers = new AudioBuffers(default, default, frames, skipped, midi: midiBuffer.Events);
                client.Process(in buffers);
                sample += frames;
            }
        }
    }
}
