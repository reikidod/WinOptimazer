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
        Opacity = SystemParameters.ClientAreaAnimation ? 0 : 1;
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
        if (!SystemParameters.ClientAreaAnimation) return;
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
                SetHeader("#3D6B4F", "#2A4A38");
                break;
            case DialogKind.Warning:
                IconText.Text = "⚠";
                SubtitleText.Text = subtitle ?? "Проверьте перед продолжением";
                SetHeader("#6B5A2E", "#4A3E20");
                break;
            case DialogKind.Success:
                IconText.Text = "✓";
                SubtitleText.Text = subtitle ?? "Готово";
                SetHeader("#2E5A45", "#1E3D30");
                break;
            case DialogKind.Danger:
                IconText.Text = "✕";
                SubtitleText.Text = subtitle ?? "Это действие может быть необратимым";
                SetHeader("#6B3A3A", "#4A2828");
                break;
            default:
                IconText.Text = "ℹ";
                SubtitleText.Text = subtitle ?? "WinOptimizer";
                SetHeader("#3A4A66", "#2A3344");
                break;
        }
    }

    private void SetHeader(string c0, string c1)
    {
        HeaderColor0.Color = (Color)ColorConverter.ConvertFromString(c0)!;
        HeaderColor1.Color = (Color)ColorConverter.ConvertFromString(c1)!;
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
