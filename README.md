# AudioIOSharp
Real-time, allocation-free audio and MIDI I/O for .NET

AudioIOSharp connects your C# code to audio devices on Windows and Linux. Your code gets a callback for every block of audio, with the inputs to read, the outputs to write and the block's MIDI input, and nothing is allocated on the audio thread. It started as the audio engine of [SpiceForge](https://github.com/mikeoliphant/SpiceForge), and is becoming the audio engine of [AudioPlugSharp](https://github.com/mikeoliphant/AudioPlugSharp)'s standalone host too.

# Features

- **Duplex audio:** one callback per block reads the inputs and writes the outputs.
- **MIDI input:** each block's MIDI messages come with the audio, in time order, each at its sample offset in the block, as with JACK.
- **No allocations on the audio thread:** each channel is a span, exactly one block long. With JACK, the spans are JACK's own buffers, with nothing copied.
- **Drivers:** ASIO (Windows), ALSA (Linux), JACK (Windows and Linux, including PipeWire's JACK) and **None**, an internal clock that runs your code in real time without a device.
- **Robust:** the **AudioEngine** keeps your code running whatever the device does. If the device can't be opened, it runs on the internal clock instead. If the device stops (unplugged, say), it switches to no device at the same sample rate. A new configuration is checked before the running device closes, and the previous one stays if the new one can't be used.
- **Switching devices without stopping:** moving to another device or driver carries on with the same code, which is prepared for the new format. A JACK client stays in the graph between runs, so connections made in a patchbay are kept.

# Using It

Implement **IAudioClient**:

```csharp
class Passthrough : IAudioClient
{
    public void Prepare(AudioFormat format)
    {
        // Called before the device starts, and again if its format changes: size your buffers here
    }

    public void Process(in AudioBuffers buffers)
    {
        // Called on the audio thread for every block: fill every output, and don't allocate, lock or block
        for (int output = 0; output < buffers.OutputCount; output++)
        {
            if (buffers.InputCount > 0)
                buffers.Input(0).CopyTo(buffers.Output(output));
            else
                buffers.Output(output).Clear();
        }
    }
}
```

Then run it on an **AudioEngine**:

```csharp
var config = new AudioConfig { Driver = "ASIO", Device = "My Interface", BufferSize = 128 };

using var engine = new AudioEngine(config, AudioEngine.PlatformDrivers("MyApp"));

engine.StateChanged += () => Console.WriteLine(engine.State);

await engine.Start();
await engine.Attach(new Passthrough());
```

A null field in an **AudioConfig** means the device's default: its first device, its first input and first two outputs, its current sample rate and its preferred buffer size.

# MIDI

Choose MIDI inputs by name in the configuration (none by default):

```csharp
var config = new AudioConfig { Driver = "ASIO", Device = "My Interface", MidiInputs = ["MPK mini 3"] };
```

Each block's messages are in **buffers.Midi**, in order of their sample offset:

```csharp
foreach (MidiEvent midi in buffers.Midi)
{
    if ((midi.Command == 0x90) && (midi.Data2 > 0))
        NoteOn(midi.Channel, midi.Data1, midi.Data2, midi.Offset);
}
```

Each event has its **Offset** in the block, the **Port** it came from (an index into **AudioFormat.MidiInputs**), and its **Status**, **Data1** and **Data2** bytes. Short messages are passed on; sysex isn't.

With JACK, the MIDI inputs are other programs' MIDI ports, and messages arrive at the sample offsets JACK gives. With the other drivers, they're the system's MIDI devices. Their messages arrive one block late, placed in the block where they arrived in the previous one, so their timing is kept.

A MIDI input that isn't there (a keyboard unplugged) or that another program has open doesn't stop the audio: the device opens without it, and **AudioState.MidiErrors** says why.

Each driver lists its devices (**Devices**) and what a device offers (**Info**: channels, MIDI inputs, sample rates and buffer sizes), for letting the user choose. **SetConfig** moves to a new configuration. Saving configurations is up to your application: the engine reports the configuration it uses, and tells you (**ConfigChanged**) when it changes it itself.

# Real-time Tips

- Set **<TieredCompilation>false</TieredCompilation>** in your application's project file. With tiered compilation, the runtime recompiles methods a few seconds after they first run, which can cause dropouts at small buffer sizes.
- Allocating anywhere in your process can pause the audio thread for a garbage collection. Keep allocation low while audio runs, not just in **Process**.

# Requirements

AudioIOSharp needs .NET 10 or newer.

- **ASIO** needs the device's ASIO driver installed. It uses [NAudio](https://github.com/naudio/NAudio)'s ASIO support.
- **ALSA** needs ALSA's library (libasound), which Linux distributions install. It opens sound cards directly, for the lowest latency, so a card that a sound server (PipeWire or PulseAudio) is using is busy: use JACK for those, or stop the server's use of the card. Its audio thread runs at real-time priority when the user may use it (the **audio** group on most distributions); without it, audio still runs, but drops out more easily. Playback is buffered by three periods of the buffer size, which USB devices need; on PCI, PCIe and onboard devices, **new AlsaDriver(periods: 2)** has one period less latency.
- **MIDI** uses Windows' MIDI devices (WinMM) with ASIO and the internal clock. On Linux, MIDI input is through JACK for now. Before Windows MIDI Services, a Windows MIDI device can be open in only one program at a time.
- **JACK** needs the JACK client library: [JACK2](https://jackaudio.org/) on Windows, or JACK2 or PipeWire's JACK on Linux. It uses [JackSharpCore](https://github.com/mikeoliphant/JackSharpCore).

# Building

Clone the repository with its submodules:

```bash
git clone --recurse-submodules https://github.com/mikeoliphant/AudioIOSharp
```

Then build **AudioIOSharp.slnx**, and run the tests with **dotnet test AudioIOSharp.Tests**.
