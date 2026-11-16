using System.Runtime.InteropServices;
using PrismOS.Common;
using PrismOS.Interop;

namespace PrismOS.Widgets;

/// <summary>A system-audio meter listening on the default render endpoint's loopback.
/// Reads the mix that goes to the speakers from a background thread and keeps the last
/// smoothed 12-band spectrum in an array the widgets read each frame.
///
/// Life cycle: constructed once, started at engine start, stopped at shutdown (COM must
/// be released from the same thread that created it). No per-frame allocations on the
/// hot path.</summary>
public sealed class LoopbackMeter : IDisposable
{
    /// <summary>The COM class of the endpoint enumerator. This is <c>CLSID_MMDeviceEnumerator</c>
    /// (BCDE0395…); A95664D5… is its interface IID, <c>IID_IMMDeviceEnumerator</c>, and asking the
    /// class factory for that fails with REGDB_E_CLASSNOTREG on every machine — which is how the
    /// meter ended up silently dead while the code looked plausible.</summary>
    public static readonly Guid EnumeratorClsid = new("bcde0395-e52f-467c-8e3d-c4579291692e");

    private sealed class Run : IDisposable
    {
        public volatile bool Stop;
        public Thread? Thread;
        public readonly ManualResetEventSlim Started = new();
        public readonly ManualResetEventSlim Done = new();

        public void Dispose()
        {
            if (Thread is null) return;
            Stop = true;
            if (Thread.Join(3000)) Done.Set();
        }
    }

    private readonly Lock _gate = new();
    private readonly float[] _bands = new float[Spectrum.BandCount];
    private volatile bool _available;

    // Counters for the diagnostics page and for figuring out a silent endpoint: they say whether
    // the capture saw packets, samples and FFT windows, not just whether a device was found.
    private long _packets, _frames, _windows;

    public long Packets => Interlocked.Read(ref _packets);
    public long Frames => Interlocked.Read(ref _frames);
    public long Windows => Interlocked.Read(ref _windows);

    private Run? _run;

    public bool Available => _available;

    /// <summary>The most recent band magnitudes, 12 values 0..1, attack-fast/release-slow.
    /// Zero while the capture is starting or before it has heard anything.</summary>
    public void Read(float[] into)
    {
        lock (_gate)
            Array.Copy(_bands, into, into.Length);
    }

