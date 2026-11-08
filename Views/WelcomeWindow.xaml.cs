using System.Windows;
using System.Windows.Media.Animation;
using WinOptimizer.Infrastructure;

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

    public WelcomeWindow()
    {
        InitializeComponent();
        GreetingText.Text = Greetings[Random.Shared.Next(Greetings.Length)];
        Opacity = 0;
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.UseDarkTitleBar(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // Плавное появление
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, fadeIn);

        await Task.Delay(1800);

        // Плавное исчезновение
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        var tcs = new TaskCompletionSource();
        fadeOut.Completed += (_, _) => tcs.TrySetResult();
        BeginAnimation(OpacityProperty, fadeOut);
        await tcs.Task;

        Close();
    }
}
