using System.Runtime.InteropServices;
using PrismOS.Common;
using PrismOS.Interop;

namespace PrismOS.Widgets;

/// <summary>A live spectrum meter over WASAPI loopback capture. It listens on every active render
/// endpoint and publishes the bands of whichever endpoint is actually producing audio — on a
/// machine with speakers, a headset, an HDMI output and a virtual cable, the system default is
/// often a device nothing is playing through, and listening only there leaves the meter dead.
///
/// Life cycle: constructed once, started at engine start, stopped at shutdown (COM must be
/// released from the same thread that created it). No per-frame allocations on the hot path.</summary>
public sealed class LoopbackMeter : IDisposable
{
    /// <summary>The COM class of the endpoint enumerator. This is <c>CLSID_MMDeviceEnumerator</c>
    /// (BCDE0395…); A95664D5… is its interface IID, and asking the class factory for that fails
    /// with REGDB_E_CLASSNOTREG on every machine.</summary>
    public static readonly Guid EnumeratorClsid = new("bcde0395-e52f-467c-8e3d-c4579291692e");

    private const int DeviceInvalidated = unchecked((int)0x88890004);

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

    /// <summary>One endpoint's capture stream and its own FFT state.</summary>
    private sealed class Capture : IDisposable
    {
        public required string Id;
        public required AudioInterop.IAudioClient Client;
        public required AudioInterop.IAudioCaptureClient Source;
        public required AudioInterop.WAVEFORMATEX Format;
        public readonly float[] Bands = new float[Spectrum.BandCount];
        public bool Invalid;

        private readonly float[] _ring = new float[Spectrum.FftSize * 4];
        private readonly float[] _window = new float[Spectrum.FftSize];
        private int _head, _count;

        public bool Pump(LoopbackMeter owner)
        {
            uint packetSize;
            int hr = Source.GetNextPacketSize(out packetSize);
            if (hr == DeviceInvalidated) { Invalid = true; return false; }
            if (hr != 0 || packetSize == 0) return false;

            Interlocked.Increment(ref owner._packets);
            while (packetSize > 0)
            {
                hr = Source.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                if (hr == DeviceInvalidated) { Invalid = true; break; }
                if (hr != 0) break;

                if (data != IntPtr.Zero && frames > 0 && (flags & AudioInterop.BufferFlagsSilent) == 0)
                {
                    Interlocked.Add(ref owner._frames, frames);
                    int channels = Format.Channels > 0 ? Format.Channels : 1;
                    int floats = (int)frames * channels;
                    unsafe
                    {
                        var frame = new ReadOnlySpan<float>((float*)data, floats);
                        for (int f = 0; f < frame.Length; f += channels)
                        {
                            float mono = 0f;
                            for (int c = 0; c < channels; c++) mono += frame[f + c];
                            _ring[_head] = mono / channels;
                            _head = (_head + 1) % _ring.Length;
                            if (_count < _ring.Length) _count++;
                        }
                    }
                }
                Source.ReleaseBuffer(frames);
                if (Source.GetNextPacketSize(out packetSize) != 0) break;
            }

            float rate = Format.SamplesPerSec >= 8000 ? Format.SamplesPerSec : 48000f;
            while (_count >= Spectrum.FftSize)
            {
                int start = (_head - _count + _ring.Length) % _ring.Length;
                for (int i = 0; i < Spectrum.FftSize; i++)
                    _window[i] = _ring[(start + i) % _ring.Length];
                _count -= Spectrum.FftSize / 2;
                Interlocked.Increment(ref owner._windows);
                Spectrum.Magnitudes(_window, rate, Bands, sensitivity: 1.0f);
            }
            return true;
        }

