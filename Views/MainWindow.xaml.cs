using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using WinOptimizer.Infrastructure;
using WinOptimizer.ViewModels;

namespace WinOptimizer.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _copyFeedbackActive;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
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
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            // Граница async void: исключение отсюда уронило бы процесс. Окно остаётся рабочим, ошибка — в журнале.
            Logger.Error("Ошибка начальной загрузки", ex);
            ShowInfo("Не удалось загрузить данные", $"{ex.Message}{Environment.NewLine}{Environment.NewLine}Подробности записаны в {AppPaths.LogFile}");
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        // Закрытие посреди применения твиков может оставить систему в промежуточном состоянии — спрашиваем.
        if (!e.Cancel && _viewModel.IsBusy)
        {
            var dialog = new DialogWindow(
                "Идёт применение изменений",
                "Операция ещё не завершена. Если закрыть программу сейчас, часть изменений может остаться незавершённой.",
                primary: "Подождать",
                secondary: null,
                cancel: "Всё равно закрыть")
            {
                Owner = this,
            };

            dialog.ShowDialog();
            e.Cancel = dialog.Choice == DialogChoice.Primary;
        }
    }

    private async void OnCopyReport(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.BuildInfoReport());
        }
        catch (ExternalException ex)
        {
            // Буфер обмена может быть занят другой программой.
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
        if (_viewModel.InfoSections.Count == 0)
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

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            // BOM нужен, чтобы Блокнот и другие программы правильно открыли кириллицу.
            File.WriteAllText(dialog.FileName, _viewModel.BuildInfoReport(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
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
            Owner = this,
        };
        dialog.ShowDialog();
    }
}
