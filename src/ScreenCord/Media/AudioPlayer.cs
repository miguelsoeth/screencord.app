using System.Runtime.InteropServices;
using Concentus;
using NAudio.Wave;

namespace ScreenCord.Media;

/// <summary>
/// Toca o áudio Opus de um compartilhamento. Mantém pouco áudio em espera: se acumular atraso (rede
/// irregular), descarta o excesso para continuar perto do vídeo.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private static readonly TimeSpan MaxBuffered = TimeSpan.FromMilliseconds(300);

    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(AudioCapture.SampleRate, AudioCapture.Channels);
    private readonly BufferedWaveProvider _buffer;
    private readonly WasapiPlayer _player;
    private readonly float[] _pcm = new float[AudioCapture.FrameSamples * AudioCapture.Channels * 6];
    private readonly Lock _lock = new();
    private bool _disposed;

    public AudioPlayer()
    {
        _buffer = new BufferedWaveProvider(AudioCapture.OutputFormat, TimeSpan.FromSeconds(1))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        _player = new WasapiPlayerBuilder().WithSharedMode().WithLatency(60).Build();
        _player.Init(_buffer);
        _player.Play();
    }

    /// <summary>Volume de 0 a 1, só deste compartilhamento.</summary>
    public float Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0f, 1f);
    }

    public bool IsMuted
    {
        get => _player.IsMuted;
        set => _player.IsMuted = value;
    }

    public void Enqueue(ReadOnlySpan<byte> opus)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            int samples;
            try
            {
                samples = _decoder.Decode(opus, _pcm, _pcm.Length / AudioCapture.Channels, false);
            }
            catch (Exception)
            {
                // Pacote corrompido: só ele se perde.
                return;
            }

            if (samples <= 0)
            {
                return;
            }

            if (_buffer.BufferedDuration > MaxBuffered)
            {
                _buffer.ClearBuffer();
            }

            _buffer.AddSamples(MemoryMarshal.AsBytes(_pcm.AsSpan(0, samples * AudioCapture.Channels)));
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            _player.Stop();
        }
        catch (Exception)
        {
        }

        _player.Dispose();
    }
}
