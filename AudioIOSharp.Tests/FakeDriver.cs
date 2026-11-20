using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AudioIOSharp.Tests;

/// <summary>
/// A driver for the tests: one device ("Fake") with two inputs and two outputs at 48 kHz, whose stream runs a block
/// only when the test asks (<see cref="FakeStream.Run"/>), and can be made to fail.
/// </summary>
public class FakeDriver(string name = "Fake", bool resident = false) : AudioDriver
{
    public const string DeviceName = "Fake device";

    public override string DriverName => name;

    public override bool KeepsOpen => resident;

    /// <summary>Opening fails with this message.</summary>
    public string? OpenError { get; set; }

    public FakeStream? Stream { get; private set; }

    public int Opened { get; private set; }

    public static AudioDeviceInfo Info0 { get; } = new(DeviceName, ["In 1", "In 2"], ["Out 1", "Out 2"], [44100, 48000], [128, 256], 48000, 256, false);

    public override Task<IReadOnlyList<string>> Devices() => Task.FromResult<IReadOnlyList<string>>([DeviceName]);

    public override Task<AudioDeviceInfo> Info(string device) => Task.FromResult(Info0);

    public override Task<AudioStream> Open(AudioConfig config, IAudioClient? client)
    {
        if (OpenError != null)
            throw new AudioException(AudioErrorKind.Unavailable, OpenError);
        config = config.Checked(Info0);
        Opened++;
        Stream = new FakeStream(this, config, client, resident);
        return Task.FromResult<AudioStream>(Stream);
    }
}

public unsafe class FakeStream : AudioStream
{
    private readonly FakeDriver owner;
    private readonly bool resident;
    private readonly float[][] inputs, outputs;
    private readonly nint[] inputPointers, outputPointers;
    private IAudioClient? client;

    public FakeStream(FakeDriver owner, AudioConfig config, IAudioClient? client, bool resident)
    {
        this.owner = owner;
        this.resident = resident;
        this.client = client;
        var (ins, outs) = config.Channels(FakeDriver.Info0);
        Format = new AudioFormat(config.SampleRate ?? 48000, config.BufferSize ?? 256,
            ins.Select(i => FakeDriver.Info0.Inputs[i]).ToArray(), outs.Select(o => FakeDriver.Info0.Outputs[o]).ToArray());
        inputs = Format.Inputs.Select(_ => GC.AllocateArray<float>(Format.MaxFrames, pinned: true)).ToArray();
        outputs = Format.Outputs.Select(_ => GC.AllocateArray<float>(Format.MaxFrames, pinned: true)).ToArray();
        inputPointers = inputs.Select(a => (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(a))).ToArray();
        outputPointers = outputs.Select(a => (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(a))).ToArray();
        client?.Prepare(Format);
    }

    public override string Driver => owner.DriverName;
    public override string Device => FakeDriver.DeviceName;
    public override AudioFormat Format { get; }
    public override int BufferSize => Format.MaxFrames;
    public override bool Resident => resident;
    public bool Closed { get; private set; }
    public float[][] Inputs => inputs;
    public float[][] Outputs => outputs;

    public override void SetClient(IAudioClient? newClient)
    {
        newClient?.Prepare(Format);
        client = newClient;
    }

    /// <summary>Runs a block, as a device's callback would.</summary>
    public void Run()
    {
        var buffers = new AudioBuffers(inputPointers, outputPointers, Format.MaxFrames);
        client?.Process(in buffers);
    }

    /// <summary>The device stops on its own (unplugged).</summary>
    public void Fail(string message) => OnFailed(message);

    public override Task Close()
    {
        Closed = true;
        return Task.CompletedTask;
    }
}

/// <summary>A client that counts its blocks, copies input 0 to every output, and records allocations between blocks.</summary>
public class TestClient : IAudioClient
{
    public AudioFormat? Format;
    public int Prepared, Blocks;
    public long LastAllocated = -1, AllocatedBetweenBlocks;
    public readonly SemaphoreSlim BlockRan = new(0);

    public void Prepare(AudioFormat format)
    {
        Format = format;
        Prepared++;
    }

    public void Process(in AudioBuffers buffers)
    {
        long now = GC.GetAllocatedBytesForCurrentThread();
        if (LastAllocated >= 0 && Blocks >= 4)
            AllocatedBetweenBlocks += now - LastAllocated;
        for (int o = 0; o < buffers.OutputCount; o++)
        {
            if (buffers.InputCount > 0)
                buffers.Input(0).CopyTo(buffers.Output(o));
            else
                buffers.Output(o).Clear();
        }
        Blocks++;
        BlockRan.Release(); // the test's own signal (it can allocate, waking a waiter): not counted
        LastAllocated = GC.GetAllocatedBytesForCurrentThread();
    }
}
