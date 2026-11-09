using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using WinOptimizer.Infrastructure;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinOptimizer.Views;

/// <summary>
/// Мягкая анимация интерфейса: появление страниц и карточек, плавная прокрутка колесом мыши.
/// Всё отключается, если в Windows выключены эффекты анимации.
/// </summary>
public static class Motion
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseOutSoft = new QuinticEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Анимации включены и в настройках приложения, и в Windows.</summary>
    public static bool IsEnabled => AppSettings.Current.Animations && SystemParameters.ClientAreaAnimation;

    private static bool Enabled => IsEnabled;

    // ---------- Появление страницы при переключении ----------

    public static readonly DependencyProperty PageTransitionProperty = DependencyProperty.RegisterAttached(
        "PageTransition", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPageTransitionChanged));

    public static bool GetPageTransition(DependencyObject d) => (bool)d.GetValue(PageTransitionProperty);
    public static void SetPageTransition(DependencyObject d, bool value) => d.SetValue(PageTransitionProperty, value);

    private static void OnPageTransitionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.IsVisibleChanged -= OnPageVisibleChanged;
        if ((bool)e.NewValue) el.IsVisibleChanged += OnPageVisibleChanged;
    }

    private static void OnPageVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is UIElement el && e.NewValue is true)
            PlayEnter(el, offsetY: 16, TimeSpan.FromMilliseconds(320), TimeSpan.Zero);
    }

    // ---------- Появление карточки/строки при создании ----------

    public static readonly DependencyProperty FadeInProperty = DependencyProperty.RegisterAttached(
        "FadeIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFadeInChanged));

    public static bool GetFadeIn(DependencyObject d) => (bool)d.GetValue(FadeInProperty);
    public static void SetFadeIn(DependencyObject d, bool value) => d.SetValue(FadeInProperty, value);

    private static void OnFadeInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.Loaded -= OnFadeInLoaded;
        if ((bool)e.NewValue) el.Loaded += OnFadeInLoaded;
    }

    private static void OnFadeInLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        // Каскад: соседние карточки в одном списке появляются с небольшой задержкой друг за другом
        var delay = TimeSpan.FromMilliseconds(Math.Min(SiblingIndex(el), 10) * 35);
        PlayEnter(el, offsetY: 10, TimeSpan.FromMilliseconds(380), delay);
    }

    private static int SiblingIndex(FrameworkElement el)
    {
        DependencyObject? child = el;
        while (child != null)
        {
            var parent = VisualTreeHelper.GetParent(child);
            if (parent is Panel panel && child is UIElement ui)
                return Math.Max(0, panel.Children.IndexOf(ui));
            child = parent;
        }

        return 0;
    }

    private static void PlayEnter(UIElement el, double offsetY, TimeSpan duration, TimeSpan delay)
    {
        if (!Enabled) return;

        if (el.RenderTransform is not TranslateTransform tt || tt.IsFrozen)
        {
            tt = new TranslateTransform();
            el.RenderTransform = tt;
        }

        // FillBehavior.Stop: после анимации свойство возвращается к базовому значению (1 и 0) и не «залипает»
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            BeginTime = delay,
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.Stop,
        });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY, 0, duration)
        {
            BeginTime = delay,
            EasingFunction = EaseOutSoft,
            FillBehavior = FillBehavior.Stop,
        });
    }

    // ---------- Карточка твика: выезд слева направо, затем появляется тумблер ----------

    private const double CardSlideOffset = 56;
    private const int CardCascadeStepMs = 45;
    private const int CardCascadeMaxIndex = 12;

    /// <summary>Задержка начала анимации карточки: соседи выезжают каскадом, но не дольше ~0,5 с.</summary>
    internal static int CascadeDelayMs(int index) => Math.Clamp(index, 0, CardCascadeMaxIndex) * CardCascadeStepMs;

    public static readonly DependencyProperty CardEntranceProperty = DependencyProperty.RegisterAttached(
        "CardEntrance", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnCardEntranceChanged));

    public static bool GetCardEntrance(DependencyObject d) => (bool)d.GetValue(CardEntranceProperty);
    public static void SetCardEntrance(DependencyObject d, bool value) => d.SetValue(CardEntranceProperty, value);

    /// <summary>Метка на тумблере внутри карточки: он скрыт и неактивен, пока карточка не выехала.</summary>
    public static readonly DependencyProperty RevealWithCardProperty = DependencyProperty.RegisterAttached(
        "RevealWithCard", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    public static bool GetRevealWithCard(DependencyObject d) => (bool)d.GetValue(RevealWithCardProperty);
    public static void SetRevealWithCard(DependencyObject d, bool value) => d.SetValue(RevealWithCardProperty, value);

    /// <summary>False, пока тумблер ещё появляется (по нему стиль делает тумблер неактивным). Без анимации всегда True.</summary>
    public static readonly DependencyProperty EntranceDoneProperty = DependencyProperty.RegisterAttached(
        "EntranceDone", typeof(bool), typeof(Motion), new PropertyMetadata(true));

    public static bool GetEntranceDone(DependencyObject d) => (bool)d.GetValue(EntranceDoneProperty);
    public static void SetEntranceDone(DependencyObject d, bool value) => d.SetValue(EntranceDoneProperty, value);

    private static readonly DependencyProperty EntrancePlayedProperty = DependencyProperty.RegisterAttached(
        "EntrancePlayed", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    private static void OnCardEntranceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.Loaded -= OnCardLoaded;
        el.IsVisibleChanged -= OnCardVisibleChanged;
        if (e.NewValue is not true) return;

        // Прячем сразу при создании, чтобы карточка не мелькнула до начала анимации
        if (Enabled) el.Opacity = 0;
        el.Loaded += OnCardLoaded;
    }

    private static void OnCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el) TryPlayCard(el);
    }

    private static void OnCardVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is FrameworkElement el) TryPlayCard(el);
    }

    private static void TryPlayCard(FrameworkElement el)
    {
        if ((bool)el.GetValue(EntrancePlayedProperty)) return;

        if (!Enabled)
        {
            el.SetValue(EntrancePlayedProperty, true);
            el.ClearValue(UIElement.OpacityProperty);
            return;
        }

        // Страница с твиками может быть скрыта: ждём, пока её откроют, чтобы анимация не прошла «за кадром»
        if (!el.IsVisible)
        {
            el.IsVisibleChanged -= OnCardVisibleChanged;
            el.IsVisibleChanged += OnCardVisibleChanged;
            return;
        }

        el.IsVisibleChanged -= OnCardVisibleChanged;
        el.SetValue(EntrancePlayedProperty, true);
        PlayCardEntrance(el);
    }

    private static void PlayCardEntrance(FrameworkElement el)
    {
        var delay = TimeSpan.FromMilliseconds(CascadeDelayMs(SiblingIndex(el)));

        // Тумблер: неактивен и невидим, пока карточка едет
        var toggle = FindRevealToggle(el);
        if (toggle is not null)
        {
            SetEntranceDone(toggle, false);
            toggle.Opacity = 0;
        }

        if (el.RenderTransform is not TranslateTransform tt || tt.IsFrozen)
        {
            tt = new TranslateTransform();
            el.RenderTransform = tt;
        }

        tt.X = -CardSlideOffset;

        // HoldEnd: до BeginTime и после анимации карточка не «мигает» — значения снимаем вручную в Completed
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
        {
            BeginTime = delay,
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.HoldEnd,
        };
        var move = new DoubleAnimation(-CardSlideOffset, 0, TimeSpan.FromMilliseconds(420))
        {
            BeginTime = delay,
            EasingFunction = EaseOutSoft,
            FillBehavior = FillBehavior.HoldEnd,
        };
        move.Completed += (_, _) =>
        {
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            tt.X = 0;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.ClearValue(UIElement.OpacityProperty);
            if (toggle is not null) RevealToggle(toggle);
        };

        el.BeginAnimation(UIElement.OpacityProperty, fade);
        tt.BeginAnimation(TranslateTransform.XProperty, move);
    }

    /// <summary>Тумблер проявляется «выключенным» (затемнён), потом становится активным.</summary>
    private static void RevealToggle(UIElement toggle)
    {
        const double disabledOpacity = 0.45;

        var appear = new DoubleAnimation(0, disabledOpacity, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.HoldEnd,
        };
        appear.Completed += (_, _) =>
        {
            toggle.BeginAnimation(UIElement.OpacityProperty, null);
            toggle.ClearValue(UIElement.OpacityProperty);
            SetEntranceDone(toggle, true); // дальше IsEnabled снова решает IsAvailable твика

            if (toggle is UIElement { IsEnabled: true })
            {
                toggle.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(disabledOpacity, 1, TimeSpan.FromMilliseconds(160))
                {
                    EasingFunction = EaseOut,
                    FillBehavior = FillBehavior.Stop,
                });
            }
        };
        toggle.BeginAnimation(UIElement.OpacityProperty, appear);
    }

    private static UIElement? FindRevealToggle(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is CheckBox box && GetRevealWithCard(box)) return box;
            if (FindRevealToggle(child) is { } found) return found;
        }

        return null;
    }

    // ---------- Плавная прокрутка колесом ----------

    public static readonly DependencyProperty SmoothScrollProperty = DependencyProperty.RegisterAttached(
        "SmoothScroll", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnSmoothScrollChanged));

    public static bool GetSmoothScroll(DependencyObject d) => (bool)d.GetValue(SmoothScrollProperty);
    public static void SetSmoothScroll(DependencyObject d, bool value) => d.SetValue(SmoothScrollProperty, value);

    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.RegisterAttached(
        "AnimatedOffset", typeof(double), typeof(Motion), new PropertyMetadata(0.0, OnAnimatedOffsetChanged));

    private static readonly DependencyProperty TargetOffsetProperty = DependencyProperty.RegisterAttached(
        "TargetOffset", typeof(double?), typeof(Motion), new PropertyMetadata(null));

    private static void OnSmoothScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        sv.PreviewMouseWheel -= OnWheel;
        if ((bool)e.NewValue) sv.PreviewMouseWheel += OnWheel;
    }

    private static void OnAnimatedOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv) sv.ScrollToVerticalOffset((double)e.NewValue);
    }

    private static void OnWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || !Enabled || sv.ScrollableHeight <= 0) return;
        if (e.Handled) return;

        // Прокручиваем сами: цель складывается из нескольких быстрых щелчков колеса, движение — одной кривой
        var current = sv.GetValue(TargetOffsetProperty) as double? ?? sv.VerticalOffset;
        var target = Math.Clamp(current - e.Delta * 0.9, 0, sv.ScrollableHeight);
        sv.SetValue(TargetOffsetProperty, target);

        var anim = new DoubleAnimation(sv.VerticalOffset, target, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.HoldEnd,
        };
        anim.Completed += (_, _) =>
        {
            // Если за это время пришёл новый щелчок колеса, цель уже другая — эта анимация устарела
            if (sv.GetValue(TargetOffsetProperty) is not double t || Math.Abs(t - target) > 0.01) return;
            sv.BeginAnimation(AnimatedOffsetProperty, null);
            sv.SetValue(AnimatedOffsetProperty, target);
            sv.SetValue(TargetOffsetProperty, null);
        };
        sv.BeginAnimation(AnimatedOffsetProperty, anim);
        e.Handled = true;
    }

    // ---------- Ползунок прокрутки: виден только во время прокрутки ----------

    public static readonly DependencyProperty AutoHideScrollBarProperty = DependencyProperty.RegisterAttached(
        "AutoHideScrollBar", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnAutoHideScrollBarChanged));

    public static bool GetAutoHideScrollBar(DependencyObject d) => (bool)d.GetValue(AutoHideScrollBarProperty);
    public static void SetAutoHideScrollBar(DependencyObject d, bool v) => d.SetValue(AutoHideScrollBarProperty, v);

    private static void OnAutoHideScrollBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv || e.NewValue is not true) return;
        sv.Loaded += (_, _) => HookAutoHide(sv);
    }

    private static void HookAutoHide(ScrollViewer sv)
    {
        if (sv.Tag is "autohide") return; // Loaded срабатывает повторно при переключении страниц
        sv.ApplyTemplate();
        if (sv.Template?.FindName("PART_VerticalScrollBar", sv) is not ScrollBar bar) return;
        sv.Tag = "autohide";

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (bar.IsMouseOver || bar.IsMouseCaptureWithin) { timer.Start(); return; }
            FadeBar(bar, 0, 0.35);
        };

        void Show()
        {
            FadeBar(bar, 1, 0.12);
            timer.Stop();
            timer.Start();
        }

        sv.ScrollChanged += (_, a) => { if (a.VerticalChange != 0) Show(); };
        sv.PreviewMouseWheel += (_, _) => Show();
        bar.MouseEnter += (_, _) => { FadeBar(bar, 1, 0.12); timer.Stop(); };
        bar.MouseLeave += (_, _) => { timer.Stop(); timer.Start(); };
    }

    private static void FadeBar(UIElement bar, double to, double seconds)
    {
        if (!Enabled) { bar.Opacity = to; return; }
        bar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, TimeSpan.FromSeconds(seconds)) { EasingFunction = EaseOut });
    }
}
