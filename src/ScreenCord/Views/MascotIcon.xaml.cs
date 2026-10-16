using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ScreenCord.Views;

/// <summary>
/// O monitor do ícone com os olhos vivos: cada olho acompanha o mouse pela janela, com atraso suave e um leve
/// paralaxe (o monitor anda bem menos que os olhos), e fica vesgo com o mouse bem no meio do ícone. Os olhos piscam
/// de tempos em tempos e, com um clique, o monitor balança como depois de um tapa e fica tonto. Parado quando
/// invisível ou quando o Windows está com as animações desligadas.
/// </summary>
public partial class MascotIcon : UserControl
{
    // Tamanho das imagens e centro de cada olho nelas: as contas de posição são feitas nesse espaço.
    private const double ArtWidth = 600;
    private const double ArtHeight = 439;
    private static readonly Point LeftEyeCenter = new(213, 192);
    private static readonly Point RightEyeCenter = new(387, 192);

    // Deslocamento máximo dos olhos, nas coordenadas das imagens; o monitor anda uma fração disso.
    private const double EyeRangeX = 41;
    private const double EyeRangeY = 30;
    private const double BodyParallax = 0.2;

    // Distância do mouse (em DIPs) em que o olhar chega à metade do alcance: perto do ícone ele responde mais.
    private const double Reach = 260;

    // Raio (nas coordenadas das imagens) em volta do meio dos olhos em que eles vão ficando vesgos.
    private const double CrossRadius = 250;

    // Quanto maior, mais rápido o olhar alcança o alvo (por segundo). Tonto, os olhos giram mais soltos.
    private const double Smoothing = 4.5;
    private const double DizzySmoothing = 16;

    // Depois do tapa: quanto tempo os olhos ficam girando, e quantas voltas por segundo.
    private static readonly TimeSpan DizzyDuration = TimeSpan.FromSeconds(1.8);
    private const double DizzyTurnsPerSecond = 1.6;

