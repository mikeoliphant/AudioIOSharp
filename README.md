# AudioIOSharp

Real-time, allocation-free audio I/O for .NET, on Windows and Linux.

Shared by [AudioPlugSharp](https://github.com/mikeoliphant/AudioPlugSharp) (its standalone host) and SpiceForge.

The code in **AudioIOSharp/** is SpiceForge's audio implementation (SpiceForgeServer/Audio, as of SpiceForge commit ec85495), copied unchanged. It still depends on SpiceForge and doesn't build on its own yet: it is about to be refactored into a library of its own.