    /// <summary>True when recent audio is above silence threshold — used to dim bars or
    /// hide them when nothing is playing.</summary>
    public bool IsAudible
    {
        get
        {
            float peak = 0f;
            lock (_gate) { for (int i = 0; i < _bands.Length; i++) peak = Math.Max(peak, _bands[i]); }
            return peak > 0.01f;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_run is not null) return;
            var run = new Run();
            _run = run;
            run.Thread = new Thread(() => CaptureThread(run))
            {
                IsBackground = true,
                Name = "prism-audiometer",
            };
            run.Thread.Start();
        }
    }

    public void Stop()
    {
        Run? run;
        lock (_gate) { run = _run; _run = null; }
        run?.Dispose();
    }

    private void CaptureThread(Run run)
    {
        int hrCo = CoInitializeEx(0, 0x0); // COINIT_MULTITHREADED
        bool coInit = hrCo >= 0;

        try
        {
            var enumType = Type.GetTypeFromCLSID(EnumeratorClsid);
            if (enumType is null) { Log.Warn("Audio meter: CLSID mismatch"); return; }
            var enumerator = (AudioInterop.IMMDeviceEnumerator)Activator.CreateInstance(enumType)!;
            int hrEndpoint = enumerator.GetDefaultAudioEndpoint(AudioInterop.eRender, AudioInterop.eConsole, out AudioInterop.IMMDevice? device);
            if (hrEndpoint != 0 || device is null)
            {
                Log.Warn($"Audio meter: no default render endpoint ({hrEndpoint:X8})");
                return;
            }

            var activateGuid = AudioInterop.IIDAudioClient;
            int hrActivate = device.Activate(ref activateGuid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object? unknown);
            if (hrActivate != 0 || unknown is not AudioInterop.IAudioClient client)
            {
                Log.Warn($"Audio meter: endpoint Activate failed {hrActivate:X8} ({unknown?.GetType().Name ?? "no object"})");
                return;
            }

            var fmt = AudioInterop.WAVEFORMATEX.Float48kHzStereo();
            int hr = client.Initialize(AudioInterop.AudioClientShareModeShared,
                AudioInterop.StreamFlagsLoopback | AudioInterop.StreamFlagsAutoConvertPcm,
                200_000, 0, ref fmt, IntPtr.Zero); // 200 ms in 100-ns units
            if (hr != 0)
            {
                Log.Warn($"Audio meter: Initialize failed {hr:X8}");
                return;
            }

            var serviceGuid = AudioInterop.IIDCaptureClient;
            int hrService = client.GetService(ref serviceGuid, out unknown);
            if (hrService != 0 || unknown is not AudioInterop.IAudioCaptureClient capture)
            {
                Log.Warn($"Audio meter: no IAudioCaptureClient ({hrService:X8})");
                return;
            }
            int hrStart = client.Start();
            if (hrStart != 0)
            {
                Log.Warn($"Audio meter: Start failed {hrStart:X8}");
                return;
            }

            _available = true;
            run.Started.Set();
            try
            {
                CaptureLoop(run, capture, fmt);
            }
            finally
            {
                try { client.Stop(); } catch { }
                _available = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Audio meter capture thread failed: {ex.Message}");
        }
        finally
        {
            if (coInit) CoUninitialize();
            run.Done.Set();
        }
    }

    private void CaptureLoop(Run run, AudioInterop.IAudioCaptureClient capture, AudioInterop.WAVEFORMATEX fmt)
    {
        // Ring buffer of mono samples (Hann windowed on demand).
        int ringCapacity = Spectrum.FftSize * 4;
        var ring = new float[ringCapacity];
        int ringHead = 0, ringCount = 0;

        var td = new float[Spectrum.FftSize];
        var bands = new float[Spectrum.BandCount];
        const float sampleRate = 48000f;

        while (!run.Stop)
        {
            uint packetSize = 0;
            int hr = capture.GetNextPacketSize(out packetSize);
            if (hr == unchecked((int)0x88890004) /* AUDCLNT_E_DEVICE_INVALIDATED */) { Log.Warn("Audio meter: device invalidated"); break; }
            if (hr != 0 || packetSize == 0)
            {
                Decay(bands);
                Publish(bands);
                Thread.Sleep(10);
                continue;
            }
            Interlocked.Increment(ref _packets);

            while (packetSize > 0)
            {
                hr = capture.GetBuffer(out IntPtr data, out uint numFrames, out uint flags, out _, out _);
                if (hr == unchecked((int)0x88890004)) break;
                if (hr != 0) break;

                if (data != IntPtr.Zero && numFrames > 0 && (flags & AudioInterop.BufferFlagsSilent) == 0)
                {
                    Interlocked.Add(ref _frames, numFrames);
                    int channels = fmt.Channels;
                    int floatCount = (int)numFrames * channels;
                    unsafe
                    {
                        var frame = new ReadOnlySpan<float>((float*)data, floatCount);
                        for (int f = 0; f < frame.Length; f += channels)
                        {
                            float mono = 0f;
                            for (int c = 0; c < channels; c++) mono += frame[f + c];
                            mono /= channels;
                            ring[ringHead] = mono;
                            ringHead = (ringHead + 1) % ringCapacity;
                            if (ringCount < ringCapacity) ringCount++;
                        }
                    }
                }
                capture.ReleaseBuffer(numFrames);
                if (capture.GetNextPacketSize(out packetSize) != 0) break;
            }

            while (ringCount >= Spectrum.FftSize)
            {
                int readStart = (ringHead - ringCount + ringCapacity) % ringCapacity;
                for (int i = 0; i < Spectrum.FftSize; i++)
                    td[i] = ring[(readStart + i) % ringCapacity];
                ringCount -= Spectrum.FftSize / 2;
                Interlocked.Increment(ref _windows);
                Spectrum.Magnitudes(td, sampleRate, bands, sensitivity: 1.0f);
                Publish(bands);
            }
        }
    }

    private void Decay(float[] bands)
    {
        for (int i = 0; i < bands.Length; i++)
            bands[i] = Math.Max(0f, bands[i] - 0.012f);
    }

    private void Publish(float[] bands)
    {
        lock (_gate)
            Array.Copy(bands, _bands, bands.Length);
    }

    public void Dispose() => Stop();

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(int pvReserved, int coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
