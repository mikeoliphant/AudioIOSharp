namespace AudioIOSharp;

/// <summary>
/// How the ALSA driver and the ALSA sequencer name what they list: plain string work, with nothing of ALSA's own, so it
/// runs (and is tested) on every platform.
/// </summary>
internal static class AlsaNames
{
    /// <summary>A device's name as the driver lists it: the card and device description, then the ALSA name it opens
    /// (by card id, which stays the same across reboots, unlike the card's number).</summary>
    public static string DisplayName(string? description, string alsaName)
    {
        string first = description?.Split('\n')[0].Trim() ?? "";

        return first.Length > 0 ? $"{first} ({alsaName})" : alsaName;
    }

    /// <summary>The ALSA name a device opens: the hardware name at the end of a listed name, or the name itself (any
    /// ALSA PCM name, for devices that aren't listed).</summary>
    public static string AlsaName(string device)
    {
        int open = device.LastIndexOf(" (hw:", StringComparison.Ordinal);

        if ((open >= 0) && device.EndsWith(')'))
            return device.Substring(open + 2, device.Length - open - 3);

        return device;
    }

    /// <summary>A sequencer port's name as a MIDI input: its own name when that starts with its client's (a USB
    /// keyboard's "MPK mini 3 MIDI 1"), else both ("VMPK Output: out").</summary>
    public static string PortName(string client, string port)
    {
        if (port.Length == 0)
            return (client.Length > 0) ? client : "MIDI input";

        if ((client.Length == 0) || port.StartsWith(client, StringComparison.OrdinalIgnoreCase))
            return port;

        return $"{client}: {port}";
    }
}
