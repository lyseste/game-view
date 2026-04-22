using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using Serilog;
using Windows.Media.Capture.Frames;

namespace VCDV.Devices;

public record AudioDeviceInfo(string Id, string Name, int Index);

public static class DeviceEnumerator
{
    // ── Video ────────────────────────────────────────────────────────────────

    public static async Task<List<string>> EnumerateVideoDevicesAsync()
    {
        try
        {
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            var names = groups
                .Where(g => g.SourceInfos.Any(s =>
                    s.SourceKind == MediaFrameSourceKind.Color))
                .Select(g => g.DisplayName)
                .Distinct()
                .ToList();

            Log.Information("video devices: [{Devices}]", string.Join(", ", names));
            return names;
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to enumerate video devices: {Error}", ex.Message);
            return [];
        }
    }

    // ── Audio ────────────────────────────────────────────────────────────────

    public static List<AudioDeviceInfo> EnumerateAudioInputs()
    {
        var devices = EnumerateAudioDevices(DataFlow.Capture);
        Log.Information("audio inputs: [{Devices}]",
            string.Join(", ", devices.Select(d => $"{d.Index}:{d.Name}")));
        return devices;
    }

    public static List<AudioDeviceInfo> EnumerateAudioOutputs()
    {
        var devices = EnumerateAudioDevices(DataFlow.Render);
        Log.Information("audio outputs: [{Devices}]",
            string.Join(", ", devices.Select(d => $"{d.Index}:{d.Name}")));
        return devices;
    }

    private static List<AudioDeviceInfo> EnumerateAudioDevices(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator
                .EnumerateAudioEndPoints(flow, DeviceState.Active)
                .Select((d, i) => new AudioDeviceInfo(d.ID, d.FriendlyName, i))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to enumerate audio devices ({Flow}): {Error}", flow, ex.Message);
            return [];
        }
    }

    // ── Audio auto-detect from video device name ──────────────────────────────

    private static readonly HashSet<string> _stopWords =
        ["the", "and", "for", "usb", "hdmi", "audio", "video", "capture", "device", "input", "output"];

    public static int? FindAudioInputForVideo(string videoDeviceName, IList<AudioDeviceInfo> inputs)
    {
        var keywords = videoDeviceName
            .Split(' ', '-', '_', '(', ')')
            .Select(w => w.ToLowerInvariant())
            .Where(w => w.Length >= 3 && !_stopWords.Contains(w))
            .ToArray();

        if (keywords.Length == 0)
            return null;

        int threshold = keywords.Length == 1 ? 1 : 2;

        var best = inputs
            .Select(d => (Device: d, Score: keywords.Count(k =>
                d.Name.ToLowerInvariant().Contains(k))))
            .Where(x => x.Score >= threshold)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        if (best.Device is not null)
        {
            Log.Information("audio auto-detect matched input {Index} ('{Name}') for video '{Video}'",
                best.Device.Index, best.Device.Name, videoDeviceName);
            return best.Device.Index;
        }

        return null;
    }
}
