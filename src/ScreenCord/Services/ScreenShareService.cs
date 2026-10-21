using ScreenCord.Localization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenCord.Contracts;
using ScreenCord.Infrastructure;
using ScreenCord.Media;
using Microsoft.Extensions.Logging;

namespace ScreenCord.Services;

/// <summary>Compartilhamento de tela em andamento.</summary>
public interface IActiveScreenShare
{
    ScreenShareDto Share { get; }

    ShareRequest Request { get; }

    /// <summary>Por que o áudio pedido não está sendo compartilhado; null quando está ou não foi pedido.</summary>
    string? AudioWarning { get; }

    /// <summary>O compartilhamento parou sozinho (janela fechada, conexão caiu); o argumento é o motivo.</summary>
    event EventHandler<string>? Ended;

    Task StopAsync();
}

/// <summary>Compartilhar a própria tela e assistir à tela de outra pessoa da sala.</summary>
public interface IScreenShareService
{
    /// <summary>Id do usuário conectado na API central (para não oferecer "assistir" à própria tela).</summary>
    Guid? CurrentUserId { get; }

    /// <summary>
    /// Começa a compartilhar na sala do grupo em que a conexão em tempo real está. Com <paramref name="replacing"/>,
    /// troca o compartilhamento atual pelo novo (ex.: outra resolução) sem que ele termine para quem assiste:
    /// o novo assume o anúncio e o canal, e o antigo para em seguida. Se a troca falhar antes do anúncio, o
    /// antigo continua; depois dele, o antigo termina (<see cref="IActiveScreenShare.Ended"/>).
    /// </summary>
    Task<IActiveScreenShare> StartAsync(Guid groupId, ShareRequest request, IActiveScreenShare? replacing = null, CancellationToken cancellationToken = default);

    /// <summary>Conecta ao compartilhamento de outra pessoa da sala.</summary>
    Task<IStreamViewer> WatchAsync(Guid groupId, ScreenShareDto share, CancellationToken cancellationToken = default);
}

/// <summary>Recepção de um compartilhamento. Os eventos chegam em threads de fundo.</summary>
public interface IStreamViewer : IAsyncDisposable
{
    /// <summary>Quadro decodificado em BGRA (largura × altura × 4). O buffer só vale durante a chamada.</summary>
    event DecodedFrameHandler? FrameReady;

    /// <summary>A transmissão terminou; o argumento é o motivo quando não foi um encerramento normal.</summary>
    event EventHandler<string?>? Ended;

    bool HasAudio { get; }

    float Volume { get; set; }

    bool IsMuted { get; set; }

    /// <summary>Para de receber o vídeo, mantendo o áudio (ex.: janela minimizada), para economizar banda.</summary>
    bool IsVideoPaused { get; set; }
}

public sealed class ScreenShareService : IScreenShareService
{
    private readonly IGroupsRealtime _realtime;
    private readonly CentralApiClient _api;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ScreenShareService> _logger;
    private readonly Local.ILocalRooms? _local;

    /// <param name="local">Salas locais: nelas a mídia vai direto entre os computadores, sem passar pela API.</param>
    public ScreenShareService(IGroupsRealtime realtime, CentralApiClient api, ILoggerFactory loggerFactory, Local.ILocalRooms? local = null)
    {
        _realtime = realtime;
        _api = api;
        _local = local;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ScreenShareService>();
    }

    public Guid? CurrentUserId => _api.CurrentUserId;