    private static readonly IEasingFunction EaseIn = Frozen(new SineEase { EasingMode = EasingMode.EaseIn });
    private static readonly IEasingFunction EaseOut = Frozen(new SineEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseInOut = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });

    private readonly DispatcherTimer _blinkTimer = new();
    private readonly Random _random = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Window? _window;
    private Point? _mouse;
    private Vector _left;
    private Vector _right;
    private TimeSpan? _lastFrame;
    private TimeSpan? _slappedAt;
    private bool _animating;

    public MascotIcon()
    {
        InitializeComponent();

        _blinkTimer.Tick += (_, _) =>
        {
            if (!IsDizzy)
            {
                Blink();
            }

            ScheduleBlink();
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateActive();
    }

    private static bool Enabled => SystemParameters.ClientAreaAnimation;

    private bool IsDizzy => _slappedAt is { } at && _clock.Elapsed - at < DizzyDuration;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.PreviewMouseMove += OnWindowMouseMove;
            _window.MouseLeave += OnWindowMouseLeave;
        }

        UpdateActive();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.PreviewMouseMove -= OnWindowMouseMove;
            _window.MouseLeave -= OnWindowMouseLeave;
            _window = null;
        }

        UpdateActive();
    }

    private void UpdateActive()
    {
        if (IsVisible && _window is not null && Enabled)
        {
            if (!_blinkTimer.IsEnabled)
            {
                ScheduleBlink();
            }

            return;
        }

        _blinkTimer.Stop();
        StopAnimating();
        _mouse = null;
        _slappedAt = null;
        _left = _right = default;
        Apply();
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsVisible || !Enabled)
        {
            return;
        }

        _mouse = e.GetPosition(this);
        StartAnimating();
    }

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        _mouse = null;
        StartAnimating();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!Enabled)
        {
            return;
        }

        e.Handled = true;
        _slappedAt = _clock.Elapsed;
        Shake();
        Squint();
        StartAnimating();
    }

    #region Olhar

    private void StartAnimating()
    {
        if (_animating)
        {
            return;
        }

        _animating = true;
        _lastFrame = null;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimating()
    {
        if (!_animating)
        {
            return;
        }

        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        var elapsed = _lastFrame is { } last ? (now - last).TotalSeconds : 0;
        _lastFrame = now;
        if (elapsed <= 0)
        {
            return;
        }

        var dizzy = IsDizzy;
        var (left, right) = dizzy ? DizzyGaze() : Gaze();

        // Aproximação exponencial: igual em qualquer taxa de quadros, e desacelera ao chegar.
        var step = 1 - Math.Exp(-(dizzy ? DizzySmoothing : Smoothing) * Math.Min(elapsed, 0.1));
        _left += (left - _left) * step;
        _right += (right - _right) * step;
        if (!dizzy && (left - _left).Length < 0.0005 && (right - _right).Length < 0.0005)
        {
            _left = left;
            _right = right;
            StopAnimating();
        }

        Apply();
    }

    /// <summary>
    /// Para onde cada olho olha, de -1 a 1 em cada eixo. Longe, os dois olham juntos na direção do mouse; perto do
    /// meio dos olhos, cada um vira para dentro, e no centro eles ficam vesgos.
    /// </summary>
    private (Vector Left, Vector Right) Gaze()
    {
        if (_mouse is not { } mouse)
        {
            return default;
        }

        var scale = Math.Min(ActualWidth / ArtWidth, ActualHeight / ArtHeight);
        if (scale <= 0)
        {
            return default;
        }

        var origin = new Point((ActualWidth - ArtWidth * scale) / 2, (ActualHeight - ArtHeight * scale) / 2);
        Point ToLocal(Point art) => new(origin.X + art.X * scale, origin.Y + art.Y * scale);

        // Intensidade que cresce com a distância e satura no alcance máximo.
        var offset = mouse - ToLocal(new Point(ArtWidth / 2, ArtHeight / 2));
        var distance = offset.Length;
        var together = distance > 0 ? offset / distance * (distance / (distance + Reach)) : default;

        var middle = mouse - ToLocal(new Point((LeftEyeCenter.X + RightEyeCenter.X) / 2, LeftEyeCenter.Y));
        var radius = CrossRadius * scale;
        var cross = SmoothStep(1 - middle.Length / radius);
        var lookY = Math.Clamp(middle.Y / radius, -1, 1);

        return (
            together + (new Vector(1, lookY) - together) * cross,
            together + (new Vector(-1, lookY) - together) * cross);
    }

    /// <summary>Tontos, os olhos giram em sentidos opostos, em círculos que vão diminuindo até parar.</summary>
    private (Vector Left, Vector Right) DizzyGaze()
    {
        var progress = (_clock.Elapsed - _slappedAt!.Value) / DizzyDuration;
        var radius = 0.9 * Math.Pow(1 - progress, 0.7);
        var angle = progress * DizzyDuration.TotalSeconds * DizzyTurnsPerSecond * 2 * Math.PI;
        return (
            new Vector(Math.Cos(angle), Math.Sin(angle)) * radius,
            new Vector(-Math.Cos(angle), Math.Sin(angle)) * radius);
    }

    private void Apply()
    {
        LeftEyeShift.X = _left.X * EyeRangeX;
        LeftEyeShift.Y = _left.Y * EyeRangeY;
        RightEyeShift.X = _right.X * EyeRangeX;
        RightEyeShift.Y = _right.Y * EyeRangeY;

        var average = (_left + _right) / 2;
        BodyShift.X = average.X * EyeRangeX * BodyParallax;
        BodyShift.Y = average.Y * EyeRangeY * BodyParallax;
    }

    private static double SmoothStep(double value)
    {
        var x = Math.Clamp(value, 0, 1);
        return x * x * (3 - 2 * x);
    }

    #endregion

    #region Piscar

    private void ScheduleBlink()
    {
        _blinkTimer.Interval = TimeSpan.FromSeconds(2.5 + _random.NextDouble() * 4.5);
        _blinkTimer.Start();
    }

    private void Blink()
    {
        // De vez em quando, uma piscada dupla.
        var twice = _random.NextDouble() < 0.2;
        var blink = new DoubleAnimationUsingKeyFrames();
        AddBlink(blink, TimeSpan.Zero);
        if (twice)
        {
            AddBlink(blink, TimeSpan.FromMilliseconds(260));
        }

        blink.Freeze();
        LeftEyelid.BeginAnimation(ScaleTransform.ScaleYProperty, blink);
        RightEyelid.BeginAnimation(ScaleTransform.ScaleYProperty, blink);
    }

    private static void AddBlink(DoubleAnimationUsingKeyFrames animation, TimeSpan start)
    {
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(start)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.08, KeyTime.FromTimeSpan(start + TimeSpan.FromMilliseconds(75)), EaseIn));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(start + TimeSpan.FromMilliseconds(195)), EaseOut));
    }

    #endregion

    #region Tapa

    /// <summary>
    /// O monitor pula um pouco com o impacto, assenta e balança para os lados sobre o pé (inclinado, com a base
    /// parada), cada vez menos. Os valores são (milissegundos, valor), nas coordenadas das imagens e em graus.
    /// </summary>
    private void Shake()
    {
        SlapTilt.BeginAnimation(SkewTransform.AngleXProperty, Keyframes(
            (0, 0), (45, -7), (140, 5.5), (250, -3.6), (360, 2.2), (470, -1.2), (580, 0.5), (700, 0)));
        SlapShift.BeginAnimation(TranslateTransform.YProperty, Keyframes(
            (0, 0), (60, -12), (150, 0), (220, -3.5), (290, 0)));
    }

    /// <summary>Tonto, um olho se aperta e o outro se arregala; os dois voltam ao normal quando passa.</summary>
    private void Squint()
    {
        var end = DizzyDuration.TotalMilliseconds;
        var squeeze = Keyframes((0, 1), (120, 0.7), (end - 300, 0.74), (end, 1));
        var widen = Keyframes((0, 1), (120, 1.12), (end - 300, 1.1), (end, 1));
        LeftSquint.BeginAnimation(ScaleTransform.ScaleYProperty, squeeze);
        RightSquint.BeginAnimation(ScaleTransform.ScaleXProperty, widen);
        RightSquint.BeginAnimation(ScaleTransform.ScaleYProperty, widen);
    }

    private static DoubleAnimationUsingKeyFrames Keyframes(params (double Milliseconds, double Value)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        foreach (var (milliseconds, value) in frames)
        {
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)), EaseInOut));
        }

        animation.Freeze();
        return animation;
    }

    #endregion

    private static IEasingFunction Frozen(EasingFunctionBase easing)
    {
        easing.Freeze();
        return easing;
    }
}
