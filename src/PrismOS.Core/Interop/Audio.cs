using System.Runtime.InteropServices;

namespace PrismOS.Interop;

/// <summary>WASAPI capture interfaces for a system-audio loopback. The same approach
/// Windhawk's taskbar media player and the Windows "sound meter" use: the default
/// render endpoint's loopback captures everything the user hears, including their
/// music, in real time.
///
/// Raw COM interop rather than a package: the init is one endpoint activation and
/// a handful of calls, and the codebase keeps its interop in this style.</summary>
internal static class AudioInterop
{
    // Audio device roles / flows
    internal const int eRender = 0;
    internal const int eConsole = 0;
    internal const int DEVICE_STATE_ACTIVE = 0x1;

    // Audio share mode
    internal const int AudioClientShareModeShared = 0;

    // Stream flags
    internal const uint StreamFlagsLoopback = 0x00020000;
    internal const uint StreamFlagsAutoConvertPcm = 0x80000000;
    internal const uint StreamFlagsEventCallback = 0x00040000;

    // Buffer flags
    internal const uint BufferFlagsSilent = 0x00000002;

    // WAVEFORMATEX.FormatTag for IEEE float
    internal const ushort WaveFormatIeeeFloat = 3;

    // These are the published WASAPI interface ids. They must match the interfaces the engine
    // hands out: a wrong GUID here makes QueryInterface fail with E_NOINTERFACE a few calls into
    // an otherwise correct capture, and the meter looks dead with nothing but a log line.
    internal static readonly Guid IIDAudioClient = new("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
    internal static readonly Guid IIDCaptureClient = new("c8adbd64-e71e-48a0-a4de-185c395cd317");

    [ComImport, Guid("bcde0395-e52f-467c-8e3d-c4579291692e")]
    internal class MMDeviceEnumerator { }

    [ComImport, Guid("a95664d2-9614-4f35-a746-de8db63617e6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("0bd7a1be-7a1a-44db-8397-cc5392387b5e"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("d666063f-1587-4e43-81f1-b948e807363f"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out object ppdb);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out int pdwState);
    }

    [ComImport, Guid("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration,
            long hnsPeriodicity, ref WAVEFORMATEX pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, ref WAVEFORMATEX pFormat, IntPtr phkFormatUsed);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("c8adbd64-e71e-48a0-a4de-185c395cd317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        // The order is the vtable order from audioclient.idl: GetBuffer, ReleaseBuffer,
        // GetNextPacketSize. A wrong order here turns GetNextPacketSize into ReleaseBuffer and
        // returns AUDCLNT_E_OUT_OF_ORDER forever, so no packet is ever read.
        [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags,
            out ulong pu64DevicePosition, out ulong pu64QPCPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WAVEFORMATEX
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;

        public static WAVEFORMATEX Float48kHzStereo() => new()
        {
            FormatTag = WaveFormatIeeeFloat,
            Channels = 2,
            SamplesPerSec = 48000,
            AvgBytesPerSec = 48000 * 2 * 4,
            BlockAlign = 2 * 4,
            BitsPerSample = 32,
            Size = 0,
        };
    }
}