    public async Task<IActiveScreenShare> StartAsync(Guid groupId, ShareRequest request, IActiveScreenShare? replacing = null, CancellationToken cancellationToken = default)
    {
        var previous = replacing as ActiveScreenShare;
        if (!ScreenCapture.IsSupported)
        {
            throw new InvalidOperationException(Strings.CaptureNotSupported);
        }

        if (!CaptureSources.IsAlive(request.Source))
        {
            throw new InvalidOperationException(Strings.ChosenWindowClosed);
        }

        var settings = request.Settings;
        IScreenCapture? capture = null;
        H264Encoder? encoder = null;
        IMediaSink? socket = null;
        AudioCapture? audio = null;
        IMediaSink? audioTarget = null;
        string? audioWarning = null;
        var announced = false;
        try
        {
            // O tamanho final depende da proporção da tela ou janela, então a captura vem antes do anúncio.
            // Fora da thread da interface: a criação espera operações assíncronas do Windows.
            capture = await Task.Run(() => ScreenCapture.Create(request.Source, settings.Height, settings.Fps, _logger), cancellationToken);
            encoder = await Task.Run(() => H264Encoder.Create(capture.Width, capture.Height, settings.Fps, settings.BitrateKbps), cancellationToken);
            _logger.LogInformation(
                "Compartilhando {Kind} ({Capture}) em {Width}x{Height}, {Fps} fps, {Kbps} kbps, encoder {Encoder}.",
                request.Source.Kind, capture.Name, capture.Width, capture.Height, settings.Fps, settings.BitrateKbps, encoder.Name);

            // O áudio também vem antes do anúncio: a sala só vê "com áudio" se a captura começou de fato.
            // Os pacotes de antes do canal de mídia abrir se perdem.
            if (settings.ShareAudio)
            {
                try
                {
                    audio = await AudioCapture.StartAsync(request.Source, (packet, timestamp) => audioTarget?.SendAudio(packet, timestamp));
                }
                catch (Exception ex)
                {
                    // Sem áudio o vídeo continua; quem compartilha é avisado.
                    _logger.LogWarning(ex, "Captura de áudio indisponível para {Kind} (processo {ProcessId}); compartilhando sem som.",
                        request.Source.Kind, request.Source.ProcessId);
                    audioWarning = Strings.AudioCaptureFailed;
                }
            }

            // O novo anúncio substitui o do compartilhamento atual; a partir daqui, quando o canal antigo fechar
            // (o servidor o troca pelo novo), ele não pode avisar o fim do compartilhamento.
            previous?.PrepareReplacement();
            var share = await _realtime.StartScreenShareAsync(
                new ScreenShareStart(capture.Width, capture.Height, settings.Fps, settings.BitrateKbps, audio is not null),
                cancellationToken);
            announced = true;

            socket = _local?.IsLocal(groupId) == true
                ? _local.OpenPublisher()
                : await MediaSocket.ConnectAsync(_api, groupId, share.UserId, settings.BitrateKbps, cancellationToken);
            audioTarget = socket;
            var active = new ActiveScreenShare(share, request, capture, encoder, socket, audio, audioWarning, _realtime, _loggerFactory.CreateLogger<ActiveScreenShare>());
            active.Start();
            if (previous is not null)
            {
                await previous.StopAsync();
            }

            return active;
        }
        catch
        {
            if (audio is not null)
            {
                await audio.DisposeAsync();
            }

            if (socket is not null)
            {
                await socket.DisposeAsync();
            }

            encoder?.Dispose();
            capture?.Dispose();
            if (announced)
            {
                try
                {
                    await _realtime.StopScreenShareAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Falha ao desfazer o anúncio do compartilhamento ({ExceptionType}).", ex.GetType().Name);
                }

                previous?.Fail(Strings.NewSettingsFailed);
            }
            else
            {
                previous?.CancelReplacement();
            }

            throw;
        }
    }

