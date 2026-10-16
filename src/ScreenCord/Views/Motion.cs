using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ScreenCord.Views;

/// <summary>Como um elemento entra e sai de cena.</summary>
public enum MotionKind
{
    /// <summary>Só a opacidade.</summary>
    Fade,

    /// <summary>Opacidade e uma leve escala (popups, cartões).</summary>
    Pop,

    /// <summary>Opacidade e um pequeno deslize vindo de cima (avisos e erros).</summary>
    SlideDown,

    /// <summary>Opacidade e um pequeno deslize vindo de baixo.</summary>
    SlideUp,

    /// <summary>Fundo esmaecido que aparece aos poucos, com o cartão (primeiro filho) crescendo de leve.</summary>
    Overlay,

    /// <summary>Entra com um deslize curto, mas sai na hora: para trocar conteúdo no mesmo lugar sem saltos.</summary>
    Reveal,
}

/// <summary>
/// Animações curtas da interface: mostrar e esconder com transição (<see cref="IsShownProperty"/> no lugar de
/// <c>Visibility</c>), popups que também animam ao fechar (<see cref="IsOpenProperty"/>), itens de lista que entram
/// suavemente e janelas que aparecem e somem aos poucos. Tudo rápido (até ~200 ms) e desacelerando no fim, para a
/// interface responder na hora. Sem animações do Windows (acessibilidade), as mudanças são imediatas.
/// </summary>
public static class Motion
{
    private static readonly Duration ShowDuration = new(TimeSpan.FromMilliseconds(190));
    private static readonly Duration HideDuration = new(TimeSpan.FromMilliseconds(130));
    private const double SlideOffset = 8;
    private const double PopScale = 0.95;

