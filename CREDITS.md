# Credits

AudioIOSharp builds on other people's work. AudioIOSharp itself is MIT licensed (see `LICENSE`); each dependency keeps its own license, given below.

## Dependencies

### Submodules

- [JackSharpCore](https://github.com/mikeoliphant/JackSharpCore) (MIT), in `Dependencies/JackSharpCore`: a .NET wrapper of the JACK audio library, for the JACK driver. It's a fork of [JackSharp](https://github.com/residuum/JackSharp) by Thomas Mayer, by way of [h3ll5ur7er/JackSharp](https://github.com/h3ll5ur7er/JackSharp)'s move to .NET Core. Its process callback hands out spans of JACK's own buffers, which AudioIOSharp passes on to clients without copying.

### NuGet packages

- [NAudio](https://github.com/naudio/NAudio) by Mark Heath (`NAudio.Asio`, with `NAudio.Core`; MIT): the ASIO driver. Its `AsioDevice` duplex mode runs one callback per buffer with each channel as a float span, converting to and from the driver's native sample format, and reports driver resets, resyncs and latency changes. It calls ASIO drivers through its own COM interop, and includes no Steinberg ASIO SDK code. ASIO is a trademark of Steinberg Media Technologies GmbH.
- [xUnit.net](https://github.com/xunit/xunit) by James Newkirk and Brad Wilson (`xunit`, `xunit.runner.visualstudio`; Apache 2.0) and the [.NET test SDK](https://github.com/microsoft/vstest) (`Microsoft.NET.Test.Sdk`; MIT): the tests only.

### At run time

- [JACK](https://jackaudio.org/): the JACK driver loads the JACK client library installed on the system (JACK2, or PipeWire's JACK on Linux). It isn't part of AudioIOSharp, and isn't distributed with it.
- ASIO drivers are the device makers', installed with their devices.

## Origins

- [SpiceForge](https://github.com/mikeoliphant/SpiceForge) (MIT): AudioIOSharp started as SpiceForge's audio layer, and its design comes from what SpiceForge learned running circuit simulations in real time on audio devices: one STA thread for every ASIO driver call, recovering from driver resets, abandoning (rather than disposing) an ASIO device whose reset failed, a JACK client that stays in the graph, falling back to the internal clock, and running without tiered compilation. SpiceForge's `DesignDocs/LessonsLearned.md` (Audio I/O) has the measurements behind them.
