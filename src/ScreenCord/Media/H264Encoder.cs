using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace ScreenCord.Media;

/// <summary>Quadro H.264 (Annex B) pronto para envio. <see cref="Timestamp"/> em unidades de 100 ns.</summary>
public sealed record EncodedFrame(byte[] Data, bool IsKeyframe, long Timestamp);

/// <summary>
/// Encoder H.264 do Media Foundation, com taxa de bits constante e baixa latência (sem quadros B).
/// Usa o encoder de hardware da placa de vídeo quando existe (NVIDIA, Intel, AMD) e cai para o encoder
/// por software do Windows se nenhum aceitar a configuração. Recebe NV12 em memória.
/// </summary>
public sealed class H264Encoder : IDisposable
{
    private const int MftEnumFlagSyncMft = 0x1;
    private const int MftEnumFlagAsyncMft = 0x2;
    private const int MftEnumFlagHardware = 0x4;
    private const int MftEnumFlagSortAndFilter = 0x40;

    private readonly IMFActivate _activate;
    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator? _events;
    private readonly CodecApi? _codecApi;
    private readonly SemaphoreSlim _needInput = new(0, int.MaxValue);
    private readonly Thread? _eventThread;
    private readonly Lock _outputLock = new();
    private readonly byte[] _sequenceHeader;
    private readonly long _frameDuration;
    private volatile bool _keyframeRequested;
    private volatile bool _disposed;

