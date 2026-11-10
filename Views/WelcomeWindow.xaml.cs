using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinOptimizer.Views;

public partial class WelcomeWindow : Window
{
    private static readonly string[] Greetings =
    [
        "Добро пожаловать",
        "С возвращением",
        "Рады вас видеть",
        "Готовы к оптимизации",
        "Привет! Начнём",
        "Система под контролем",
        "Добрый день",
        "Отличного дня",
        "Время навести порядок",
        "Windows станет быстрее",
        "Запускаем WinOptimizer",
        "Всё под рукой",
    ];

    // Общая длительность приветствия (включая fade in/out).
    private static readonly TimeSpan TotalDuration = TimeSpan.FromSeconds(6.23);
    private static readonly TimeSpan FadeInDuration = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(480);

    private static void AnimateDot(TranslateTransform shift, UIElement dot, int delayMs)
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var timing = new Func<DoubleAnimation, DoubleAnimation>(a =>
        {
            a.BeginTime = TimeSpan.FromMilliseconds(900 + delayMs);
            a.AutoReverse = true;
            a.RepeatBehavior = RepeatBehavior.Forever;
            a.EasingFunction = ease;
            return a;
        });
        // Подъём вверх и лёгкое затухание/возврат; пауза между циклами получается из AutoReverse на разных фазах.
        shift.BeginAnimation(TranslateTransform.YProperty, timing(new DoubleAnimation(0, -4, TimeSpan.FromMilliseconds(380))));
        dot.BeginAnimation(OpacityProperty, timing(new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(380))));
    }

    public WelcomeWindow()
    {
        InitializeComponent();
        GreetingText.Text = Greetings[Random.Shared.Next(Greetings.Length)];
        Opacity = 0;
        Loaded += OnLoaded;
    }

    private static DoubleAnimation Anim(double from, double to, int ms, int delayMs, IEasingFunction ease) =>
        new(from, to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            var outSoft = new QuinticEase { EasingMode = EasingMode.EaseOut };
            var outCubic = new CubicEase { EasingMode = EasingMode.EaseOut };

            // Поэтапное появление: окно → дуга поднимается → заголовок → приветствие → статус.
            BeginAnimation(OpacityProperty, Anim(0, 1, 500, 0, outCubic));
            ArcLayer.BeginAnimation(OpacityProperty, Anim(0, 1, 1100, 80, outCubic));
            ArcShift.BeginAnimation(TranslateTransform.YProperty, Anim(60, 0, 1500, 0, outSoft));
            TitleText.BeginAnimation(OpacityProperty, Anim(0, 1, 700, 250, outCubic));
            GreetingText.BeginAnimation(OpacityProperty, Anim(0, 1, 800, 380, outCubic));
            GreetingShift.BeginAnimation(TranslateTransform.YProperty, Anim(22, 0, 1100, 380, outSoft));
            StatusPanel.BeginAnimation(OpacityProperty, Anim(0, 1, 700, 900, outCubic));

            // Точки статуса подпрыгивают по очереди, цикл 1.2 с.
            AnimateDot(Dot1Shift, Dot1, 0);
            AnimateDot(Dot2Shift, Dot2, 160);
            AnimateDot(Dot3Shift, Dot3, 320);

            // Свечение дуги «дышит», пока окно на экране.
            var breathe = new DoubleAnimation(0.25, 0.7, TimeSpan.FromMilliseconds(1800))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            ArcGlow.BeginAnimation(OpacityProperty, breathe);

            // Блик бежит по главной линии дуги слева направо с паузой между проходами (диапазон шире видимой части).
            ShimmerShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(2600))
                {
                    BeginTime = TimeSpan.FromMilliseconds(1100),
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });

            var hold = TotalDuration - FadeOutDuration;
            if (hold < TimeSpan.Zero)
                hold = TimeSpan.Zero;

            // Полоса прогресса плавно заполняется ровно до момента закрытия.
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0, 1, hold - TimeSpan.FromMilliseconds(900))
                {
                    BeginTime = TimeSpan.FromMilliseconds(900),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    FillBehavior = FillBehavior.HoldEnd,
                });

            await Task.Delay(hold);

            var fadeOut = new DoubleAnimation(1, 0, FadeOutDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            var tcs = new TaskCompletionSource();
            fadeOut.Completed += (_, _) => tcs.TrySetResult();
            BeginAnimation(OpacityProperty, fadeOut);
            await tcs.Task;
        }
        catch
        {
            // Не блокируем запуск основного окна из-за анимации
        }

        Close();
    }
}
