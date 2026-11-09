using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using WinOptimizer.Infrastructure;
using WinOptimizer.ViewModels;

namespace WinOptimizer.Views;

public partial class SystemInfoPage : UserControl
{
    private bool _copyFeedbackActive;

    public SystemInfoPage()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private async void OnCopyReport(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        try
        {
            Clipboard.SetText(vm.BuildInfoReport());
        }
        catch (ExternalException ex)
        {
            Logger.Error("Не удалось скопировать в буфер обмена", ex);
            ShowInfo("Не удалось скопировать", "Буфер обмена занят другой программой. Повторите попытку.");
            return;
        }

        if (sender is Button button && !_copyFeedbackActive)
        {
            _copyFeedbackActive = true;
            var original = button.Content;
            button.Content = "Скопировано";
            await Task.Delay(1500);
            button.Content = original;
            _copyFeedbackActive = false;
        }
    }

    private void OnSaveReport(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        if (vm.InfoSections.Count == 0)
        {
            ShowInfo("Нет данных", "Сведения о системе ещё не загружены. Нажмите «Обновить» и повторите.");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить характеристики ПК",
            Filter = "Текстовый файл (*.txt)|*.txt",
            FileName = $"pc-info-{DateTime.Now:yyyy-MM-dd}.txt",
            DefaultExt = ".txt",
            AddExtension = true,
        };

        var owner = Window.GetWindow(this);
        if (dialog.ShowDialog(owner) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, vm.BuildInfoReport(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Logger.Info($"Характеристики сохранены: {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Не удалось сохранить отчёт", ex);
            ShowInfo("Не удалось сохранить", ex.Message);
        }
    }

    private void ShowInfo(string title, string message)
    {
        var dialog = new DialogWindow(title, message, primary: "OK", secondary: null, cancel: string.Empty)
        {
            Owner = Window.GetWindow(this),
        };
        dialog.ShowDialog();
    }
}
