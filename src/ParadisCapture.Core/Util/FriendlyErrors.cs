namespace ParadisCapture.Core.Util;

/// <summary>
/// Turns low-level failures (mostly COM HRESULTs from Media Foundation, Direct3D and WASAPI)
/// into one sentence a normal user can act on. The technical details go to the log.
/// </summary>
public static class FriendlyErrors
{
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    public const int ERROR_HANDLE_DISK_FULL = unchecked((int)0x80070027);
    public const int ERROR_DISK_FULL = unchecked((int)0x80070070);
    public const int ERROR_SHARING_VIOLATION = unchecked((int)0x80070020);
    public const int ERROR_PATH_NOT_FOUND = unchecked((int)0x80070003);
    public const int ERROR_FILE_NOT_FOUND = unchecked((int)0x80070002);
    public const int ERROR_NOT_READY = unchecked((int)0x80070015);
    public const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
    public const int DXGI_ERROR_DEVICE_RESET = unchecked((int)0x887A0007);
    public const int DXGI_ERROR_DEVICE_HUNG = unchecked((int)0x887A0006);
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);
    public const int AUDCLNT_E_SERVICE_NOT_RUNNING = unchecked((int)0x88890010);
    public const int E_NOTFOUND = unchecked((int)0x80070490);
    public const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36B4);
    public const int MF_E_TOPO_CODEC_NOT_FOUND = unchecked((int)0xC00D5212);
    public const int MF_E_UNSUPPORTED_D3D_TYPE = unchecked((int)0xC00D6D76);
    public const int MF_E_HW_MFT_FAILED_START_STREAMING = unchecked((int)0xC00D3704);

    public static bool IsDiskFull(int hr) => hr is ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL;

    public static bool IsDeviceLost(int hr) =>
        hr is DXGI_ERROR_DEVICE_REMOVED or DXGI_ERROR_DEVICE_RESET or DXGI_ERROR_DEVICE_HUNG;

    public static bool IsAudioDeviceGone(int hr) => hr is AUDCLNT_E_DEVICE_INVALIDATED or E_NOTFOUND;

    /// <summary>Message for a failure while writing the output file.</summary>
    public static string ForWriteFailure(int hr) => hr switch
    {
        ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL =>
            "The disk is full, so recording stopped. Everything recorded up to that point was kept.",
        E_ACCESSDENIED or ERROR_SHARING_VIOLATION =>
            "Paradis Capture isn't allowed to write to the save folder. Choose a different folder in Settings.",
        ERROR_PATH_NOT_FOUND or ERROR_NOT_READY =>
            "The save folder is no longer available (was a drive disconnected?). Choose a different folder in Settings.",
        _ when IsDeviceLost(hr) =>
            "The graphics driver stopped responding, so recording stopped. Everything recorded up to that point was kept.",
        E_OUTOFMEMORY => "Windows ran out of memory, so recording stopped.",
        _ => $"Recording stopped because the video file couldn't be written (error 0x{hr:X8}).",
    };

    /// <summary>Message for a failure while starting a recording.</summary>
    public static string ForStartFailure(int hr) => hr switch
    {
        E_ACCESSDENIED or ERROR_SHARING_VIOLATION =>
            "Paradis Capture isn't allowed to write to the save folder. Choose a different folder in Settings.",
        ERROR_PATH_NOT_FOUND or ERROR_NOT_READY => "The save folder isn't available. Choose a different folder in Settings.",
        ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL => "The save folder's disk is full.",
        MF_E_TOPO_CODEC_NOT_FOUND or MF_E_INVALIDMEDIATYPE or MF_E_UNSUPPORTED_D3D_TYPE or MF_E_HW_MFT_FAILED_START_STREAMING =>
            "Windows couldn't start the H.264 video encoder. Make sure your graphics driver is up to date. " +
            "(On Windows N editions, install the Media Feature Pack.)",
        _ when IsDeviceLost(hr) => "The graphics driver isn't responding. Try again in a moment.",
        AUDCLNT_E_DEVICE_IN_USE => "An audio device is being used exclusively by another app.",
        AUDCLNT_E_SERVICE_NOT_RUNNING => "The Windows Audio service isn't running.",
        _ => $"The recording couldn't be started (error 0x{hr:X8}). Details are in the log file.",
    };
}
