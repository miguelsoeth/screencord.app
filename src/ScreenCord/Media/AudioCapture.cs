using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ScreenCord.Media;

/// <summary>
/// Captura o áudio que acompanha o compartilhamento e o codifica em Opus (48 kHz, estéreo, 20 ms por pacote).
/// Tela inteira: tudo o que toca no computador, menos o próprio ScreenCord (para quem compartilha e também
/// assiste alguém não devolver o áudio da outra pessoa). Janela: só o áudio do programa dono da janela.
/// </summary>
public sealed class AudioCapture : IAsyncDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FrameSamples = SampleRate / 50; // 20 ms
    private const int Bitrate = 128_000;

    private readonly WasapiRecorder _recorder;
    private readonly IOpusEncoder _encoder;
    private readonly Action<byte[], long> _onPacket;
    private readonly float[] _frame = new float[FrameSamples * Channels];
    private readonly byte[] _packet = new byte[4000];
    private readonly LinearResampler? _resampler;
    private readonly Lock _lock = new();
    private int _frameFill;
    private long _samplesSent;
    private bool _disposed;

    private AudioCapture(WasapiRecorder recorder, Action<byte[], long> onPacket)
    {
        _recorder = recorder;
        _onPacket = onPacket;
        var format = recorder.WaveFormat;
        if (format.SampleRate != SampleRate || format.Channels != Channels || format.Encoding != WaveFormatEncoding.IeeeFloat && !IsFloatExtensible(format))
        {
            _resampler = new LinearResampler(format);
        }

        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate = Bitrate;
        _encoder.Complexity = 5;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;

        _recorder.DataAvailable += OnDataAvailable;
        _recorder.StartRecording();
    }

    /// <summary>Formato de saída do áudio: PCM float intercalado.</summary>
    public static WaveFormat OutputFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    /// <summary>
    /// Começa a capturar. <paramref name="onPacket"/> recebe cada pacote Opus e o carimbo de tempo em
    /// microssegundos desde o início, numa thread de áudio.
    /// </summary>
    public static async Task<AudioCapture> StartAsync(CaptureSource source, Action<byte[], long> onPacket)
    {
        WasapiRecorder recorder;
        try
        {
            var (processId, mode) = source.IsScreen
                ? ((uint)Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree)
                : (source.ProcessId, ProcessLoopbackMode.IncludeTargetProcessTree);
            recorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback(processId, mode)
                .WithFormat(OutputFormat)
                .BuildAsync()
                .ConfigureAwait(false);
        }
        catch (Exception) when (source.IsScreen)
        {
            // Windows sem captura por processo (anterior ao Windows 10 2004): pega tudo o que toca no dispositivo padrão.
            recorder = await new WasapiRecorderBuilder()
                .WithLoopbackCapture()
                .BuildAsync()
                .ConfigureAwait(false);
        }

        try
        {
            return new AudioCapture(recorder, onPacket);
        }
        catch
        {
            await recorder.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _recorder.DataAvailable -= OnDataAvailable;
        try
        {
            _recorder.StopRecording();
        }
        catch (Exception)
        {
        }

        await _recorder.DisposeAsync().ConfigureAwait(false);
    }

    private static bool IsFloatExtensible(WaveFormat format) =>
        format is WaveFormatExtensible extensible && extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT && format.BitsPerSample == 32;

    private void OnDataAvailable(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_resampler is not null)
            {
                var silent = (flags & AudioClientBufferFlags.Silent) != 0;
                _resampler.Process(data, silent, Append);
                return;
            }

            var samples = MemoryMarshal.Cast<byte, float>(data);
            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                AppendSilence(samples.Length);
            }
            else
            {
                Append(samples);
            }
        }
    }

    private void AppendSilence(int count)
    {
        Span<float> zeros = stackalloc float[256];
        zeros.Clear();
        while (count > 0)
        {
            var chunk = Math.Min(count, zeros.Length);
            Append(zeros[..chunk]);
            count -= chunk;
        }
    }

    private void Append(ReadOnlySpan<float> samples)
    {
        while (!samples.IsEmpty)
        {
            var take = Math.Min(samples.Length, _frame.Length - _frameFill);
            samples[..take].CopyTo(_frame.AsSpan(_frameFill));
            _frameFill += take;
            samples = samples[take..];
            if (_frameFill == _frame.Length)
            {
                var length = _encoder.Encode(_frame, FrameSamples, _packet, _packet.Length);
                var timestamp = _samplesSent * 1_000_000 / SampleRate;
                _samplesSent += FrameSamples;
                _frameFill = 0;
                if (length > 0)
                {
                    _onPacket(_packet.AsSpan(0, length).ToArray(), timestamp);
                }
            }
        }
    }

    /// <summary>Converte o formato do dispositivo (qualquer taxa, canais e PCM 16/24/32 ou float) para 48 kHz estéreo float.</summary>
    private sealed class LinearResampler(WaveFormat format)
    {
        private readonly double _step = format.SampleRate / (double)SampleRate;
        private readonly float[] _output = new float[8192];
        private double _position;
        private float _previousLeft;
        private float _previousRight;

        public void Process(ReadOnlySpan<byte> data, bool silent, SpanAction append)
        {
            var bytesPerSample = format.BitsPerSample / 8;
            var frameSize = bytesPerSample * format.Channels;
            if (frameSize == 0)
            {
                return;
            }

            var frames = data.Length / frameSize;
            var count = 0;
            for (var i = 0; i < frames; i++)
            {
                float left = 0;
                float right = 0;
                if (!silent)
                {
                    left = Read(data, i * frameSize, bytesPerSample);
                    right = format.Channels > 1 ? Read(data, i * frameSize + bytesPerSample, bytesPerSample) : left;
                }

                // Interpola entre a amostra anterior e esta para cada amostra de saída que cai no intervalo.
                while (_position <= 1.0)
                {
                    _output[count++] = _previousLeft + (left - _previousLeft) * (float)_position;
                    _output[count++] = _previousRight + (right - _previousRight) * (float)_position;
                    _position += _step;
                    if (count == _output.Length)
                    {
                        append(_output);
                        count = 0;
                    }
                }

                _position -= 1.0;
                _previousLeft = left;
                _previousRight = right;
            }

            if (count > 0)
            {
                append(_output.AsSpan(0, count));
            }
        }

        private float Read(ReadOnlySpan<byte> data, int offset, int bytesPerSample)
        {
            var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || IsFloatExtensible(format);
            return bytesPerSample switch
            {
                4 when isFloat => BitConverter.ToSingle(data.Slice(offset, 4)),
                4 => BitConverter.ToInt32(data.Slice(offset, 4)) / 2147483648f,
                3 => (data[offset] << 8 | data[offset + 1] << 16 | data[offset + 2] << 24) / 2147483648f,
                2 => BitConverter.ToInt16(data.Slice(offset, 2)) / 32768f,
                _ => 0,
            };
        }
    }

    private delegate void SpanAction(ReadOnlySpan<float> samples);
}
