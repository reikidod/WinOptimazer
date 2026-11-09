namespace WinOptimizer.Views;

/// <summary>
/// Страница «Настройки» (тема, фон, вкладки, кэш). Собрана в WinOptimizer.Settings.dll.
/// DataContext — MainViewModel окна (пробрасывается с хоста).
/// </summary>
public partial class SettingsPage : System.Windows.Controls.UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }
}
