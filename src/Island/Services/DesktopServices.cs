using System;
using NAudio.CoreAudioApi;

namespace Island.Services;

public sealed class VolumeService : IDisposable
{
    private MMDeviceEnumerator? enumerator;
    private MMDevice? device;
    public double Get()
    {
        device?.Dispose(); enumerator ??= new(); device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return device.AudioEndpointVolume.MasterVolumeLevelScalar;
    }
    public void Set(double value)
    { if (device == null) Get(); device!.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(value, 0, 1); }
    public void Dispose() { device?.Dispose(); enumerator?.Dispose(); }
}
