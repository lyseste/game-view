using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;
using VCDV.Devices;

namespace VCDV.Audio;

public class AudioPassthrough : IDisposable
{
    // Slider 0–100% maps to a VolumeSampleProvider gain of 0..VolumeGain.
    // 2.0 = 100% slider is twice unity (≈ +6 dB). Raise further for more
    // headroom, lower if clipping becomes audible on loud sources.
    private const float VolumeGain = 2.0f;

    private WasapiCapture?         _capture;
    private WasapiOut?             _output;
    private BufferedWaveProvider?  _buffer;
    private VolumeSampleProvider?  _volumeProvider;

    public bool IsRunning { get; private set; }

    // ── Start ─────────────────────────────────────────────────────────────────

    public void Start(
        string   videoDeviceName,
        int      inputIndex,
        int      outputIndex,
        double   volume,
        IList<AudioDeviceInfo> inputs,
        IList<AudioDeviceInfo> outputs)
    {
        Stop();

        using var enumerator = new MMDeviceEnumerator();

        var inputDevice  = ResolveDevice(enumerator, DataFlow.Capture, inputIndex,
            DeviceEnumerator.FindAudioInputForVideo(videoDeviceName, inputs), inputs);
        var outputDevice = ResolveDevice(enumerator, DataFlow.Render, outputIndex, null, outputs);

        if (inputDevice is null || outputDevice is null)
        {
            Log.Warning("audio passthrough skipped: could not resolve input or output device");
            return;
        }

        try
        {
            _capture = new WasapiCapture(inputDevice);
            var captureFormat = _capture.WaveFormat;

            // Query output mix format without touching Initialize — safe to call before Init
            var outputMixFormat = outputDevice.AudioClient.MixFormat;

            _buffer = new BufferedWaveProvider(captureFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration          = TimeSpan.FromMilliseconds(500),
            };

            ISampleProvider chain = _buffer.ToSampleProvider();

            _volumeProvider = new VolumeSampleProvider(chain)
                { Volume = (float)Math.Clamp(volume, 0, 1) * VolumeGain };
            chain = _volumeProvider;

            // Sample-rate conversion using WDL resampler (zero look-ahead latency)
            if (captureFormat.SampleRate != outputMixFormat.SampleRate)
            {
                Log.Information("audio resampling: {InRate}Hz → {OutRate}Hz",
                    captureFormat.SampleRate, outputMixFormat.SampleRate);
                chain = new WdlResamplingSampleProvider(chain, outputMixFormat.SampleRate);
            }

            // Channel-count conversion
            if (captureFormat.Channels == 1 && outputMixFormat.Channels == 2)
                chain = new MonoToStereoSampleProvider(chain);
            else if (captureFormat.Channels == 2 && outputMixFormat.Channels == 1)
                chain = new StereoToMonoSampleProvider(chain);

            _output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, 20);
            _output.Init(chain);

            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
            _output.Play();

            IsRunning = true;

            Log.Information(
                "audio passthrough started: input='{In}' output='{Out}' {Rate}Hz/{Ch}ch vol={Vol:P0}",
                inputDevice.FriendlyName, outputDevice.FriendlyName,
                captureFormat.SampleRate, captureFormat.Channels, volume);
        }
        catch (Exception ex)
        {
            Log.Warning("audio passthrough failed: {Error}", ex.Message);
            Stop();
        }
    }

    // ── Volume ────────────────────────────────────────────────────────────────

    public void SetVolume(double volume)
    {
        if (_volumeProvider is not null)
            _volumeProvider.Volume = (float)Math.Clamp(volume, 0, 1) * VolumeGain;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_buffer is null) return;
        // Drain stale audio so latency stays bounded to ~80ms
        if (_buffer.BufferedDuration.TotalMilliseconds > 80)
            _buffer.ClearBuffer();
        _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private static MMDevice? ResolveDevice(
        MMDeviceEnumerator     enumerator,
        DataFlow               flow,
        int                    savedIndex,
        int?                   autoDetectedIndex,
        IList<AudioDeviceInfo> devices)
    {
        try
        {
            int? resolvedIndex = savedIndex >= 0 ? savedIndex : autoDetectedIndex;

            if (resolvedIndex is int idx)
            {
                var info = devices.FirstOrDefault(d => d.Index == idx);
                if (info is not null)
                    return enumerator.GetDevice(info.Id);

                Log.Warning("saved audio device index {Index} not found; using default", idx);
            }

            return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        }
        catch (Exception ex)
        {
            Log.Warning("could not resolve audio device (flow={Flow}): {Error}", flow, ex.Message);
            return null;
        }
    }

    // ── Stop / Dispose ────────────────────────────────────────────────────────

    public void Stop()
    {
        IsRunning = false;

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.StopRecording();
            _capture.Dispose();
            _capture = null;
        }

        _output?.Stop();
        _output?.Dispose();
        _output = null;

        _buffer         = null;
        _volumeProvider = null;
    }

    public void Dispose() => Stop();
}
