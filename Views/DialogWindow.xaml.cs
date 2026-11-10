using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinOptimizer.Infrastructure;

namespace WinOptimizer.Views;

public enum DialogKind
{
    Default,
    RestorePoint,
    Warning,
    Success,
    Danger,
}

public partial class DialogWindow : Window
{
    public DialogWindow(string title, string message, string primary, string? secondary, string cancel,
        DialogKind kind = DialogKind.Default, string? subtitle = null)
    {
        InitializeComponent();

        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primary;

        SecondaryButton.Content = secondary;
        SecondaryButton.Visibility = string.IsNullOrEmpty(secondary) ? Visibility.Collapsed : Visibility.Visible;

        CancelButton.Content = cancel;
        CancelButton.Visibility = string.IsNullOrEmpty(cancel) ? Visibility.Collapsed : Visibility.Visible;

        ApplyKind(kind, subtitle);

        // Мягкое появление; Esc закрывает окно.
        Opacity = Motion.IsEnabled ? 0 : 1;
        Loaded += (_, _) => PlayOpenAnimation();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Choice = DialogChoice.Cancel;
            Close();
        };
    }

    private void PlayOpenAnimation()
    {
        if (!Motion.IsEnabled) return;
        var scale = new ScaleTransform(0.95, 0.95);
        Root.RenderTransform = scale;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(220);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.95, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.95, 1, duration) { EasingFunction = ease });
    }

    public DialogChoice Choice { get; private set; } = DialogChoice.Cancel;

    private void ApplyKind(DialogKind kind, string? subtitle)
    {
        switch (kind)
        {
            case DialogKind.RestorePoint:
                IconText.Text = "💾";
                SubtitleText.Text = subtitle ?? "Снимок системы перед изменениями";
                SetAccent("SuccessBrush");
                break;
            case DialogKind.Warning:
                IconText.Text = "⚠";
                SubtitleText.Text = subtitle ?? "Проверьте перед продолжением";
                SetAccent("WarnBrush");
                break;
            case DialogKind.Success:
                IconText.Text = "✓";
                SubtitleText.Text = subtitle ?? "Готово";
                SetAccent("SuccessBrush");
                break;
            case DialogKind.Danger:
                IconText.Text = "✕";
                SubtitleText.Text = subtitle ?? "Это действие может быть необратимым";
                SetAccent("DangerBrush");
                break;
            default:
                IconText.Text = "ℹ";
                SubtitleText.Text = subtitle ?? "WinOptimizer";
                SetAccent("AccentBrush");
                break;
        }
    }

    /// <summary>Цвет значка берётся из текущей темы (ThemeManager подменяет кисти), заголовок остаётся нейтральным.</summary>
    private void SetAccent(string brushKey)
    {
        if (TryFindResource(brushKey) is not SolidColorBrush brush) return;
        var c = brush.Color;
        IconBadge.Background = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
        IconBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B));
        IconText.Foreground = brush;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.UseDarkTitleBar(this);
    }

    private void OnPrimary(object sender, RoutedEventArgs e) => Close(DialogChoice.Primary);

    private void OnSecondary(object sender, RoutedEventArgs e) => Close(DialogChoice.Secondary);

    private void OnCancel(object sender, RoutedEventArgs e) => Close(DialogChoice.Cancel);

    private void OnDragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
            DragMove();
    }

    private void Close(DialogChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}
