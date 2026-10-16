using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ScreenCord.Views;

/// <summary>
/// O monitor do ícone com os olhos vivos: eles acompanham o mouse pela janela, com atraso suave e um leve paralaxe
/// (o monitor anda bem menos que os olhos), e piscam de tempos em tempos. Parado quando invisível ou quando o
/// Windows está com as animações desligadas.
/// </summary>
public partial class MascotIcon : UserControl
{
    // Deslocamento máximo dos olhos, nas coordenadas das imagens (600×439); o monitor anda uma fração disso.
    private const double EyeRangeX = 36;
    private const double EyeRangeY = 26;
    private const double BodyParallax = 0.2;

    // Distância do mouse (em DIPs) em que o olhar chega à metade do alcance: perto do ícone ele responde mais.
    private const double Reach = 260;

    // Quanto maior, mais rápido o olhar alcança o mouse (por segundo).
    private const double Smoothing = 4.5;

    private static readonly IEasingFunction EaseIn = Frozen(new SineEase { EasingMode = EasingMode.EaseIn });
    private static readonly IEasingFunction EaseOut = Frozen(new SineEase { EasingMode = EasingMode.EaseOut });

    private readonly DispatcherTimer _blinkTimer = new();
    private readonly Random _random = new();
    private Window? _window;
    private Vector _target;
    private Vector _current;
    private TimeSpan? _lastFrame;
    private bool _following;

    public MascotIcon()
    {
        InitializeComponent();

        _blinkTimer.Tick += (_, _) =>
        {
            Blink();
            ScheduleBlink();
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateActive();
    }

    private static bool Enabled => SystemParameters.ClientAreaAnimation;

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
        StopFollowing();
        _target = _current = default;
        Apply();
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsVisible || !Enabled)
        {
            return;
        }

        var offset = e.GetPosition(this) - new Point(ActualWidth / 2, ActualHeight / 2);
        var distance = offset.Length;

        // Direção do mouse, com intensidade que cresce com a distância e satura no alcance máximo.
        _target = distance > 0 ? offset / distance * (distance / (distance + Reach)) : default;
        StartFollowing();
    }

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        _target = default;
        StartFollowing();
    }

    private void StartFollowing()
    {
        if (_following)
        {
            return;
        }

        _following = true;
        _lastFrame = null;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopFollowing()
    {
        if (!_following)
        {
            return;
        }

        _following = false;
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

        // Aproximação exponencial: igual em qualquer taxa de quadros, e desacelera ao chegar.
        _current += (_target - _current) * (1 - Math.Exp(-Smoothing * Math.Min(elapsed, 0.1)));
        if ((_target - _current).Length < 0.0005)
        {
            _current = _target;
            StopFollowing();
        }

        Apply();
    }

    private void Apply()
    {
        var x = _current.X * EyeRangeX;
        var y = _current.Y * EyeRangeY;
        EyesShift.X = x;
        EyesShift.Y = y;
        BodyShift.X = x * BodyParallax;
        BodyShift.Y = y * BodyParallax;
    }

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

    private static IEasingFunction Frozen(EasingFunctionBase easing)
    {
        easing.Freeze();
        return easing;
    }
}
