namespace WinOptimizer.Views;

/// <summary>
/// Вторая половина страниц MainWindow (обслуживание, debloat, приложения, настройки).
/// Вынесено из MainWindow.xaml для удобства сопровождения; DataContext наследует от окна.
/// </summary>
public partial class MainWindowPages : System.Windows.Controls.UserControl
{
    public MainWindowPages()
    {
        InitializeComponent();
    }
}
