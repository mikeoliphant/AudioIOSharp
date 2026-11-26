using System.Diagnostics;

namespace AudioIOSharp.Tests;

public class MidiTests
{
    [Fact]
    public void Short_messages_parse_and_sysex_and_active_sensing_dont()
    {
        Assert.True(MidiEvent.TryParse([0x91, 60, 100], 5, 1, out MidiEvent noteOn));
        Assert.Equal((5, 1, 0x90, 1, 60, 100), (noteOn.Offset, noteOn.Port, noteOn.Command, noteOn.Channel, (int)noteOn.Data1, (int)noteOn.Data2));

        Assert.True(MidiEvent.TryParse([0xC2, 7], 0, 0, out MidiEvent program));
        Assert.Equal((0xC0, 2, 7, 0), (program.Command, program.Channel, (int)program.Data1, (int)program.Data2));

        Assert.True(MidiEvent.TryParse([0xF8], 0, 0, out MidiEvent clock));
        Assert.Equal(0xF8, clock.Command);

        Assert.False(MidiEvent.TryParse([0xF0, 0x7E, 0xF7], 0, 0, out _));   // Sysex
        Assert.False(MidiEvent.TryParse([0xFE], 0, 0, out _));               // Active sensing
        Assert.False(MidiEvent.TryParse([0x90, 60], 0, 0, out _));           // Cut short
        Assert.False(MidiEvent.TryParse([60, 100], 0, 0, out _));            // Running status
        Assert.False(MidiEvent.TryParse([], 0, 0, out _));
    }

    [Fact]
    public void A_block_sorts_by_offset_keeping_each_inputs_order()
    {
        var buffer = new MidiBuffer();

        buffer.Add(new MidiEvent(10, 0, 0x90, 60, 1));
        buffer.Add(new MidiEvent(10, 0, 0x80, 60, 0));
        buffer.Add(new MidiEvent(30, 0, 0x90, 62, 1));
        buffer.Add(new MidiEvent(0, 1, 0x90, 64, 1));
        buffer.Add(new MidiEvent(10, 1, 0x90, 65, 1));

        buffer.Sort();

        Assert.Equal(new[] { (0, 64), (10, 60), (10, 60), (10, 65), (30, 62) },
            buffer.Events.ToArray().Select(e => (e.Offset, (int)e.Data1)));
        Assert.Equal(new[] { 0x90, 0x80 }, buffer.Events.ToArray().Where(e => e.Data1 == 60).Select(e => (int)e.Status));
    }

    [Fact]
    public void A_full_block_drops_what_doesnt_fit()
    {
        var buffer = new MidiBuffer();

        for (int i = 0; i < MidiBuffer.Capacity + 10; i++)
            buffer.Add(new MidiEvent(0, 0, 0x90, 60, 1));

        Assert.Equal(MidiBuffer.Capacity, buffer.Count);
    }

    [Fact]
    public void The_ring_keeps_order_wraps_and_refuses_when_full()
    {
        var ring = new MidiRing(4);

        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < 4; i++)
                Assert.True(ring.Write(i, (round * 10) + i));

            Assert.False(ring.Write(99, 99));

            for (int i = 0; i < 4; i++)
            {
                Assert.True(ring.Peek(out long time, out int message));
                Assert.Equal((i, (round * 10) + i), (time, message));

                ring.Advance();
            }

            Assert.False(ring.Peek(out _, out _));
        }
    }

    [Fact]
    public void Messages_fall_where_they_arrived_in_the_previous_block()
    {
        double framesPerTick = 1;   // A tick per frame, for the arithmetic

        Assert.Equal(0, TimedMidiInputs.Offset(500, 0, 256, framesPerTick));      // Before the first block
        Assert.Equal(0, TimedMidiInputs.Offset(900, 1000, 256, framesPerTick));   // From before the previous block
        Assert.Equal(100, TimedMidiInputs.Offset(1100, 1000, 256, framesPerTick));
        Assert.Equal(255, TimedMidiInputs.Offset(1400, 1000, 256, framesPerTick)); // A late callback: the block's end
    }

    [Fact]
    public void A_configurations_midi_inputs_are_found_by_name()
    {
        var config = new AudioConfig { MidiInputs = ["keyboard", "Pads", "KEYBOARD", "Gone"] };

        var (present, missing) = config.MidiInputsIn(["Keyboard", "Pads", "Other"]);

        Assert.Equal(new[] { "Keyboard", "Pads" }, present);
        Assert.Equal(new[] { "Gone" }, missing);
        Assert.Empty(new AudioConfig().MidiInputsIn(["Keyboard"]).Present);
    }

    [Fact]
    public void Timed_inputs_fill_a_block_in_order_and_allocate_nothing()
    {
        var a = new TestInput();
        var b = new TestInput();
        using var midi = new TimedMidiInputs([a, b], ["A", "B"]);
        var buffer = new MidiBuffer();

        midi.Fill(buffer, 256, 48000);   // The first block: its start is the next one's reference
        Assert.Equal(0, buffer.Count);

        a.Ring.Write(Stopwatch.GetTimestamp(), 0x403C90);   // Note on 60, velocity 64
        b.Ring.Write(Stopwatch.GetTimestamp(), 0x003C80);
        a.Ring.Write(Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60, 0x003C80);   // Not yet arrived

        long before = GC.GetAllocatedBytesForCurrentThread();

        midi.Fill(buffer, 256, 48000);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(2, buffer.Count);
        Assert.Equal((0, 0x90, 60, 64), (buffer.Events[0].Port, (int)buffer.Events[0].Status, (int)buffer.Events[0].Data1, (int)buffer.Events[0].Data2));
        Assert.Equal((1, 0x80), (buffer.Events[1].Port, (int)buffer.Events[1].Status));
        Assert.True(buffer.Events[0].Offset <= buffer.Events[1].Offset);
        Assert.True(a.Ring.Peek(out _, out _));   // The one from the future waits
    }

    [Fact]
    public async Task A_midi_input_thats_not_there_is_reported_and_the_audio_runs()
    {
        using var engine = new AudioEngine(new AudioConfig { MidiInputs = ["No such keyboard"] });
        var client = new TestClient();

        await engine.Attach(client);

        Assert.True(await client.BlockRan.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(client.Format!.MidiInputs);
        Assert.Contains(engine.State.MidiErrors, e => e.Contains("No such keyboard"));

        await engine.Detach(client);
    }

    [Fact]
    public void Alsa_ports_are_named_by_their_own_name_unless_it_leaves_out_their_clients()
    {
        Assert.Equal("MPK mini 3 MIDI 1", AlsaSeqMidi.PortName("MPK mini 3", "MPK mini 3 MIDI 1"));
        Assert.Equal("VMPK Output: out", AlsaSeqMidi.PortName("VMPK Output", "out"));
        Assert.Equal("Keyboard", AlsaSeqMidi.PortName("Keyboard", ""));
        Assert.Equal("out", AlsaSeqMidi.PortName("", "out"));
    }

    private class TestInput : MidiInput
    {
        public override void Dispose() { }
    }
}
