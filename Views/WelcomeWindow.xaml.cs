using System.Windows;
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
    private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(320);

    public WelcomeWindow()
    {
        InitializeComponent();
        GreetingText.Text = Greetings[Random.Shared.Next(Greetings.Length)];
        Opacity = 0;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            var fadeIn = new DoubleAnimation(0, 1, FadeInDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            BeginAnimation(OpacityProperty, fadeIn);

            var hold = TotalDuration - FadeInDuration - FadeOutDuration;
            if (hold < TimeSpan.Zero)
                hold = TimeSpan.Zero;
            await Task.Delay(hold);

            var fadeOut = new DoubleAnimation(1, 0, FadeOutDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
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
