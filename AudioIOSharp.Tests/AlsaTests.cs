using System.Runtime.InteropServices;

namespace AudioIOSharp.Tests;

/// <summary>
/// The ALSA driver's parts that don't need a device: converting samples between an mmap area (interleaved, in each
/// format it takes) and floats, and its device names. Its devices are tested by hand on Linux.
/// </summary>
public unsafe class AlsaTests
{
    static readonly int[] Formats = AlsaSamples.Preference;

    static int Bytes(int format) => format switch
    {
        AlsaNative.FormatS16LE => 2,
        AlsaNative.FormatS24_3LE => 3,
        _ => 4,
    };

    // An interleaved area for one channel of a buffer of `channels` channels
    static AlsaNative.ChannelArea Area(byte* buffer, int format, int channel, int channels) => new()
    {
        Address = (nint)buffer,
        First = (uint)(channel * Bytes(format) * 8),
        Step = (uint)(channels * Bytes(format) * 8),
    };

    [Theory]
    [InlineData(AlsaNative.FormatFloatLE, 1e-7)]
    [InlineData(AlsaNative.FormatS32LE, 1e-7)]
    [InlineData(AlsaNative.FormatS24_3LE, 2.5e-7)]
    [InlineData(AlsaNative.FormatS24LE, 2.5e-7)]
    [InlineData(AlsaNative.FormatS16LE, 6.2e-5)]
    public void Samples_round_trip_through_an_interleaved_area(int format, double tolerance)
    {
        const int channels = 3, frames = 64, offset = 5;

        float[] source = Enumerable.Range(0, frames).Select(i => MathF.Sin(i * 0.3f) * 0.9f).ToArray();
        float[] back = new float[frames];
        byte[] ring = new byte[(frames + offset) * channels * Bytes(format)];

        fixed (byte* buffer = ring)
        fixed (float* s = source, b = back)
        {
            // The middle channel, from a frame offset in, leaving its neighbours alone
            AlsaNative.ChannelArea area = Area(buffer, format, 1, channels);

            AlsaSamples.Write(format, area, offset, frames, s);
            AlsaSamples.Read(format, area, offset, frames, b);

            AlsaNative.ChannelArea first = Area(buffer, format, 0, channels);
            AlsaNative.ChannelArea last = Area(buffer, format, 2, channels);
            float[] other = new float[frames];

            fixed (float* o = other)
            {
                AlsaSamples.Read(format, first, offset, frames, o);
                Assert.All(other, v => Assert.Equal(0f, v));

                AlsaSamples.Read(format, last, offset, frames, o);
                Assert.All(other, v => Assert.Equal(0f, v));
            }
        }

        for (int i = 0; i < frames; i++)
            Assert.True(Math.Abs(source[i] - back[i]) <= tolerance, $"frame {i}: {source[i]} came back as {back[i]}");
    }

    [Fact]
    public void Integer_formats_read_at_full_scale()
    {
        float Read(int format, byte[] sample)
        {
            float value;

            fixed (byte* p = sample)
                AlsaSamples.Read(format, Area(p, format, 0, 1), 0, 1, &value);

            return value;
        }

        Assert.Equal(0.5f, Read(AlsaNative.FormatS16LE, BitConverter.GetBytes((short)0x4000)));
        Assert.Equal(-1f, Read(AlsaNative.FormatS16LE, BitConverter.GetBytes(short.MinValue)));
        Assert.Equal(-1f, Read(AlsaNative.FormatS32LE, BitConverter.GetBytes(int.MinValue)));
        Assert.Equal(0.5f, Read(AlsaNative.FormatS24_3LE, new byte[] { 0x00, 0x00, 0x40 }));
        Assert.Equal(-1f, Read(AlsaNative.FormatS24_3LE, new byte[] { 0x00, 0x00, 0x80 }));

        // 24 bits in 32: the top byte is ignored, the 24 bits sign-extended
        Assert.Equal(-1f, Read(AlsaNative.FormatS24LE, BitConverter.GetBytes(0x00800000)));
        Assert.Equal(0.5f, Read(AlsaNative.FormatS24LE, BitConverter.GetBytes(0x7F400000)));
    }

