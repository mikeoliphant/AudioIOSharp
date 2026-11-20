namespace AudioIOSharp;

/// <summary>What kind of problem an <see cref="AudioException"/> reports.</summary>
public enum AudioErrorKind
{
    /// <summary>A configuration asks for something the device doesn't offer: a channel, a rate, a buffer size.</summary>
    InvalidValue,
    /// <summary>The device or driver asked for doesn't exist.</summary>
    NotFound,
    /// <summary>Something else holds the driver: a device of it is already open (ASIO allows one at a time).</summary>
    Busy,
    /// <summary>The device can't be used now: not installed, not connected, no server running, or it failed to open.</summary>
    Unavailable,
    /// <summary>The driver can't do what was asked (a control panel, for one).</summary>
    Unsupported,
}

/// <summary>
/// A problem with an audio driver or device. The message is written for the user: it says what went wrong, and where
/// possible what to do about it.
/// </summary>
public class AudioException : Exception
{
    public AudioException(AudioErrorKind kind, string message, object? details = null)
        : base(message)
    {
        Kind = kind;
        Details = details;
    }

    public AudioErrorKind Kind { get; }

    /// <summary>For <see cref="AudioErrorKind.InvalidValue"/>, what the device does offer: its channel names
    /// (<c>IReadOnlyList&lt;string&gt;</c>), rates or buffer sizes (<c>IReadOnlyList&lt;int&gt;</c>). Null otherwise.</summary>
    public object? Details { get; }
}
