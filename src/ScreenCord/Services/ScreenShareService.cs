using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenCord.Contracts;
using ScreenCord.Media;
using Microsoft.Extensions.Logging;

namespace ScreenCord.Services;

/// <summary>Compartilhamento de tela em andamento.</summary>
public interface IActiveScreenShare
{
    ScreenShareDto Share { get; }

    ShareRequest Request { get; }

    /// <summary>O compartilhamento parou sozinho (janela fechada, conexão caiu); o argumento é o motivo.</summary>
    event EventHandler<string>? Ended;

    Task StopAsync();
}

/// <summary>Compartilhar a própria tela e assistir à tela de outra pessoa da sala.</summary>
public interface IScreenShareService
{
    /// <summary>Id do usuário conectado na API central (para não oferecer "assistir" à própria tela).</summary>
    Guid? CurrentUserId { get; }

    /// <summary>Começa a compartilhar na sala do grupo em que a conexão em tempo real está.</summary>
    Task<IActiveScreenShare> StartAsync(Guid groupId, ShareRequest request, CancellationToken cancellationToken = default);

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
}

public sealed class ScreenShareService : IScreenShareService
{
    private readonly IGroupsRealtime _realtime;
    private readonly CentralApiClient _api;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ScreenShareService> _logger;

    public ScreenShareService(IGroupsRealtime realtime, CentralApiClient api, ILoggerFactory loggerFactory)
    {
        _realtime = realtime;
        _api = api;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ScreenShareService>();
    }

    public Guid? CurrentUserId => _api.CurrentUserId;

    public async Task<IActiveScreenShare> StartAsync(Guid groupId, ShareRequest request, CancellationToken cancellationToken = default)
    {
        if (!ScreenCapture.IsSupported)
        {
            throw new InvalidOperationException("Este Windows não permite capturar a tela (é preciso o Windows 10 versão 1903 ou mais recente).");
        }

        if (!CaptureSources.IsAlive(request.Source))
        {
            throw new InvalidOperationException("A janela escolhida foi fechada.");
        }

        var settings = request.Settings;
        ScreenCapture? capture = null;
        H264Encoder? encoder = null;
        MediaSocket? socket = null;
        var announced = false;
        try
        {
            // O tamanho final depende da proporção da tela ou janela, então a captura vem antes do anúncio.
            // Fora da thread da interface: a criação espera operações assíncronas do Windows.
            capture = await Task.Run(() => new ScreenCapture(request.Source, settings.Height, settings.Fps), cancellationToken);
            encoder = await Task.Run(() => H264Encoder.Create(capture.Width, capture.Height, settings.Fps, settings.BitrateKbps), cancellationToken);
            _logger.LogInformation(
                "Compartilhando {Kind} em {Width}x{Height}, {Fps} fps, {Kbps} kbps, encoder {Encoder}.",
                request.Source.Kind, capture.Width, capture.Height, settings.Fps, settings.BitrateKbps, encoder.Name);

            var share = await _realtime.StartScreenShareAsync(
                new ScreenShareStart(capture.Width, capture.Height, settings.Fps, settings.BitrateKbps, settings.ShareAudio),
                cancellationToken);
            announced = true;

            socket = await MediaSocket.ConnectAsync(_api, groupId, share.UserId, settings.BitrateKbps, cancellationToken);
            var active = new ActiveScreenShare(share, request, capture, encoder, socket, _realtime, _loggerFactory.CreateLogger<ActiveScreenShare>());
            if (settings.ShareAudio)
            {
                await active.StartAudioAsync();
            }

            active.Start();
            return active;
        }
        catch
        {
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
            }

            throw;
        }
    }

    public async Task<IStreamViewer> WatchAsync(Guid groupId, ScreenShareDto share, CancellationToken cancellationToken = default)
    {
        var decoder = new H264Decoder();
        try
        {
            var socket = await MediaSocket.ConnectAsync(_api, groupId, share.UserId, share.BitrateKbps, cancellationToken);
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
    /// enfileira para envio. Quando a tela não muda, o último quadro é reenviado (o encoder o comprime a quase nada).
    /// </summary>
    private sealed class ActiveScreenShare : IActiveScreenShare
    {
        private readonly ScreenCapture _capture;
        private readonly H264Encoder _encoder;
        private readonly MediaSocket _socket;
        private readonly IGroupsRealtime _realtime;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _videoThread;
        private readonly Stopwatch _clock = new();
        private readonly BitrateController _bitrate;
        private bool _adaptiveBitrate = true;
        private AudioCapture? _audio;
        private int _stopped;

        public ActiveScreenShare(
            ScreenShareDto share,
            ShareRequest request,
            ScreenCapture capture,
            H264Encoder encoder,
            MediaSocket socket,
            IGroupsRealtime realtime,
            ILogger logger)
        {
            Share = share;
            Request = request;
            _capture = capture;
            _encoder = encoder;
            _socket = socket;
            _realtime = realtime;
            _logger = logger;
            _bitrate = new BitrateController(request.Settings.BitrateKbps, TimeSpan.Zero);

            _encoder.FrameEncoded += frame =>
            {
                if (frame.IsKeyframe)
                {
                    _logger.LogDebug("Quadro-chave de {Bytes} bytes.", frame.Data.Length);
                }

                _socket.SendVideo(frame, frame.Timestamp / 10);
            };
            _socket.KeyframeRequested += () =>
            {
                _logger.LogDebug("Quadro-chave pedido.");
                _encoder.RequestKeyframe();
            };
            _socket.Closed += reason => End(reason ?? "O servidor encerrou o compartilhamento.");
            _capture.Closed += (_, _) => End(request.Source.IsScreen ? "A tela compartilhada foi desconectada." : "A janela compartilhada foi fechada.");
            _videoThread = new Thread(VideoLoop) { IsBackground = true, Name = "Compartilhamento de tela", Priority = ThreadPriority.AboveNormal };
            _socket.Start();
        }

        public ScreenShareDto Share { get; }

        public ShareRequest Request { get; }

        public event EventHandler<string>? Ended;

        public async Task StartAudioAsync()
        {
            try
            {
                _audio = await AudioCapture.StartAsync(Request.Source, (packet, timestamp) => _socket.SendAudio(packet, timestamp));
            }
            catch (Exception ex)
            {
                // Sem áudio o vídeo continua; quem assiste só não ouve nada.
                _logger.LogWarning("Captura de áudio indisponível ({ExceptionType}: {Message}).", ex.GetType().Name, ex.Message);
            }
        }

        public void Start()
        {
            _clock.Start();
            _videoThread.Start();
        }

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            await ShutdownAsync();
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
                    _capture.Update();
                    hasFrame |= _capture.ReadNv12(frame);
                    if (hasFrame)
                    {
                        _encoder.Encode(frame, _clock.Elapsed.Ticks);
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
                _logger.LogError(ex, "O compartilhamento de tela parou por um erro.");
                End("O compartilhamento parou por um erro na captura ou no encoder de vídeo.");
            }
            finally
            {
                _ = TimeEndPeriod(1);
            }
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
        private bool _disposed;

        public StreamViewer(MediaSocket socket, H264Decoder decoder, bool hasAudio, ILogger logger)
        {
            _socket = socket;
            _decoder = decoder;
            _logger = logger;
            HasAudio = hasAudio;
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
            if (!_disposed && Environment.TickCount64 - Interlocked.Read(ref _lastFrameTicks) > KeyframeWait.TotalMilliseconds)
            {
                _socket.RequestKeyframe();
            }
        }
    }
}
