using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AudioIOSharp;

/// <summary>
/// A stream's buffers for one format, made when it is prepared so the audio thread allocates nothing: a pinned array
/// per channel, for drivers that copy (ASIO) or for a channel the device gives no buffer, and the channel pointer
/// tables each callback's <see cref="AudioBuffers"/> is made from. The tables point at the arrays, unless a driver
/// points them at the device's own buffers (JACK) for the callback.
/// </summary>
internal unsafe class StreamBuffers
{
    public StreamBuffers(AudioFormat format)
    {
        Capacity = Math.Max(format.MaxFrames, 1);
        Inputs = Pinned(format.Inputs.Count, Capacity);
        Outputs = Pinned(format.Outputs.Count, Capacity);
        InputArrays = Inputs.Select(Address).ToArray();
        OutputArrays = Outputs.Select(Address).ToArray();
        InputPointers = (nint[])InputArrays.Clone();
        OutputPointers = (nint[])OutputArrays.Clone();
    }

    /// <summary>The most frames a callback can carry: the length of every array.</summary>
    public int Capacity { get; }

    /// <summary>A pinned array per channel (pinned: on the pinned object heap, so their addresses never change).</summary>
    public float[][] Inputs { get; }
    public float[][] Outputs { get; }

    /// <summary>The arrays' addresses.</summary>
    public nint[] InputArrays { get; }
    public nint[] OutputArrays { get; }

    /// <summary>What the next callback's buffers point at, per channel.</summary>
    public nint[] InputPointers { get; }
    public nint[] OutputPointers { get; }

    /// <summary>The next callback's MIDI, gathered by the driver before it runs the client.</summary>
    public MidiBuffer Midi { get; } = new();

    public AudioBuffers Buffers(int frames, long skipped = 0, int xruns = 0) =>
        new(InputPointers, OutputPointers, frames, skipped, xruns, Midi.Events);

    /// <summary>The address of a span over native memory (or a pinned array).</summary>
    public static nint Address(Span<float> span) => (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));

    private static nint Address(float[] array) => (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));

    private static float[][] Pinned(int count, int length)
    {
        var arrays = new float[count][];
        for (int i = 0; i < count; i++)
            arrays[i] = GC.AllocateArray<float>(length, pinned: true);
        return arrays;
    }
}
