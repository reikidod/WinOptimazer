using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using WinOptimizer.Services;

namespace WinOptimizer.Views;

public partial class DiagnosticConsoleWindow : Window
{
    private readonly DiagnosticToolKind _kind;
    private Process? _process;
    private string? _scriptPath;
    private bool _isClosing;

    public DiagnosticConsoleWindow(DiagnosticToolKind kind)
    {
        InitializeComponent();
        _kind = kind;
        TitleText.Text = kind == DiagnosticToolKind.CoreAffinity
            ? "Core Affinity · CPU / FPS / IRQ"
            : "Input Latency · HID / Polling";
        DescriptionText.Text = kind == DiagnosticToolKind.CoreAffinity
            ? "Интерактивный инструмент встроен в окно WinOptimizer. Доступны топология CPU, настройка устройств, FPS-тесты и восстановление."
            : "Операции запускаются из интерфейса приложения. Вывод, запросы ввода, HID-инспектор, профили и откат остаются в этом окне.";
        InputLatencyActions.Visibility = kind == DiagnosticToolKind.InputLatency
            ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            if (_kind == DiagnosticToolKind.CoreAffinity)
                StartScript(interactive: true, mode: null);
            else
                Append("Выберите операцию выше. Результаты и запросы сценария будут отображаться здесь.\r\n");
        };
        Closed += (_, _) => StopProcess();
    }

    private void RunMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string mode }) return;
        StartScript(interactive: false, mode);
    }

    private void StartScript(bool interactive, string? mode)
    {
        if (_process is { HasExited: false })
        {
            Append("\r\n[!] Сначала завершите текущую операцию.\r\n");
            return;
        }

        try
        {
            _scriptPath ??= DiagnosticScriptProvider.MaterializeScript(_kind);
            var powershell = Path.Combine(Environment.SystemDirectory,
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = powershell,
                WorkingDirectory = Path.GetDirectoryName(_scriptPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(_scriptPath);
            if (_kind == DiagnosticToolKind.InputLatency && !string.IsNullOrWhiteSpace(mode))
            {
                startInfo.ArgumentList.Add("-Mode");
                startInfo.ArgumentList.Add(mode);
            }

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null) Dispatcher.BeginInvoke(() => Append(args.Data + Environment.NewLine));
            };
            _process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null) Dispatcher.BeginInvoke(() => Append("[ошибка] " + args.Data + Environment.NewLine));
            };
            _process.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                StatusText.Text = $"Операция завершена · код {_process?.ExitCode}";
                InputBox.IsEnabled = false;
            });

            Append($"\r\n> Запуск: {(_kind == DiagnosticToolKind.CoreAffinity ? "CoreAffinity" : "InputLatency")} {(mode ?? "interactive")}\r\n");
            if (!_process.Start())
                throw new InvalidOperationException("Windows не запустила встроенный сценарий.");
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            InputBox.IsEnabled = true;
            InputBox.Focus();
            StatusText.Text = "Сценарий выполняется внутри WinOptimizer.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось запустить операцию.";
            Append("[ошибка] " + ex.Message + Environment.NewLine);
            Logger.Error("Ошибка встроенного диагностического сценария", ex);
        }
    }

    private void SendInput_Click(object sender, RoutedEventArgs e) => SendInput();
    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SendInput();
        e.Handled = true;
    }

    private void SendInput()
    {
        if (_process is not { HasExited: false }) return;
        try
        {
            var value = InputBox.Text;
            _process.StandardInput.WriteLine(value);
            _process.StandardInput.Flush();
            Append("> " + value + Environment.NewLine);
            InputBox.Clear();
        }
        catch (Exception ex)
        {
            Append("[ошибка ввода] " + ex.Message + Environment.NewLine);
        }
    }

    private void Append(string text)
    {
        OutputBox.AppendText(text);
        OutputBox.ScrollToEnd();
    }

    private void StopProcess()
    {
        _isClosing = true;
        try
        {
            if (_process is { HasExited: false })
            {
                _process.StandardInput.Close();
                _process.WaitForExit(1500);
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) { Logger.Error("Не удалось корректно закрыть диагностический сценарий", ex); }
        finally { _process?.Dispose(); _process = null; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
