using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AudioIOSharp;

/// <summary>
/// Windows' MIDI inputs, through WinMM (winmm.dll), which every version of Windows has. A device's short messages
/// arrive on a callback thread of WinMM's, which stamps each one as it comes and puts it in the input's ring: nothing
/// allocated, nothing locked. Before Windows MIDI Services, a device is open in one program at a time.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WinMmMidi
{
    private const string Library = "winmm.dll";

    private const uint CallbackFunction = 0x30000;
    private const uint MimData = 0x3C3;
    private const uint MmsyserrAllocated = 4;
    private const uint MmsyserrNoMem = 7;   // What USB MIDI drivers give for a device another program has open

    [StructLayout(LayoutKind.Sequential)]
    private struct MidiInCaps
    {
        public ushort Mid;
        public ushort Pid;
        public uint DriverVersion;
        public fixed char Name[32];
        public uint Support;
    }

    /// <summary>The inputs' names, made unique (two of the same keyboard: "Keyboard", "Keyboard (2)"), in WinMM's
    /// order, which is the order of their device ids.</summary>
    public static IReadOnlyList<string> Inputs()
    {
        uint count = midiInGetNumDevs();
        var names = new List<string>();

        for (uint id = 0; id < count; id++)
        {
            MidiInCaps caps;
            string name = (midiInGetDevCapsW(id, &caps, (uint)sizeof(MidiInCaps)) == 0)
                ? new string(caps.Name).TrimEnd('\0').Trim()
                : "";

            if (name.Length == 0)
                name = $"MIDI input {id + 1}";

            string unique = name;

            for (int n = 2; names.Contains(unique, StringComparer.OrdinalIgnoreCase); n++)
                unique = $"{name} ({n})";

            names.Add(unique);
        }

        return names;
    }

    public static MidiInput Open(string name)
    {
        IReadOnlyList<string> names = Inputs();
        int id = names.ToList().FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

        if (id < 0)
            throw new AudioException(AudioErrorKind.NotFound, $"There's no MIDI input '{name}'.");

        return new Input((uint)id, names[id]);
    }

    private class Input : MidiInput
    {
        private nint handle;
        private GCHandle self;

        public Input(uint id, string name)
        {
            self = GCHandle.Alloc(this);

            nint callback = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint, void>)&Callback;
            uint error = midiInOpen(out handle, id, callback, GCHandle.ToIntPtr(self), CallbackFunction);

            if (error != 0)
            {
                handle = 0;
                self.Free();

                throw Error(name, error);
            }

            error = midiInStart(handle);

            if (error != 0)
            {
                Dispose();

                throw Error(name, error);
            }
        }

        public override void Dispose()
        {
            if (handle == 0)
                return;

            midiInStop(handle);
            midiInReset(handle);
            midiInClose(handle);

            // No callback comes after the close.
            handle = 0;

            self.Free();
        }
    }

    // WinMM's callback thread: only the messages that are passed on, into the input's ring.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void Callback(nint device, uint message, nint instance, nint param1, nint param2)
    {
        if ((message != MimData) || (instance == 0))
            return;

        int data = (int)param1 & 0xFFFFFF;

        if (!MidiEvent.Passes((byte)data))
            return;

        if (GCHandle.FromIntPtr(instance).Target is Input input)
            input.Ring.Write(Stopwatch.GetTimestamp(), data);
    }

    private static AudioException Error(string name, uint error)
    {
        if ((error == MmsyserrAllocated) || (error == MmsyserrNoMem))
            return new AudioException(AudioErrorKind.Busy, $"The MIDI input '{name}' is in use by another program.");

        char* text = stackalloc char[256];
        string reason = (midiInGetErrorTextW(error, text, 256) == 0) ? new string(text).Trim() : $"error {error}";

        return new AudioException(AudioErrorKind.Unavailable, $"Couldn't open the MIDI input '{name}': {reason}");
    }

    [LibraryImport(Library)]
    private static partial uint midiInGetNumDevs();

    [LibraryImport(Library)]
    private static partial uint midiInGetDevCapsW(nuint deviceId, MidiInCaps* caps, uint size);

    [LibraryImport(Library)]
    private static partial uint midiInOpen(out nint handle, uint deviceId, nint callback, nint instance, uint flags);

    [LibraryImport(Library)]
    private static partial uint midiInStart(nint handle);

    [LibraryImport(Library)]
    private static partial uint midiInStop(nint handle);

    [LibraryImport(Library)]
    private static partial uint midiInReset(nint handle);

    [LibraryImport(Library)]
    private static partial uint midiInClose(nint handle);

    [LibraryImport(Library)]
    private static partial uint midiInGetErrorTextW(uint error, char* text, uint size);
}
