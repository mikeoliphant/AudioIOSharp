namespace AudioIOSharp;

/// <summary>
/// Converts one channel between an ALSA mmap area (the device's own ring buffer, in its native format, interleaved or
/// not) and a float buffer, in one pass with no buffer in between. Floats are full scale at +-1; written samples are
/// clipped there. Allocates nothing.
/// </summary>
internal static unsafe class AlsaSamples
{
    /// <summary>The formats the driver converts, best first: what a device is opened with is the first it takes.</summary>
    public static readonly int[] Preference =
    {
        AlsaNative.FormatFloatLE,
        AlsaNative.FormatS32LE,
        AlsaNative.FormatS24_3LE,
        AlsaNative.FormatS24LE,
        AlsaNative.FormatS16LE,
    };

    public static bool IsSupported(int format) => Array.IndexOf(Preference, format) >= 0;

    public static string FormatName(int format) => format switch
    {
        AlsaNative.FormatFloatLE => "32-bit float",
        AlsaNative.FormatS32LE => "32-bit",
        AlsaNative.FormatS24_3LE => "24-bit packed",
        AlsaNative.FormatS24LE => "24-bit",
        AlsaNative.FormatS16LE => "16-bit",
        _ => $"format {format}",
    };

    private const float Scale16 = 1f / 32768f;
    private const float Scale24 = 1f / 8388608f;
    private const float Scale32 = 1f / 2147483648f;

    // The first sample of the area at the frame offset, and the bytes from one sample to the next.
    private static byte* Start(in AlsaNative.ChannelArea area, nuint offset, out nint stride)
    {
        stride = (nint)(area.Step / 8);

        return (byte*)area.Address + ((area.First + ((ulong)offset * area.Step)) / 8);
    }

    /// <summary>Reads <paramref name="frames"/> samples of the area's channel, from frame <paramref name="offset"/>, into
    /// <paramref name="destination"/>.</summary>
    public static void Read(int format, in AlsaNative.ChannelArea area, nuint offset, int frames, float* destination)
    {
        byte* p = Start(area, offset, out nint stride);

        switch (format)
        {
            case AlsaNative.FormatFloatLE:
                for (int i = 0; i < frames; i++, p += stride)
                    destination[i] = *(float*)p;
                break;

            case AlsaNative.FormatS32LE:
                for (int i = 0; i < frames; i++, p += stride)
                    destination[i] = *(int*)p * Scale32;
                break;

            case AlsaNative.FormatS24_3LE:
                for (int i = 0; i < frames; i++, p += stride)
                    destination[i] = (p[0] | (p[1] << 8) | ((sbyte)p[2] << 16)) * Scale24;
                break;

            case AlsaNative.FormatS24LE:
                for (int i = 0; i < frames; i++, p += stride)
                    destination[i] = ((*(int*)p << 8) >> 8) * Scale24;
                break;

            case AlsaNative.FormatS16LE:
                for (int i = 0; i < frames; i++, p += stride)
                    destination[i] = *(short*)p * Scale16;
                break;
        }
    }

    /// <summary>Writes <paramref name="frames"/> samples from <paramref name="source"/> to the area's channel, from
    /// frame <paramref name="offset"/>.</summary>
    public static void Write(int format, in AlsaNative.ChannelArea area, nuint offset, int frames, float* source)
    {
        byte* p = Start(area, offset, out nint stride);

        switch (format)
        {
            case AlsaNative.FormatFloatLE:
                for (int i = 0; i < frames; i++, p += stride)
                    *(float*)p = Math.Clamp(source[i], -1f, 1f);
                break;

            case AlsaNative.FormatS32LE:
                // Float to int conversions saturate, so full scale doesn't wrap
                for (int i = 0; i < frames; i++, p += stride)
                    *(int*)p = (int)(Math.Clamp(source[i], -1f, 1f) * 2147483648f);
                break;

            case AlsaNative.FormatS24_3LE:
                for (int i = 0; i < frames; i++, p += stride)
                {
                    int v = (int)(Math.Clamp(source[i], -1f, 1f) * 8388607f);

                    p[0] = (byte)v;
                    p[1] = (byte)(v >> 8);
                    p[2] = (byte)(v >> 16);
                }
                break;

            case AlsaNative.FormatS24LE:
                for (int i = 0; i < frames; i++, p += stride)
                    *(int*)p = (int)(Math.Clamp(source[i], -1f, 1f) * 8388607f);
                break;

            case AlsaNative.FormatS16LE:
                for (int i = 0; i < frames; i++, p += stride)
                    *(short*)p = (short)(Math.Clamp(source[i], -1f, 1f) * 32767f);
                break;
        }
    }

    /// <summary>Writes silence to the area's channel.</summary>
    public static void Silence(int format, in AlsaNative.ChannelArea area, nuint offset, int frames)
    {
        byte* p = Start(area, offset, out nint stride);
        int bytes = format switch
        {
            AlsaNative.FormatS16LE => 2,
            AlsaNative.FormatS24_3LE => 3,
            _ => 4,
        };

        for (int i = 0; i < frames; i++, p += stride)
            new Span<byte>(p, bytes).Clear();
    }
}
