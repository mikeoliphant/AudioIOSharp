using System.Runtime.InteropServices;

namespace AudioIOSharp;

/// <summary>
/// The parts of libasound (ALSA's library) the ALSA driver uses. Every call the audio thread makes takes only
/// blittable values and pointers, so none of them allocates. Errors are negative errno values.
/// </summary>
internal static unsafe partial class AlsaNative
{
    public const string Library = "libasound.so.2";

    public const int StreamPlayback = 0;
    public const int StreamCapture = 1;
    public const int OpenNonBlock = 1;

    public const int AccessMmapInterleaved = 0;
    public const int AccessMmapNonInterleaved = 1;

    public const int FormatS16LE = 2;
    public const int FormatS24LE = 6;     // 24 bits in the low three bytes of 32
    public const int FormatS32LE = 10;
    public const int FormatFloatLE = 14;
    public const int FormatS24_3LE = 32;  // 24 bits in three bytes

    public const int EAGAIN = 11;
    public const int EBUSY = 16;
    public const int ENODEV = 19;
    public const int ENOENT = 2;
    public const int EPIPE = 32;
    public const int EBADFD = 77;
    public const int ESTRPIPE = 86;

    /// <summary>Where a channel's samples are in an mmap area: sample i is at addr + (first + i * step) bits.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ChannelArea
    {
        public nint Address;
        public uint First;
        public uint Step;
    }