    [Theory]
    [InlineData(AlsaNative.FormatS16LE)]
    [InlineData(AlsaNative.FormatS24_3LE)]
    [InlineData(AlsaNative.FormatS24LE)]
    [InlineData(AlsaNative.FormatS32LE)]
    [InlineData(AlsaNative.FormatFloatLE)]
    public void Writes_clip_at_full_scale(int format)
    {
        float[] loud = { 2f, -2f, 1f, -1f };
        float[] back = new float[loud.Length];
        byte[] ring = new byte[loud.Length * Bytes(format)];

        fixed (byte* buffer = ring)
        fixed (float* s = loud, b = back)
        {
            AlsaNative.ChannelArea area = Area(buffer, format, 0, 1);

            AlsaSamples.Write(format, area, 0, loud.Length, s);
            AlsaSamples.Read(format, area, 0, loud.Length, b);
        }

        // Positive full scale is one step short of 1 in the integer formats
        Assert.All(new[] { back[0], back[2] }, v => Assert.InRange(v, 0.9999f, 1f));
        Assert.All(new[] { back[1], back[3] }, v => Assert.InRange(v, -1f, -0.9999f));
    }

    [Fact]
    public void Silence_clears_only_its_channel()
    {
        int format = AlsaNative.FormatS24_3LE;
        byte[] ring = Enumerable.Repeat((byte)0x55, 4 * 2 * 3).ToArray();

        fixed (byte* buffer = ring)
            AlsaSamples.Silence(format, Area(buffer, format, 1, 2), 0, 4);

        for (int frame = 0; frame < 4; frame++)
        {
            Assert.Equal(new byte[] { 0x55, 0x55, 0x55 }, ring.Skip(frame * 6).Take(3));
            Assert.Equal(new byte[] { 0, 0, 0 }, ring.Skip((frame * 6) + 3).Take(3));
        }
    }

    [Fact]
    public void Converting_allocates_nothing()
    {
        const int frames = 256;

        byte[] ring = GC.AllocateArray<byte>(frames * 2 * 4, pinned: true);
        float[] samples = GC.AllocateArray<float>(frames, pinned: true);

        fixed (byte* buffer = ring)
        fixed (float* s = samples)
        {
            foreach (int format in Formats)
            {
                AlsaNative.ChannelArea area = Area(buffer, format, 1, 2);

                AlsaSamples.Write(format, area, 0, frames, s);
                AlsaSamples.Read(format, area, 0, frames, s);

                long before = GC.GetAllocatedBytesForCurrentThread();

                AlsaSamples.Write(format, area, 0, frames, s);
                AlsaSamples.Read(format, area, 0, frames, s);
                AlsaSamples.Silence(format, area, 0, frames);

                Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }
    }

    [Fact]
    public void Device_names_show_the_card_and_open_the_hardware_name()
    {
        string listed = AlsaDriver.DisplayName("HDA Intel PCH, ALC892 Analog\nDirect hardware device without any conversions",
            "hw:CARD=PCH,DEV=0");

        Assert.Equal("HDA Intel PCH, ALC892 Analog (hw:CARD=PCH,DEV=0)", listed);
        Assert.Equal("hw:CARD=PCH,DEV=0", AlsaDriver.AlsaName(listed));

        // Without a description, and other ALSA names, as they are
        Assert.Equal("hw:CARD=USB,DEV=1", AlsaDriver.DisplayName(null, "hw:CARD=USB,DEV=1"));
        Assert.Equal("hw:1,0", AlsaDriver.AlsaName("hw:1,0"));
        Assert.Equal("null", AlsaDriver.AlsaName("null"));
        Assert.Equal("Mixer (Front) output", AlsaDriver.AlsaName("Mixer (Front) output"));
    }
}
