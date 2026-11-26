using System.Diagnostics;

namespace AudioIOSharp;

/// <summary>
/// A MIDI message in a block: where in the block it falls, which of the stream's MIDI inputs it came from, and its bytes
/// (a short message: the status byte and up to two data bytes; sysex isn't passed on).
/// </summary>
public readonly struct MidiEvent
{
    public MidiEvent(int offset, int port, byte status, byte data1, byte data2)
    {
        Offset = offset;
        Port = port;
        Status = status;
        Data1 = data1;
        Data2 = data2;
    }

    /// <summary>The frame in the block it falls on.</summary>
    public int Offset { get; }

    /// <summary>The input it came from, by index into <see cref="AudioFormat.MidiInputs"/>.</summary>
    public int Port { get; }

    public byte Status { get; }

    public byte Data1 { get; }

    public byte Data2 { get; }

    /// <summary>The message's kind, without its channel (0x90 for a note on), for channel messages; the status itself
    /// for system messages.</summary>
    public int Command => Status < 0xF0 ? Status & 0xF0 : Status;

    /// <summary>The channel (0-15), for channel messages.</summary>
    public int Channel => Status & 0x0F;

    /// <summary>The message's length in bytes, from its status byte.</summary>
    public static int SizeOf(byte status) => status switch
    {
        < 0xC0 => 3,           // Note off, note on, poly pressure, control change
        < 0xE0 => 2,           // Program change, channel pressure
        < 0xF0 => 3,           // Pitch bend
        0xF1 or 0xF3 => 2,     // Time code quarter frame, song select
        0xF2 => 3,             // Song position
        _ => 1,                // Tune request, real-time messages
    };

    /// <summary>Whether a message is passed on: a complete short message with a status byte, other than sysex and active
    /// sensing (the link's keep-alive, not music).</summary>
    internal static bool Passes(byte status) =>
        (status >= 0x80) && (status != 0xF0) && (status != 0xF7) && (status != 0xFE);

    /// <summary>A message's bytes as an event, if it's one that's passed on.</summary>
    internal static bool TryParse(ReadOnlySpan<byte> data, int offset, int port, out MidiEvent midi)
    {
        midi = default;

        if ((data.Length == 0) || !Passes(data[0]))
            return false;

        int size = SizeOf(data[0]);

        if (data.Length < size)
            return false;

        midi = new MidiEvent(offset, port, data[0], (size > 1) ? data[1] : (byte)0, (size > 2) ? data[2] : (byte)0);

        return true;
    }
}

/// <summary>
/// A stream's MIDI for one block, made ahead so the audio thread allocates nothing: events are added as they're
/// gathered from the inputs, then put in time order (stably, so each input's own order is kept).
/// </summary>
internal class MidiBuffer
{
    /// <summary>The most events a block carries; more are dropped.</summary>
    public const int Capacity = 1024;

    private readonly MidiEvent[] events = new MidiEvent[Capacity];

    public int Count { get; private set; }

    public ReadOnlySpan<MidiEvent> Events => events.AsSpan(0, Count);

    public void Clear() => Count = 0;

    public void Add(in MidiEvent midi)
    {
        if (Count < Capacity)
            events[Count++] = midi;
    }

    /// <summary>Puts the events in order of offset: an insertion sort, stable, and quick for what's nearly in order
    /// already (one input's events are).</summary>
    public void Sort()
    {
        for (int i = 1; i < Count; i++)
        {
            MidiEvent midi = events[i];
            int j = i - 1;

            while ((j >= 0) && (events[j].Offset > midi.Offset))
            {
                events[j + 1] = events[j];
                j--;
            }

            events[j + 1] = midi;
        }
    }
}

/// <summary>
/// Timestamped short messages from one producer thread (a MIDI driver's callback) to one consumer (the audio thread),
/// lock-free and allocation-free. A message that finds the ring full is dropped.
/// </summary>
internal class MidiRing
{
    private readonly long[] times;
    private readonly int[] messages;
    private readonly int mask;
    private int head;   // The next slot written: the producer's.
    private int tail;   // The next slot read: the consumer's.

    /// <param name="capacity">A power of two.</param>
    public MidiRing(int capacity = 1024)
    {
        if ((capacity <= 0) || ((capacity & (capacity - 1)) != 0))
            throw new ArgumentOutOfRangeException(nameof(capacity), "The capacity is a power of two.");

        times = new long[capacity];
        messages = new int[capacity];
        mask = capacity - 1;
    }