    private H264Encoder(IMFActivate activate, IMFTransform transform, bool isAsync, string name, int width, int height, int frameRate, int bitrateKbps)
    {
        _activate = activate;
        _transform = transform;
        Name = name;
        Width = width;
        Height = height;
        IsHardware = isAsync;
        _frameDuration = 10_000_000L / frameRate;

        if (isAsync)
        {
            using var attributes = transform.Attributes;
            attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
            _events = transform.QueryInterface<IMFMediaEventGenerator>();
        }

        _codecApi = CodecApi.From(transform);
        _codecApi?.Set(CodecApi.RateControlMode, CodecApi.RateControlCbr);
        _codecApi?.Set(CodecApi.MeanBitRate, (uint)bitrateKbps * 1000);
        _codecApi?.Set(CodecApi.LowLatencyMode, true);
        _codecApi?.Set(CodecApi.BPictureCount, 0u);

        // Quadro-chave a cada 4 s; quem entra no meio pede um na hora pela API.
        _codecApi?.Set(CodecApi.GopSize, (uint)frameRate * 4);

        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrateKbps * 1000);
            output.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)width, (uint)height));
            output.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio(frameRate, 1));
            output.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1));
            output.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // MFVideoInterlace_Progressive
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, 77u); // eAVEncH264VProfile_Main
            transform.SetOutputType(0, output, 0);
        }

        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            input.Set(MediaTypeAttributeKeys.FrameSize, MediaFactory.PackSize((uint)width, (uint)height));
            input.Set(MediaTypeAttributeKeys.FrameRate, MediaFactory.PackRatio(frameRate, 1));
            input.Set(MediaTypeAttributeKeys.PixelAspectRatio, MediaFactory.PackRatio(1, 1));
            input.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            input.Set(MediaTypeAttributeKeys.YuvMatrix, 1u); // MFVideoTransferMatrix_BT709
            input.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u); // MFNominalRange_16_235
            transform.SetInputType(0, input, 0);
        }

        // SPS/PPS: alguns encoders só os põem no tipo de saída; são repetidos antes de cada quadro-chave.
        try
        {
            using var current = transform.GetOutputCurrentType(0);
            _sequenceHeader = current.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
        }
        catch (SharpGenException)
        {
            _sequenceHeader = [];
        }

        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        if (_events is not null)
        {
            _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "Encoder H.264", Priority = ThreadPriority.AboveNormal };
            _eventThread.Start();
        }
    }

    /// <summary>Quadro codificado. Em encoders de hardware chega numa thread própria do encoder.</summary>
    public event Action<EncodedFrame>? FrameEncoded;

    public string Name { get; }

    public bool IsHardware { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Quadros descartados porque o encoder ainda estava ocupado com o anterior.</summary>
    public long DroppedFrames { get; private set; }

    /// <summary>Cria o melhor encoder disponível para a configuração, tentando hardware antes de software.</summary>
    public static H264Encoder Create(int width, int height, int frameRate, int bitrateKbps, bool allowHardware = true)
    {
        MediaFoundation.EnsureStarted();
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        var flags = MftEnumFlagSyncMft | MftEnumFlagSortAndFilter | (allowHardware ? MftEnumFlagAsyncMft | MftEnumFlagHardware : 0);

        var failures = new List<string>();
        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, (uint)flags, null, output);
        foreach (var item in activates)
        {
            var activate = MediaFoundation.Keep(item);
            string name;
            try
            {
                name = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
            }
            catch (SharpGenException)
            {
                name = "Encoder H.264";
            }

            // O encoder de DX12 da Microsoft exige superfícies de GPU; aqui as entradas ficam em memória.
            if (name.Contains("DX12", StringComparison.OrdinalIgnoreCase))
            {
                activate.Dispose();
                continue;
            }

            IMFTransform? transform = null;
            try
            {
                transform = activate.ActivateObject<IMFTransform>();
                return new H264Encoder(activate, transform, IsAsync(transform), name, width, height, frameRate, bitrateKbps);
            }
            catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
            {
                failures.Add($"{name}: {ex.Message}");
                transform?.Dispose();
                try
                {
                    activate.ShutdownObject();
                }
                catch (SharpGenException)
                {
                }

                activate.Dispose();
            }
        }

        throw new InvalidOperationException("Nenhum encoder H.264 aceitou a configuração. " + string.Join(" | ", failures));
    }

    /// <summary>MFTs de hardware são assíncronos (MF_TRANSFORM_ASYNC) e precisam ser destravados antes do uso.</summary>
    private static bool IsAsync(IMFTransform transform)
    {
        try
        {
            using var attributes = transform.Attributes;
            return attributes.GetUInt32(TransformAttributeKeys.TransformAsync) != 0;
        }
        catch (SharpGenException)
        {
            return false;
        }
    }

    /// <summary>O próximo quadro sai como quadro-chave (alguém começou a assistir ou perdeu pacotes).</summary>
    public void RequestKeyframe() => _keyframeRequested = true;

    /// <summary>
    /// Muda a taxa de bits durante a transmissão. Deve ser chamado pela mesma thread de <see cref="Encode"/>.
    /// Retorna falso se o encoder não aceita a mudança.
    /// </summary>
    public bool SetBitrate(int kbps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _codecApi?.Set(CodecApi.MeanBitRate, (uint)kbps * 1000) ?? false;
    }

    /// <summary>
    /// Envia um quadro NV12 (<see cref="Width"/> × <see cref="Height"/>, contíguo). Se o encoder de hardware
    /// ainda estiver ocupado com o anterior, o quadro é descartado em vez de acumular atraso.
    /// </summary>
    public void Encode(ReadOnlySpan<byte> nv12, long timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_events is not null && !_needInput.Wait(0))
        {
            DroppedFrames++;
            return;
        }

        if (_keyframeRequested)
        {
            _keyframeRequested = false;
            _codecApi?.Set(CodecApi.ForceKeyFrame, 1u);
        }

        using (var sample = MediaFoundation.CreateSample(nv12, timestamp, _frameDuration))
        {
            _transform.ProcessInput(0, sample, 0);
        }

        if (_events is null)
        {
            DrainOutput();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
        }
        catch (SharpGenException)
        {
        }

        // Desligar o MFT faz o GetEvent bloqueado na thread de eventos retornar com erro.
        try
        {
            _activate.ShutdownObject();
        }
        catch (SharpGenException)
        {
        }

        _eventThread?.Join(TimeSpan.FromSeconds(2));
        lock (_outputLock)
        {
            _codecApi?.Dispose();
            _events?.Dispose();
            _transform.Dispose();
            _activate.Dispose();
        }

        _needInput.Dispose();
    }

    private void EventLoop()
    {
        while (!_disposed)
        {
            IMFMediaEvent mediaEvent;
            try
            {
                mediaEvent = _events!.GetEvent(0);
            }
            catch (SharpGenException)
            {
                return;
            }

            using (mediaEvent)
            {
                var type = mediaEvent.EventType;
                if (type == MediaEventTypes.TransformNeedInput)
                {
                    _needInput.Release();
                }
                else if (type == MediaEventTypes.TransformHaveOutput)
                {
                    try
                    {
                        DrainOutput();
                    }
                    catch (SharpGenException) when (_disposed)
                    {
                        return;
                    }
                }
            }
        }
    }

    private void DrainOutput()
    {
        lock (_outputLock)
        {
            if (_disposed && _events is not null)
            {
                return;
            }

            var info = _transform.GetOutputStreamInfo(0);
            // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES (0x100) ou CAN_PROVIDE_SAMPLES (0x200): a amostra vem do encoder.
            var providesSamples = (info.Flags & 0x300) != 0;

            while (true)
            {
                var buffer = new OutputDataBuffer { StreamID = 0 };
                if (!providesSamples)
                {
                    buffer.Sample = MediaFoundation.CreateEmptySample(Math.Max(info.Size, Width * Height), info.Alignment);
                }

                var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
                try
                {
                    if (result == ResultCode.TransformNeedMoreInput)
                    {
                        return;
                    }

                    if (result == ResultCode.TransformStreamChange)
                    {
                        // O encoder renegociou o tipo de saída; reaplica o atual e tenta de novo.
                        using var type = _transform.GetOutputAvailableType(0, 0);
                        _transform.SetOutputType(0, type, 0);
                        continue;
                    }

                    result.CheckError();
                    if (buffer.Sample is { } sample)
                    {
                        Emit(sample);
                    }
                }
                finally
                {
                    buffer.Sample?.Dispose();
                    buffer.Events?.Dispose();
                }

                // Encoders assíncronos entregam uma amostra por evento.
                if (_events is not null)
                {
                    return;
                }
            }
        }
    }

    private void Emit(IMFSample sample)
    {
        var data = MediaFoundation.ToArray(sample);
        if (data.Length == 0)
        {
            return;
        }

        bool isKeyframe;
        try
        {
            isKeyframe = sample.GetUInt32(SampleAttributeKeys.CleanPoint) != 0;
        }
        catch (SharpGenException)
        {
            isKeyframe = H264.ContainsIdr(data);
        }

        if (isKeyframe && _sequenceHeader.Length > 0 && !H264.ContainsSps(data))
        {
            data = [.. _sequenceHeader, .. data];
        }

        FrameEncoded?.Invoke(new EncodedFrame(data, isKeyframe, sample.SampleTime));
    }
}

/// <summary>Inspeção mínima de fluxos H.264 Annex B.</summary>
public static class H264
{
    public static bool ContainsSps(ReadOnlySpan<byte> data) => ContainsNal(data, 7);

    public static bool ContainsIdr(ReadOnlySpan<byte> data) => ContainsNal(data, 5);

    private static bool ContainsNal(ReadOnlySpan<byte> data, int nalType)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && (data[i + 3] & 0x1F) == nalType)
            {
                return true;
            }
        }

        return false;
    }
}