    /// <summary>Whether libasound can be loaded.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsLinux())
                return false;
            if (!NativeLibrary.TryLoad(Library, out nint handle))
                return false;
            NativeLibrary.Free(handle);
            return true;
        }
    }

    // ---- Device names -----------------------------------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "snd_device_name_hint", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int DeviceNameHint(int card, string iface, out nint hints);

    [LibraryImport(Library, EntryPoint = "snd_device_name_get_hint", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint DeviceNameGetHint(nint hint, string id);

    [LibraryImport(Library, EntryPoint = "snd_device_name_free_hint")]
    public static partial int DeviceNameFreeHint(nint hints);

    [LibraryImport("libc", EntryPoint = "free")]
    public static partial void Free(nint pointer);

    [LibraryImport(Library, EntryPoint = "snd_strerror")]
    public static partial nint StrError(int error);

    public static string Error(int error) => Marshal.PtrToStringUTF8(StrError(error)) ?? $"error {error}";

    // ---- PCM --------------------------------------------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "snd_pcm_open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int PcmOpen(out nint pcm, string name, int stream, int mode);

    [LibraryImport(Library, EntryPoint = "snd_pcm_close")]
    public static partial int PcmClose(nint pcm);

    [LibraryImport(Library, EntryPoint = "snd_pcm_link")]
    public static partial int PcmLink(nint pcm1, nint pcm2);

    [LibraryImport(Library, EntryPoint = "snd_pcm_unlink")]
    public static partial int PcmUnlink(nint pcm);

    [LibraryImport(Library, EntryPoint = "snd_pcm_prepare")]
    public static partial int PcmPrepare(nint pcm);

    [LibraryImport(Library, EntryPoint = "snd_pcm_start")]
    public static partial int PcmStart(nint pcm);

    [LibraryImport(Library, EntryPoint = "snd_pcm_drop")]
    public static partial int PcmDrop(nint pcm);

    [LibraryImport(Library, EntryPoint = "snd_pcm_resume")]
    public static partial int PcmResume(nint pcm);

    [LibraryImport(Library, EntryPoint = "snd_pcm_wait")]
    public static partial int PcmWait(nint pcm, int timeoutMs);

    [LibraryImport(Library, EntryPoint = "snd_pcm_avail_update")]
    public static partial nint PcmAvailUpdate(nint pcm);

    [LibraryImport(Library, EntryPoint = "snd_pcm_mmap_begin")]
    public static partial int PcmMmapBegin(nint pcm, out ChannelArea* areas, out nuint offset, ref nuint frames);

    [LibraryImport(Library, EntryPoint = "snd_pcm_mmap_commit")]
    public static partial nint PcmMmapCommit(nint pcm, nuint offset, nuint frames);

    // ---- Hardware parameters ------------------------------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_malloc")]
    public static partial int HwParamsMalloc(out nint parameters);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_free")]
    public static partial void HwParamsFree(nint parameters);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_any")]
    public static partial int HwParamsAny(nint pcm, nint parameters);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params")]
    public static partial int HwParams(nint pcm, nint parameters);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_set_rate_resample")]
    public static partial int HwParamsSetRateResample(nint pcm, nint parameters, uint enable);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_set_access")]
    public static partial int HwParamsSetAccess(nint pcm, nint parameters, int access);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_test_format")]
    public static partial int HwParamsTestFormat(nint pcm, nint parameters, int format);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_set_format")]
    public static partial int HwParamsSetFormat(nint pcm, nint parameters, int format);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_get_channels_max")]
    public static partial int HwParamsGetChannelsMax(nint parameters, out uint channels);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_set_channels_min")]
    public static partial int HwParamsSetChannelsMin(nint pcm, nint parameters, ref uint channels);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_set_channels_first")]
    public static partial int HwParamsSetChannelsFirst(nint pcm, nint parameters, out uint channels);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_test_rate")]
    public static partial int HwParamsTestRate(nint pcm, nint parameters, uint rate, int dir);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_set_rate")]
    public static partial int HwParamsSetRate(nint pcm, nint parameters, uint rate, int dir);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_test_period_size")]
    public static partial int HwParamsTestPeriodSize(nint pcm, nint parameters, nuint frames, int dir);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_set_period_size")]
    public static partial int HwParamsSetPeriodSize(nint pcm, nint parameters, nuint frames, int dir);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_set_periods_near")]
    public static partial int HwParamsSetPeriodsNear(nint pcm, nint parameters, ref uint periods, ref int dir);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_get_buffer_size")]
    public static partial int HwParamsGetBufferSize(nint parameters, out nuint frames);

    [LibraryImport(Library, EntryPoint = "snd_pcm_hw_params_get_rate")]
    public static partial int HwParamsGetRate(nint parameters, out uint rate, out int dir);

    // ---- Software parameters ------------------------------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "snd_pcm_sw_params_malloc")]
    public static partial int SwParamsMalloc(out nint parameters);

    [LibraryImport(Library, EntryPoint = "snd_pcm_sw_params_free")]
    public static partial void SwParamsFree(nint parameters);

    [LibraryImport(Library, EntryPoint = "snd_pcm_sw_params_current")]
    public static partial int SwParamsCurrent(nint pcm, nint parameters);

    [LibraryImport(Library, EntryPoint = "snd_pcm_sw_params")]
    public static partial int SwParams(nint pcm, nint parameters);

    [LibraryImport(Library, EntryPoint = "snd_pcm_sw_params_get_boundary")]
    public static partial int SwParamsGetBoundary(nint parameters, out nuint boundary);

    [LibraryImport(Library, EntryPoint = "snd_pcm_sw_params_set_start_threshold")]
    public static partial int SwParamsSetStartThreshold(nint pcm, nint parameters, nuint frames);

    [LibraryImport(Library, EntryPoint = "snd_pcm_sw_params_set_avail_min")]
    public static partial int SwParamsSetAvailMin(nint pcm, nint parameters, nuint frames);

    // ---- Real-time scheduling (libc) -------------------------------------------------------------------------------------

    public const int SchedFifo = 1;

    [LibraryImport("libc", EntryPoint = "pthread_self")]
    public static partial nint PthreadSelf();

    [LibraryImport("libc", EntryPoint = "pthread_setschedparam")]
    public static partial int PthreadSetSchedParam(nint thread, int policy, in int priority);
}