    /// <summary>The producer: a message (its bytes in the low three bytes, status lowest) and when it arrived.</summary>
    public bool Write(long time, int message)
    {
        int h = head;

        if (h - Volatile.Read(ref tail) > mask)
            return false;

        times[h & mask] = time;
        messages[h & mask] = message;

        Volatile.Write(ref head, h + 1);

        return true;
    }

    /// <summary>The consumer: the oldest message, if any, left in the ring until <see cref="Advance"/>.</summary>
    public bool Peek(out long time, out int message)
    {
        int t = tail;

        if (t == Volatile.Read(ref head))
        {
            time = 0;
            message = 0;

            return false;
        }

        time = times[t & mask];
        message = messages[t & mask];

        return true;
    }

    public void Advance() => Volatile.Write(ref tail, tail + 1);
}

/// <summary>A system MIDI input (not JACK's): its messages arrive, timestamped, in its ring.</summary>
internal abstract class MidiInput : IDisposable
{
    public MidiRing Ring { get; } = new();

    public abstract void Dispose();
}

/// <summary>The system's MIDI inputs, for drivers whose devices carry no MIDI of their own (every driver but JACK):
/// WinMM's on Windows; none elsewhere yet.</summary>
internal static class SystemMidi
{
    public static IReadOnlyList<string> Inputs()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WinMmMidi.Inputs();
        }
        catch (Exception)
        {
            // No MIDI system to ask: no inputs.
        }

        return Array.Empty<string>();
    }

    public static MidiInput Open(string name)
    {
        if (OperatingSystem.IsWindows())
            return WinMmMidi.Open(name);

        throw new AudioException(AudioErrorKind.Unsupported, "MIDI inputs aren't supported on this platform yet.");
    }
}

/// <summary>
/// A stream's system MIDI inputs, opened with it and closed with it. Messages arrive between blocks, timestamped; each
/// block takes the ones that arrived during the previous block and places them at the same position in this one: one
/// block late, with their timing kept (rather than all at the start, a block's worth of jitter). An input that isn't
/// there or can't be opened (another program has it) is left out, and said so in <see cref="Errors"/>: the audio runs
/// regardless.
/// </summary>
internal class TimedMidiInputs : IDisposable
{
    private readonly MidiInput[] inputs;
    private long previousStart;   // Stopwatch ticks at the previous block's start; 0 before the first.

    public TimedMidiInputs(AudioConfig config)
    {
        var (present, missing) = config.MidiInputsIn(SystemMidi.Inputs());
        var opened = new List<MidiInput>();
        var names = new List<string>();
        var errors = missing.Select(m => $"The MIDI input '{m}' isn't connected.").ToList();

        foreach (string name in present)
        {
            try
            {
                opened.Add(SystemMidi.Open(name));
                names.Add(name);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        inputs = opened.ToArray();
        Names = names;
        Errors = errors;
    }

    /// <summary>Inputs already open (the tests').</summary>
    internal TimedMidiInputs(MidiInput[] inputs, IReadOnlyList<string> names)
    {
        this.inputs = inputs;
        Names = names;
        Errors = Array.Empty<string>();
    }

    /// <summary>The inputs opened, in the order of <see cref="MidiEvent.Port"/>.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>Why inputs the configuration names weren't opened.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>The audio thread, at the start of a block: the messages that arrived during the previous block, at
    /// their positions in this one.</summary>
    public void Fill(MidiBuffer buffer, int frames, double sampleRate)
    {
        buffer.Clear();

        long now = Stopwatch.GetTimestamp();
        long start = previousStart;

        previousStart = now;

        if (inputs.Length == 0)
            return;

        double framesPerTick = sampleRate / Stopwatch.Frequency;

        for (int port = 0; port < inputs.Length; port++)
        {
            MidiRing ring = inputs[port].Ring;

            while (ring.Peek(out long time, out int message) && (time <= now))
            {
                buffer.Add(new MidiEvent(Offset(time, start, frames, framesPerTick), port,
                    (byte)message, (byte)(message >> 8), (byte)(message >> 16)));

                ring.Advance();
            }
        }

        buffer.Sort();
    }

    /// <summary>Where a message that arrived at <paramref name="time"/> falls in a block: as far into it as it came
    /// after the previous block started, within the block (a message from before then, or before the first block, at
    /// its start).</summary>
    internal static int Offset(long time, long previousStart, int frames, double framesPerTick)
    {
        if ((previousStart == 0) || (time <= previousStart) || (frames <= 1))
            return 0;

        double offset = (time - previousStart) * framesPerTick;

        return (int)Math.Min(offset, frames - 1);
    }

    public void Dispose()
    {
        foreach (MidiInput input in inputs)
        {
            try
            {
                input.Dispose();
            }
            catch (Exception)
            {
                // Closing a device that's gone.
            }
        }
    }
}
