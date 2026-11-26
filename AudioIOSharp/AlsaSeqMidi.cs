using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AudioIOSharp;

/// <summary>
/// Linux's MIDI inputs, through the ALSA sequencer (libasound): hardware ports and other programs' ports alike. Each
/// input opened is a sequencer client of its own, with a port subscribed to the input's; a thread of its own reads its
/// events, decodes each one to its bytes, stamps it and puts it in the input's ring: nothing allocated, nothing locked.
/// Unlike WinMM, a port can feed any number of programs at once.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe partial class AlsaSeqMidi
{
    private const string Library = AlsaNative.Library;

    private const int OpenInput = 2;
    private const int NonBlock = 1;

    private const uint CapRead = 1 << 0;
    private const uint CapWrite = 1 << 1;
    private const uint CapSubsRead = 1 << 5;
    private const uint CapSubsWrite = 1 << 6;
    private const uint CapNoExport = 1 << 7;

    private const uint TypeMidiGeneric = 1 << 1;
    private const uint TypeApplication = 1 << 20;

    private const int SystemClient = 0;
    private const short PollIn = 1;
    private const int PollTimeoutMs = 100;   // How long closing an input waits for its thread, at most

    private const int ENOSPC = 28;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    private readonly record struct Port(string Name, int Client, int Number);

    /// <summary>The inputs' names, made unique as WinMM's are, in the sequencer's order (by client, then port).</summary>
    public static IReadOnlyList<string> Inputs() => Ports().Select(p => p.Name).ToList();

    public static MidiInput Open(string name)
    {
        foreach (Port port in Ports())
        {
            if (string.Equals(port.Name, name, StringComparison.OrdinalIgnoreCase))
                return new Input(port);
        }

        throw new AudioException(AudioErrorKind.NotFound, $"There's no MIDI input '{name}'.");
    }

    /// <summary>The ports other programs can subscribe to for reading, but the system's own (timer, announcements).
    /// A port is named by its own name when that starts with its client's (a USB keyboard's "MPK mini 3 MIDI 1"), else
    /// by both ("VMPK Output: out").</summary>
    private static List<Port> Ports()
    {
        var ports = new List<Port>();

        if (snd_seq_open(out nint seq, "default", OpenInput, NonBlock) < 0)
            return ports;   // No sequencer (no snd-seq module): no inputs

        nint clientInfo = 0;
        nint portInfo = 0;

        try
        {
            if ((snd_seq_client_info_malloc(out clientInfo) < 0) || (snd_seq_port_info_malloc(out portInfo) < 0))
                return ports;

            int self = snd_seq_client_id(seq);

            snd_seq_client_info_set_client(clientInfo, -1);

            while (snd_seq_query_next_client(seq, clientInfo) >= 0)
            {
                int client = snd_seq_client_info_get_client(clientInfo);

                if ((client == SystemClient) || (client == self))
                    continue;

                string clientName = Marshal.PtrToStringUTF8(snd_seq_client_info_get_name(clientInfo)) ?? "";

                snd_seq_port_info_set_client(portInfo, client);
                snd_seq_port_info_set_port(portInfo, -1);

                while (snd_seq_query_next_port(seq, portInfo) >= 0)
                {
                    uint caps = snd_seq_port_info_get_capability(portInfo);

                    if (((caps & (CapRead | CapSubsRead)) != (CapRead | CapSubsRead)) || ((caps & CapNoExport) != 0))
                        continue;

                    string portName = (Marshal.PtrToStringUTF8(snd_seq_port_info_get_name(portInfo)) ?? "").Trim();
                    string name = PortName(clientName.Trim(), portName);
                    string unique = name;

                    for (int n = 2; ports.Any(p => string.Equals(p.Name, unique, StringComparison.OrdinalIgnoreCase)); n++)
                        unique = $"{name} ({n})";

                    ports.Add(new Port(unique, client, snd_seq_port_info_get_port(portInfo)));
                }
            }
        }
        finally
        {
            if (portInfo != 0)
                snd_seq_port_info_free(portInfo);

            if (clientInfo != 0)
                snd_seq_client_info_free(clientInfo);

            snd_seq_close(seq);
        }

        return ports;
    }

    internal static string PortName(string client, string port)
    {
        if (port.Length == 0)
            return (client.Length > 0) ? client : "MIDI input";

        if ((client.Length == 0) || port.StartsWith(client, StringComparison.OrdinalIgnoreCase))
            return port;

        return $"{client}: {port}";
    }

    private class Input : MidiInput
    {
        private readonly string name;
        private readonly Thread thread;
        private nint seq;
        private nint decoder;
        private volatile bool stopping;

        public Input(Port port)
        {
            name = port.Name;

            try
            {
                int error = snd_seq_open(out seq, "default", OpenInput, NonBlock);

                if (error < 0)
                {
                    seq = 0;

                    throw Error(error);
                }

                snd_seq_set_client_name(seq, "AudioIOSharp");

                int own = snd_seq_create_simple_port(seq, name, CapWrite | CapSubsWrite, TypeMidiGeneric | TypeApplication);

                if (own < 0)
                    throw Error(own);

                error = snd_seq_connect_from(seq, own, port.Client, port.Number);

                if (error < 0)
                    throw Error(error);

                error = snd_midi_event_new(16, out decoder);

                if (error < 0)
                {
                    decoder = 0;

                    throw Error(error);
                }

                // Every message with its status byte: the ring holds whole messages
                snd_midi_event_no_status(decoder, 1);
            }
            catch
            {
                Close();

                throw;
            }

            thread = new Thread(Run) { IsBackground = true, Name = "ALSA MIDI input", Priority = ThreadPriority.Highest };
            thread.Start();
        }

        // The input's thread: waits for events (waking now and then to see whether the input is closing), then reads
        // every one waiting
        private void Run()
        {
            int count = Math.Clamp(snd_seq_poll_descriptors_count(seq, PollIn), 1, 8);
            PollFd* fds = stackalloc PollFd[count];
            byte* bytes = stackalloc byte[16];

            count = snd_seq_poll_descriptors(seq, fds, (uint)count, PollIn);

            while (!stopping)
            {
                if (poll(fds, (nuint)count, PollTimeoutMs) <= 0)
                    continue;

                while (!stopping)
                {
                    int result = snd_seq_event_input(seq, out nint ev);

                    if (result == -ENOSPC)
                        continue;   // Events were lost (the queue overran): read on

                    if ((result < 0) || (ev == 0))
                        break;      // -EAGAIN: none left

                    long time = Stopwatch.GetTimestamp();
                    nint size = snd_midi_event_decode(decoder, bytes, 16, ev);

                    if (size <= 0)
                    {
                        // Not MIDI (a sequencer event of its own), or a sysex too long to decode
                        snd_midi_event_reset_decode(decoder);

                        continue;
                    }

                    if (!MidiEvent.Passes(bytes[0]) || (size < MidiEvent.SizeOf(bytes[0])))
                        continue;

                    int message = bytes[0] | ((size > 1) ? bytes[1] << 8 : 0) | ((size > 2) ? bytes[2] << 16 : 0);

                    Ring.Write(time, message);
                }
            }
        }

        public override void Dispose()
        {
            stopping = true;

            thread.Join();

            Close();
        }

        private void Close()
        {
            if (decoder != 0)
            {
                snd_midi_event_free(decoder);

                decoder = 0;
            }

            // Closing the client takes its port and subscription with it
            if (seq != 0)
            {
                snd_seq_close(seq);

                seq = 0;
            }
        }

        private AudioException Error(int error)
        {
            if (error == -AlsaNative.EBUSY)
                return new AudioException(AudioErrorKind.Busy, $"The MIDI input '{name}' is in use by another program.");

            if ((error == -AlsaNative.ENOENT) || (error == -AlsaNative.ENODEV))
                return new AudioException(AudioErrorKind.NotFound, $"There's no MIDI input '{name}'.");

            return new AudioException(AudioErrorKind.Unavailable,
                $"Couldn't open the MIDI input '{name}': {AlsaNative.Error(error)}");
        }
    }

    // ---- The sequencer ----------------------------------------------------------------------------------------------

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int snd_seq_open(out nint seq, string name, int streams, int mode);

    [LibraryImport(Library)]
    private static partial int snd_seq_close(nint seq);

    [LibraryImport(Library)]
    private static partial int snd_seq_client_id(nint seq);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int snd_seq_set_client_name(nint seq, string name);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int snd_seq_create_simple_port(nint seq, string name, uint caps, uint type);

    [LibraryImport(Library)]
    private static partial int snd_seq_connect_from(nint seq, int ownPort, int sourceClient, int sourcePort);

    [LibraryImport(Library)]
    private static partial int snd_seq_poll_descriptors_count(nint seq, short events);

    [LibraryImport(Library)]
    private static partial int snd_seq_poll_descriptors(nint seq, PollFd* fds, uint space, short events);

    [LibraryImport(Library)]
    private static partial int snd_seq_event_input(nint seq, out nint ev);

    [LibraryImport(Library)]
    private static partial int snd_seq_client_info_malloc(out nint info);

    [LibraryImport(Library)]
    private static partial void snd_seq_client_info_free(nint info);

    [LibraryImport(Library)]
    private static partial void snd_seq_client_info_set_client(nint info, int client);

    [LibraryImport(Library)]
    private static partial int snd_seq_client_info_get_client(nint info);

    [LibraryImport(Library)]
    private static partial nint snd_seq_client_info_get_name(nint info);

    [LibraryImport(Library)]
    private static partial int snd_seq_query_next_client(nint seq, nint info);

    [LibraryImport(Library)]
    private static partial int snd_seq_port_info_malloc(out nint info);

    [LibraryImport(Library)]
    private static partial void snd_seq_port_info_free(nint info);

    [LibraryImport(Library)]
    private static partial void snd_seq_port_info_set_client(nint info, int client);

    [LibraryImport(Library)]
    private static partial void snd_seq_port_info_set_port(nint info, int port);

    [LibraryImport(Library)]
    private static partial int snd_seq_port_info_get_port(nint info);

    [LibraryImport(Library)]
    private static partial nint snd_seq_port_info_get_name(nint info);

    [LibraryImport(Library)]
    private static partial uint snd_seq_port_info_get_capability(nint info);

    [LibraryImport(Library)]
    private static partial int snd_seq_query_next_port(nint seq, nint info);

    // ---- Sequencer events to MIDI bytes ------------------------------------------------------------------------------

    [LibraryImport(Library)]
    private static partial int snd_midi_event_new(nuint bufferSize, out nint decoder);

    [LibraryImport(Library)]
    private static partial void snd_midi_event_free(nint decoder);

    [LibraryImport(Library)]
    private static partial int snd_midi_event_no_status(nint decoder, int on);

    [LibraryImport(Library)]
    private static partial void snd_midi_event_reset_decode(nint decoder);

    [LibraryImport(Library)]
    private static partial nint snd_midi_event_decode(nint decoder, byte* buffer, nint count, nint ev);

    [LibraryImport("libc")]
    private static partial int poll(PollFd* fds, nuint count, int timeoutMs);
}