    public async Task<IStreamViewer> WatchAsync(Guid groupId, ScreenShareDto share, CancellationToken cancellationToken = default)
    {
        var decoder = new H264Decoder();
        try
        {
            var socket = _local?.IsLocal(groupId) == true
                ? await _local.WatchAsync(groupId, share.UserId, cancellationToken)
                : await MediaSocket.ConnectAsync(_api, groupId, share.UserId, share.BitrateKbps, cancellationToken);
            return new StreamViewer(socket, decoder, share.HasAudio, _loggerFactory.CreateLogger<StreamViewer>());
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Laço de vídeo numa thread própria: a cada 1/fps pega o quadro mais recente da captura, codifica e
    /// enfileira para envio. Com a tela parada, o ritmo cai para poucos quadros por segundo (<see cref="FramePacer"/>).
    /// </summary>
    private sealed class ActiveScreenShare : IActiveScreenShare
    {
        private readonly IScreenCapture _capture;
        private volatile H264Encoder _encoder;
        private readonly IMediaSink _socket;
        private readonly IGroupsRealtime _realtime;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _videoThread;
        private readonly Stopwatch _clock = new();
        private readonly BitrateController _bitrate;
        private readonly FramePacer _pacer = new();
        private bool _adaptiveBitrate = true;
        private readonly AudioCapture? _audio;
        private int _stopped;
        private volatile bool _replaced;

        public ActiveScreenShare(
            ScreenShareDto share,
            ShareRequest request,
            IScreenCapture capture,
            H264Encoder encoder,
            IMediaSink socket,
            AudioCapture? audio,
            string? audioWarning,
            IGroupsRealtime realtime,
            ILogger logger)
        {
            Share = share;
            Request = request;
            AudioWarning = audioWarning;
            _capture = capture;
            _encoder = encoder;
            _socket = socket;
            _audio = audio;
            _realtime = realtime;
            _logger = logger;
            _bitrate = new BitrateController(request.Settings.BitrateKbps, TimeSpan.Zero);

            _encoder.FrameEncoded += OnFrameEncoded;
            _socket.KeyframeRequested += () =>
            {
                _logger.LogDebug("Quadro-chave pedido.");
                _encoder.RequestKeyframe();
            };
            _socket.Closed += reason => End(reason ?? Strings.ServerEndedShare);
            _capture.Closed += (_, _) => End(request.Source.IsScreen ? Strings.SharedScreenDisconnected : Strings.SharedWindowClosed);
            _videoThread = new Thread(VideoLoop) { IsBackground = true, Name = "Compartilhamento de tela", Priority = ThreadPriority.AboveNormal };
            _socket.Start();
        }

        public ScreenShareDto Share { get; }

        public ShareRequest Request { get; }

        public string? AudioWarning { get; }

        public event EventHandler<string>? Ended;

        public void Start()
        {
            ErrorContext.Clear("share.");
            ErrorContext.Set("share.kind", Request.Source.Kind);
            ErrorContext.Set("share.capture", _capture.Name);
            ErrorContext.Set("share.encoder", _encoder.Name);
            ErrorContext.Set("share.size", $"{_capture.Width}x{_capture.Height}");
            ErrorContext.Set("share.fps", Request.Settings.Fps);
            ErrorContext.Set("share.kbps", Request.Settings.BitrateKbps);
            ErrorContext.Set("share.audio", _audio is not null);
            _clock.Start();
            _videoThread.Start();
        }

        /// <summary>Outro compartilhamento vai assumir o anúncio na sala: ao parar, este não avisa o servidor nem dispara <see cref="Ended"/>.</summary>
        public void PrepareReplacement() => _replaced = true;

        /// <summary>A troca falhou antes do anúncio: este continua sendo o compartilhamento da sala.</summary>
        public void CancelReplacement() => _replaced = false;

        /// <summary>A troca falhou depois do anúncio: este termina com <paramref name="reason"/>.</summary>
        public void Fail(string reason)
        {
            _replaced = false;
            if (_stopped != 0)
            {
                // Já parou sem avisar (o canal foi trocado pelo novo, que falhou em seguida).
                Ended?.Invoke(this, reason);
                return;
            }

            End(reason);
        }

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            await ShutdownAsync();
            if (_replaced)
            {
                return;
            }

            try
            {
                await _realtime.StopScreenShareAsync();
            }
            catch (Exception ex)
            {
                // Sem conexão, o servidor já encerrou o compartilhamento junto com ela.
                _logger.LogDebug("Falha ao avisar o fim do compartilhamento ({ExceptionType}).", ex.GetType().Name);
            }
        }

        private void End(string reason)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                await ShutdownAsync();
                if (_replaced)
                {
                    return;
                }

                try
                {
                    await _realtime.StopScreenShareAsync();
                }
                catch (Exception)
                {
                }

                Ended?.Invoke(this, reason);
            });
        }

        private async Task ShutdownAsync()
        {
            _stop.Cancel();
            if (_videoThread.IsAlive && Thread.CurrentThread != _videoThread)
            {
                await Task.Run(() => _videoThread.Join(TimeSpan.FromSeconds(3)));
            }

            if (_audio is not null)
            {
                await _audio.DisposeAsync();
            }

            await _socket.DisposeAsync();
            _encoder.Dispose();
            _capture.Dispose();
            if (!_replaced)
            {
                ErrorContext.Clear("share.");
            }
        }

        private void VideoLoop()
        {
            var interval = TimeSpan.FromSeconds(1.0 / Request.Settings.Fps);
            var frame = new byte[_capture.Width * _capture.Height * 3 / 2];
            var next = _clock.Elapsed;
            var hasFrame = false;

            // Resolução de 1 ms para o Sleep: sem isso, 60 fps viram ~32 fps.
            _ = TimeBeginPeriod(1);
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var changed = _capture.Update();
                    if (changed)
                    {
                        hasFrame |= _capture.ReadNv12(frame);
                    }

                    if (hasFrame && _pacer.ShouldEncode(_clock.Elapsed, changed, _encoder.IsKeyframePending))
                    {
                        try
                        {
                            _encoder.Encode(frame, _clock.Elapsed.Ticks);
                        }
                        catch (H264EncoderFailedException ex) when (_encoder.IsHardware)
                        {
                            if (!TrySwitchToSoftwareEncoder(ex))
                            {
                                throw;
                            }
                        }
                    }

                    AdaptBitrate();

                    next += interval;
                    var wait = next - _clock.Elapsed;
                    if (wait < -interval)
                    {
                        // Atrasou mais de um quadro (máquina ocupada): não tenta compensar de uma vez.
                        next = _clock.Elapsed;
                    }
                    else if (wait > TimeSpan.Zero)
                    {
                        _stop.Token.WaitHandle.WaitOne(wait);
                    }
                }
            }
            catch (Exception ex) when (!_stop.IsCancellationRequested)
            {
                _logger.LogError(
                    ex, "O compartilhamento de tela parou por um erro ({Capture}, encoder {Encoder}, {Width}x{Height}, {Fps} fps, {Frames} quadros em {Elapsed}).",
                    _capture.Name, _encoder.Name, _capture.Width, _capture.Height, Request.Settings.Fps, _encoder.EncodedFrames, _clock.Elapsed.ToString(@"hh\:mm\:ss"));
                End(Strings.CaptureOrEncoderFailed);
            }
            finally
            {
                _ = TimeEndPeriod(1);
            }
        }

        private void OnFrameEncoded(EncodedFrame frame)
        {
            if (frame.IsKeyframe)
            {
                _logger.LogDebug("Quadro-chave de {Bytes} bytes.", frame.Data.Length);
            }

            _socket.SendVideo(frame, frame.Timestamp / 10);
        }

        /// <summary>
        /// O encoder de hardware parou no meio da transmissão (driver reiniciado, placa de vídeo trocada...):
        /// segue com o encoder por software do Windows. Roda na thread de vídeo. Falso se nem ele pôde ser criado.
        /// </summary>
        private bool TrySwitchToSoftwareEncoder(H264EncoderFailedException failure)
        {
            var failed = _encoder;
            _logger.LogError(
                failure.InnerException, "O encoder {Encoder} falhou depois de {Frames} quadros em {Elapsed} ({Width}x{Height}, {Fps} fps, {Kbps} kbps); trocando para o encoder por software.",
                failed.Name, failed.EncodedFrames, _clock.Elapsed.ToString(@"hh\:mm\:ss"), failed.Width, failed.Height, Request.Settings.Fps, _bitrate.TargetKbps);

            H264Encoder software;
            try
            {
                software = H264Encoder.Create(failed.Width, failed.Height, Request.Settings.Fps, _bitrate.TargetKbps, allowHardware: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "O encoder por software também não pôde ser criado.");
                return false;
            }

            failed.FrameEncoded -= OnFrameEncoded;
            software.FrameEncoded += OnFrameEncoded;
            software.RequestKeyframe(); // quem assiste precisa de um quadro-chave do encoder novo
            _encoder = software;
            _adaptiveBitrate = true;
            ErrorContext.Set("share.encoder", $"{software.Name} (depois de falhar {failed.Name})");
            failed.Dispose();
            _logger.LogInformation("Compartilhamento seguindo com o encoder {Encoder}.", software.Name);
            return true;
        }

        /// <summary>Ajusta a taxa do encoder à fila de envio. Roda na thread de vídeo, a mesma do <see cref="H264Encoder.Encode"/>.</summary>
        private void AdaptBitrate()
        {
            if (!_adaptiveBitrate || _bitrate.Update(_clock.Elapsed, _socket.QueuedBytes, _socket.VideoOverflows) is not { } kbps)
            {
                return;
            }

            if (_encoder.SetBitrate(kbps))
            {
                _logger.LogDebug("Taxa de bits ajustada para {Kbps} kbps (escolhida: {Target} kbps).", kbps, _bitrate.TargetKbps);
            }
            else
            {
                _adaptiveBitrate = false;
                _logger.LogInformation("O encoder {Encoder} não aceita mudar a taxa de bits; ela fica fixa em {Target} kbps.", _encoder.Name, _bitrate.TargetKbps);
            }
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint milliseconds);
    }

    /// <summary>Recebe os pacotes, decodifica o vídeo na thread de leitura e toca o áudio.</summary>
    private sealed class StreamViewer : IStreamViewer
    {
        private readonly MediaSocket _socket;
        private readonly H264Decoder _decoder;
        private readonly AudioPlayer? _audio;
        private readonly ILogger _logger;
        private static readonly TimeSpan KeyframeWait = TimeSpan.FromSeconds(1.5);

        private readonly Lock _decodeLock = new();
        private readonly Timer _keyframeWatchdog;
        private long _lastFrameTicks = Environment.TickCount64;
        private volatile bool _videoPaused;
        private bool _disposed;

        public StreamViewer(MediaSocket socket, H264Decoder decoder, bool hasAudio, ILogger logger)
        {
            _socket = socket;
            _decoder = decoder;
            _logger = logger;
            HasAudio = hasAudio;
            ErrorContext.ViewerOpened();
            if (hasAudio)
            {
                try
                {
                    _audio = new AudioPlayer();
                }
                catch (Exception ex)
                {
                    HasAudio = false;
                    _logger.LogWarning("Saída de áudio indisponível ({ExceptionType}).", ex.GetType().Name);
                }
            }

            _socket.PacketReceived += OnPacket;
            _socket.Closed += reason => Ended?.Invoke(this, reason);
            _socket.Start();

            // Sem imagem logo depois de entrar (ex.: o quadro-chave se perdeu), pede outro.
            _keyframeWatchdog = new Timer(_ => RequestKeyframeIfStuck(), null, KeyframeWait, KeyframeWait);
        }

        public event DecodedFrameHandler? FrameReady;

        public event EventHandler<string?>? Ended;

        public bool HasAudio { get; }

        public float Volume
        {
            get => _audio?.Volume ?? 0;
            set
            {
                if (_audio is not null)
                {
                    _audio.Volume = value;
                }
            }
        }

        public bool IsMuted
        {
            get => _audio?.IsMuted ?? true;
            set
            {
                if (_audio is not null)
                {
                    _audio.IsMuted = value;
                }
            }
        }

        public bool IsVideoPaused
        {
            get => _videoPaused;
            set
            {
                if (_videoPaused == value || _disposed)
                {
                    return;
                }

                // Ao voltar, o vigia de quadro-chave conta do zero: o primeiro quadro leva um instante para chegar.
                Interlocked.Exchange(ref _lastFrameTicks, Environment.TickCount64);
                _videoPaused = value;
                _socket.SetVideoPaused(value);
            }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_decodeLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            ErrorContext.ViewerClosed();
            _socket.PacketReceived -= OnPacket;
            await _keyframeWatchdog.DisposeAsync();
            await _socket.DisposeAsync();
            lock (_decodeLock)
            {
                _decoder.Dispose();
            }

            _audio?.Dispose();
        }

        private void OnPacket(byte type, byte flags, long timestamp, ReadOnlyMemory<byte> payload)
        {
            if (type == Contracts.MediaContract.Audio)
            {
                _audio?.Enqueue(payload.Span);
                return;
            }

            if (type != Contracts.MediaContract.Video)
            {
                return;
            }

            lock (_decodeLock)
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    _decoder.Decode(payload.Span, timestamp * 10, (bgra, width, height, time) =>
                    {
                        Interlocked.Exchange(ref _lastFrameTicks, Environment.TickCount64);
                        FrameReady?.Invoke(bgra, width, height, time);
                    });
                }
                catch (Exception ex)
                {
                    // Quadro inválido: descarta e pede um quadro-chave para recuperar a imagem.
                    _logger.LogDebug("Falha ao decodificar um quadro ({ExceptionType}).", ex.GetType().Name);
                    _socket.RequestKeyframe();
                }
            }
        }

        private void RequestKeyframeIfStuck()
        {
            if (!_disposed && !_videoPaused && Environment.TickCount64 - Interlocked.Read(ref _lastFrameTicks) > KeyframeWait.TotalMilliseconds)
            {
                _socket.RequestKeyframe();
            }
        }
    }
}