    private static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseIn = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });

    private static bool Enabled => SystemParameters.ClientAreaAnimation;

    private static IEasingFunction Frozen(EasingFunctionBase easing)
    {
        easing.Freeze();
        return easing;
    }

    #region Mostrar e esconder

    /// <summary>Visível ou não, com transição. Use no lugar de um binding em <c>Visibility</c>.</summary>
    public static readonly DependencyProperty IsShownProperty = DependencyProperty.RegisterAttached(
        "IsShown", typeof(bool?), typeof(Motion), new PropertyMetadata(null, OnIsShownChanged));

    public static bool? GetIsShown(DependencyObject element) => (bool?)element.GetValue(IsShownProperty);

    public static void SetIsShown(DependencyObject element, bool? value) => element.SetValue(IsShownProperty, value);

    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(MotionKind), typeof(Motion), new PropertyMetadata(MotionKind.Fade));

    public static MotionKind GetKind(DependencyObject element) => (MotionKind)element.GetValue(KindProperty);

    public static void SetKind(DependencyObject element, MotionKind value) => element.SetValue(KindProperty, value);

    /// <summary>Contador de transições, para a conclusão de uma animação antiga não desfazer uma mais nova.</summary>
    private static readonly DependencyProperty GenerationProperty = DependencyProperty.RegisterAttached(
        "Generation", typeof(int), typeof(Motion), new PropertyMetadata(0));

    private static void OnIsShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not bool shown)
        {
            return;
        }

        // O primeiro valor (ainda sem nada na tela) só esconde de imediato; se for para mostrar, já entra animado.
        if (shown)
        {
            Show(element, GetKind(element));
        }
        else if (e.OldValue is null)
        {
            element.Visibility = Visibility.Collapsed;
        }
        else
        {
            Hide(element, GetKind(element));
        }
    }

    /// <summary>Mostra o elemento com a entrada de <paramref name="kind"/>.</summary>
    public static void Show(FrameworkElement element, MotionKind kind = MotionKind.Fade) =>
        Show(element, kind, startHidden: element.Visibility != Visibility.Visible);

    private static void Show(FrameworkElement element, MotionKind kind, bool startHidden)
    {
        NextGeneration(element);
        element.Visibility = Visibility.Visible;
        element.IsHitTestVisible = true;

        if (!Enabled)
        {
            Reset(element, kind);
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, Animation(startHidden ? 0 : null, 1, ShowDuration, EaseOut));
        AnimateTransform(element, kind, entering: true, startHidden);
    }

    /// <summary>
    /// Esconde o elemento com a saída de <paramref name="kind"/> e só então o recolhe (e chama <paramref name="hidden"/>,
    /// se nada o mostrou de novo nesse meio-tempo).
    /// </summary>
    public static void Hide(FrameworkElement element, MotionKind kind = MotionKind.Fade, Action? hidden = null)
    {
        var generation = NextGeneration(element);
        if (element.Visibility != Visibility.Visible)
        {
            hidden?.Invoke();
            return;
        }

        // Fora da tela (ou sem animações) não há o que mostrar: recolhe na hora.
        if (!Enabled || !element.IsVisible || kind == MotionKind.Reveal)
        {
            element.Visibility = Visibility.Collapsed;
            Reset(element, kind);
            hidden?.Invoke();
            return;
        }

        // Enquanto some, não recebe cliques (ex.: o fundo de um diálogo que está fechando).
        element.IsHitTestVisible = false;
        var fade = Animation(null, 0, HideDuration, EaseIn);
        fade.Completed += (_, _) =>
        {
            if ((int)element.GetValue(GenerationProperty) == generation)
            {
                element.Visibility = Visibility.Collapsed;
                hidden?.Invoke();
            }
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        AnimateTransform(element, kind, entering: false, startHidden: false);
    }

    private static int NextGeneration(DependencyObject element)
    {
        var generation = (int)element.GetValue(GenerationProperty) + 1;
        element.SetValue(GenerationProperty, generation);
        return generation;
    }

    private static void Reset(FrameworkElement element, MotionKind kind)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (TransformOf(TransformTarget(element, kind)) is { } transform)
        {
            transform.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            transform.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            transform.Translate.BeginAnimation(TranslateTransform.YProperty, null);
        }
    }

    private static void AnimateTransform(FrameworkElement element, MotionKind kind, bool entering, bool startHidden)
    {
        if (kind == MotionKind.Fade)
        {
            return;
        }

        var target = TransformTarget(element, kind);
        if (target is null || EnsureTransform(target, centered: kind == MotionKind.Overlay) is not { } transform)
        {
            return;
        }

        var duration = entering ? ShowDuration : HideDuration;
        var easing = entering ? EaseOut : EaseIn;

        switch (kind)
        {
            case MotionKind.Pop:
            case MotionKind.Overlay:
            {
                var hidden = kind == MotionKind.Overlay ? 0.96 : PopScale;
                double? from = entering && startHidden ? hidden : null;
                var to = entering ? 1 : hidden;
                transform.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, Animation(from, to, duration, easing));
                transform.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, Animation(from, to, duration, easing));
                break;
            }

            case MotionKind.SlideDown:
            case MotionKind.SlideUp:
            case MotionKind.Reveal:
            {
                var offset = kind == MotionKind.SlideUp ? SlideOffset : -SlideOffset;
                double? from = entering && startHidden ? offset : null;
                var to = entering ? 0 : offset;
                transform.Translate.BeginAnimation(TranslateTransform.YProperty, Animation(from, to, duration, easing));
                break;
            }
        }
    }

    /// <summary>No diálogo, quem cresce é o cartão (o primeiro filho); o fundo esmaecido só aparece aos poucos.</summary>
    private static UIElement? TransformTarget(FrameworkElement element, MotionKind kind) => kind switch
    {
        MotionKind.Overlay => element switch
        {
            Panel { Children.Count: > 0 } panel => panel.Children[0],
            Decorator { Child: { } child } => child,
            ContentControl { Content: UIElement content } => content,
            _ => null,
        },
        _ => element,
    };

    #endregion

    #region Transformações

    private sealed record MotionTransform(ScaleTransform Scale, TranslateTransform Translate);

    private static readonly DependencyProperty TransformProperty = DependencyProperty.RegisterAttached(
        "Transform", typeof(MotionTransform), typeof(Motion), new PropertyMetadata(null));

    private static MotionTransform? TransformOf(UIElement? element) =>
        element?.GetValue(TransformProperty) as MotionTransform;

    /// <summary>Cria (uma vez) a escala e o deslocamento animáveis; não mexe em quem já tem uma transformação própria.</summary>
    private static MotionTransform? EnsureTransform(UIElement element, bool centered)
    {
        if (TransformOf(element) is { } existing)
        {
            return existing;
        }

        if (element.RenderTransform is { } current && current != Transform.Identity)
        {
            return null;
        }

        var transform = new MotionTransform(new ScaleTransform(), new TranslateTransform());
        element.RenderTransform = new TransformGroup { Children = { transform.Scale, transform.Translate } };
        if (centered)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        element.SetValue(TransformProperty, transform);
        return transform;
    }

    private static DoubleAnimation Animation(double? from, double to, Duration duration, IEasingFunction easing) =>
        new() { From = from, To = to, Duration = duration, EasingFunction = easing };

    #endregion

    #region Itens que entram

    /// <summary>Ao entrar na tela (ex.: um item novo numa lista), aparece com um deslize curto.</summary>
    public static readonly DependencyProperty AppearOnLoadProperty = DependencyProperty.RegisterAttached(
        "AppearOnLoad", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnAppearOnLoadChanged));

    public static bool GetAppearOnLoad(DependencyObject element) => (bool)element.GetValue(AppearOnLoadProperty);

    public static void SetAppearOnLoad(DependencyObject element, bool value) => element.SetValue(AppearOnLoadProperty, value);

    private static void OnAppearOnLoadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnAppearLoaded;
        if (e.NewValue is true)
        {
            element.Loaded += OnAppearLoaded;
        }
    }

    private static void OnAppearLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && Enabled)
        {
            Show(element, MotionKind.Reveal, startHidden: true);
        }
    }

    #endregion

    #region Popups

    /// <summary>
    /// Abre e fecha o popup com animação (no lugar de <see cref="Popup.IsOpen"/>, nos dois sentidos). O popup deve ter
    /// <c>StaysOpen="True"</c>: o clique fora, o Esc e mover ou desativar a janela são tratados aqui, para também
    /// animarem o fechamento.
    /// </summary>
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.RegisterAttached(
        "IsOpen", typeof(bool), typeof(Motion),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsOpenChanged));

    public static bool GetIsOpen(DependencyObject popup) => (bool)popup.GetValue(IsOpenProperty);

    public static void SetIsOpen(DependencyObject popup, bool value) => popup.SetValue(IsOpenProperty, value);

    private static readonly DependencyProperty PopupHooksProperty = DependencyProperty.RegisterAttached(
        "PopupHooks", typeof(PopupHooks), typeof(Motion), new PropertyMetadata(null));

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Popup popup || popup.Child is not FrameworkElement content)
        {
            return;
        }

        if (e.NewValue is true)
        {
            popup.IsOpen = true;
            Hook(popup);
            Show(content, MotionKind.Pop);
            return;
        }

        Unhook(popup);
        if (popup.IsOpen)
        {
            Hide(content, MotionKind.Pop, () =>
            {
                if (!GetIsOpen(popup))
                {
                    popup.IsOpen = false;
                }
            });
        }
    }

    private static void RequestClose(Popup popup) => popup.SetCurrentValue(IsOpenProperty, false);

    private sealed class PopupHooks(Popup popup, Window window)
    {
        public Window Window { get; } = window;

        public void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Os cliques dentro do popup também passam pela janela (o popup é filho lógico dela): esses ficam.
            if (popup.Child?.IsMouseOver != true)
            {
                RequestClose(popup);
            }
        }

        public void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                RequestClose(popup);
                e.Handled = true;
            }
        }

        public void OnWindowChanged(object? sender, EventArgs e) => RequestClose(popup);

        public void OnUnloaded(object sender, RoutedEventArgs e) => RequestClose(popup);
    }

    private static void Hook(Popup popup)
    {
        if (popup.GetValue(PopupHooksProperty) is PopupHooks || Window.GetWindow(popup) is not { } window)
        {
            return;
        }

        var hooks = new PopupHooks(popup, window);
        window.PreviewMouseDown += hooks.OnMouseDown;
        window.PreviewKeyDown += hooks.OnKeyDown;
        window.Deactivated += hooks.OnWindowChanged;
        window.LocationChanged += hooks.OnWindowChanged;
        window.SizeChanged += hooks.OnWindowChanged;
        popup.Unloaded += hooks.OnUnloaded;
        popup.SetValue(PopupHooksProperty, hooks);
    }

    private static void Unhook(Popup popup)
    {
        if (popup.GetValue(PopupHooksProperty) is not PopupHooks hooks)
        {
            return;
        }

        var window = hooks.Window;
        window.PreviewMouseDown -= hooks.OnMouseDown;
        window.PreviewKeyDown -= hooks.OnKeyDown;
        window.Deactivated -= hooks.OnWindowChanged;
        window.LocationChanged -= hooks.OnWindowChanged;
        window.SizeChanged -= hooks.OnWindowChanged;
        popup.Unloaded -= hooks.OnUnloaded;
        popup.ClearValue(PopupHooksProperty);
    }

    #endregion

    #region Janelas

    /// <summary>
    /// A janela aparece e some aos poucos, com o conteúdo subindo de leve. Exige <c>AllowsTransparency="True"</c>
    /// (sem borda do Windows) para a opacidade da janela valer.
    /// </summary>
    public static readonly DependencyProperty WindowTransitionsProperty = DependencyProperty.RegisterAttached(
        "WindowTransitions", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnWindowTransitionsChanged));

    public static bool GetWindowTransitions(DependencyObject window) => (bool)window.GetValue(WindowTransitionsProperty);

    public static void SetWindowTransitions(DependencyObject window, bool value) => window.SetValue(WindowTransitionsProperty, value);

    private static readonly DependencyProperty IsClosingAnimatedProperty = DependencyProperty.RegisterAttached(
        "IsClosingAnimated", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    private static void OnWindowTransitionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true)
        {
            return;
        }

        window.SourceInitialized += (_, _) =>
        {
            if (Enabled)
            {
                window.Opacity = 0;
            }
        };
        window.Loaded += (_, _) =>
        {
            if (!Enabled)
            {
                return;
            }

            window.BeginAnimation(UIElement.OpacityProperty, Animation(0, 1, ShowDuration, EaseOut));
            if (window.Content is FrameworkElement content)
            {
                AnimateTransform(content, MotionKind.SlideUp, entering: true, startHidden: true);
            }
        };
        window.Closing += OnWindowClosing;
    }

    private static void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (sender is not Window window || e.Cancel || !Enabled || !window.IsVisible
            || (bool)window.GetValue(IsClosingAnimatedProperty))
        {
            return;
        }

        // Fecha de verdade só depois de sumir; o resultado do diálogo é guardado, pois cancelar o fechamento o apaga.
        e.Cancel = true;
        var result = window.DialogResult;
        window.SetValue(IsClosingAnimatedProperty, true);
        window.IsHitTestVisible = false;

        var fade = Animation(null, 0, HideDuration, EaseIn);
        fade.Completed += (_, _) =>
        {
            if (result is { } dialogResult)
            {
                window.DialogResult = dialogResult;
            }
            else
            {
                window.Close();
            }
        };
        window.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    #endregion
}