        public void Dispose()
        {
            try { Client.Stop(); } catch { }
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

    /// <summary>True when recent audio crossed the meter's floor.</summary>
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

            var captures = new List<Capture>();
            if (enumerator.EnumAudioEndpoints(AudioInterop.eRender, AudioInterop.DEVICE_STATE_ACTIVE,
                                              out AudioInterop.IMMDeviceCollection? collection) == 0
                && collection is not null)
            {
                collection.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out AudioInterop.IMMDevice? device) != 0 || device is null) continue;
                    string id = "";
                    try { device.GetId(out id); } catch { }
                    var capture = TryOpen(device, id);
                    if (capture is not null) captures.Add(capture);
                }
            }

            if (captures.Count == 0)
            {
                // The collection can be empty on a shell that only answers the default-endpoint
                // query; try that as a last resort.
                if (enumerator.GetDefaultAudioEndpoint(AudioInterop.eRender, AudioInterop.eConsole,
                        out AudioInterop.IMMDevice? device) == 0 && device is not null)
                {
                    string id = "";
                    try { device.GetId(out id); } catch { }
                    var capture = TryOpen(device, id);
                    if (capture is not null) captures.Add(capture);
                }
            }

            if (captures.Count == 0)
            {
                Log.Warn("Audio meter: no usable render endpoint for loopback capture");
                return;
            }

            _available = true;
            run.Started.Set();
            Log.Info($"Audio meter: listening on {captures.Count} endpoint(s): {string.Join(", ", captures.Select(c => c.Id))}");
            try { PumpLoop(run, captures); }
            finally
            {
                foreach (var capture in captures) capture.Dispose();
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

    private static Capture? TryOpen(AudioInterop.IMMDevice device, string id)
    {
        var iidClient = AudioInterop.IIDAudioClient;
        if (device.Activate(ref iidClient, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object? unknown) != 0
            || unknown is not AudioInterop.IAudioClient client)
        {
            Log.Warn($"Audio meter: {id} will not activate an IAudioClient");
            return null;
        }

        var format = MixFormat(client);
        int hr = client.Initialize(AudioInterop.AudioClientShareModeShared,
            AudioInterop.StreamFlagsLoopback | AudioInterop.StreamFlagsAutoConvertPcm,
            200_000, 0, ref format, IntPtr.Zero); // 200 ms in 100-ns units
        if (hr != 0)
        {
            // WAVEFORMATEXTENSIBLE carries an extension the flat struct cannot express. Fall back
            // to plain 48 kHz float, which every driver accepts with AutoConvertPcm.
            format = AudioInterop.WAVEFORMATEX.Float48kHzStereo();
            hr = client.Initialize(AudioInterop.AudioClientShareModeShared,
                AudioInterop.StreamFlagsLoopback | AudioInterop.StreamFlagsAutoConvertPcm,
                200_000, 0, ref format, IntPtr.Zero);
        }
        if (hr != 0)
        {
            Log.Warn($"Audio meter: {id} Initialize failed {hr:X8}");
            return null;
        }

        var iidCapture = AudioInterop.IIDCaptureClient;
        if (client.GetService(ref iidCapture, out unknown) != 0
            || unknown is not AudioInterop.IAudioCaptureClient source)
        {
            Log.Warn($"Audio meter: {id} has no IAudioCaptureClient");
            return null;
        }
        if (client.Start() != 0)
        {
            Log.Warn($"Audio meter: {id} would not start");
            return null;
        }

        return new Capture { Id = id, Client = client, Source = source, Format = format };
    }

    private static AudioInterop.WAVEFORMATEX MixFormat(AudioInterop.IAudioClient client)
    {
        try
        {
            if (client.GetMixFormat(out IntPtr ptr) == 0 && ptr != IntPtr.Zero)
            {
                var format = Marshal.PtrToStructure<AudioInterop.WAVEFORMATEX>(ptr);
                Marshal.FreeCoTaskMem(ptr);
                if (format.SamplesPerSec >= 8000 && format.Channels > 0) return format;
            }
        }
        catch { /* fall through to the universal format */ }
        return AudioInterop.WAVEFORMATEX.Float48kHzStereo();
    }

    private void PumpLoop(Run run, List<Capture> captures)
    {
        var published = new float[Spectrum.BandCount];
        bool announced = false;

        while (!run.Stop)
        {
            bool anyPacket = false;
            Capture? loudest = null;
            float peak = 0f;
            foreach (var capture in captures)
            {
                if (capture.Invalid) continue;
                bool got;
                try { got = capture.Pump(this); }
                catch { capture.Invalid = true; continue; }
                anyPacket |= got;
                float local = 0f;
                for (int i = 0; i < capture.Bands.Length; i++) local = Math.Max(local, capture.Bands[i]);
                if (local > peak) { peak = local; loudest = capture; }
            }

            if (loudest is not null && peak > 0.01f)
            {
                Array.Copy(loudest.Bands, published, published.Length);
                if (!announced)
                {
                    announced = true;
                    Log.Info($"Audio meter: signal on {loudest.Id}");
                }
            }
            else
            {
                Decay(published);
            }
            Publish(published);
            if (!anyPacket) Thread.Sleep(10);
        }
    }

    private static void Decay(float[] bands)
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
