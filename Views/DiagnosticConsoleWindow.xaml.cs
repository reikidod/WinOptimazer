using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;

namespace WinOptimizer.Views;

public partial class DiagnosticConsoleWindow : Window
{
    private const int MaxOutputCharacters = 250_000;
    private readonly DiagnosticToolKind _kind;
    private Process? _process;
    private string? _scriptPath;
    private DateTimeOffset? _runStartedAt;
    private DateTimeOffset? _runFinishedAt;
    private string _runMode = "interactive";
    private int? _lastExitCode;
    private bool _runCancelled;

    public DiagnosticConsoleWindow(DiagnosticToolKind kind)
    {
        InitializeComponent();
        _kind = kind;
        TitleText.Text = kind == DiagnosticToolKind.CoreAffinity
            ? "Core Affinity · CPU / FPS / IRQ"
            : "Input Latency · HID / Polling";
        DescriptionText.Text = kind == DiagnosticToolKind.CoreAffinity
            ? "Интерактивный инструмент встроен в WinOptimizer. Доступны топология CPU, настройка устройств, FPS-тесты и восстановление."
            : "Операции запускаются из интерфейса приложения. Вывод, запросы ввода, HID-инспектор, профили и откат остаются в этом окне.";
        InputLatencyActions.Visibility = kind == DiagnosticToolKind.InputLatency
            ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            if (_kind == DiagnosticToolKind.CoreAffinity)
                StartScript(mode: null);
            else
                Append("Выберите операцию выше. Результаты и запросы сценария будут отображаться здесь.\r\n");
        };
        Closed += (_, _) => StopProcess(userRequested: false);
    }

    private void RunMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string mode }) return;
        StartScript(mode);
    }

    private void StartScript(string? mode)
    {
        if (_process is { HasExited: false })
        {
            Append("\r\n[!] Сначала остановите текущую операцию.\r\n");
            return;
        }

        _process?.Dispose();
        _process = null;

        try
        {
            _scriptPath ??= DiagnosticScriptProvider.MaterializeScript(_kind);
            var powershell = Path.Combine(Environment.SystemDirectory,
                "WindowsPowerShell", "v1.0", "powershell.exe");

            var scriptLiteral = "'" + _scriptPath.Replace("'", "''") + "'";
            var modeArg = (_kind == DiagnosticToolKind.InputLatency && !string.IsNullOrWhiteSpace(mode))
                ? " -Mode " + mode
                : "";
            var bootstrap =
                "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " +
                "[Console]::InputEncoding = [Text.Encoding]::UTF8; " +
                "$OutputEncoding = [Console]::OutputEncoding; " +
                "try { chcp 65001 | Out-Null } catch {}; " +
                "& " + scriptLiteral + modeArg;

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
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false)
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(bootstrap);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                    PostToUi(() => { if (ReferenceEquals(_process, process)) Append(args.Data + Environment.NewLine); });
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                    PostToUi(() => { if (ReferenceEquals(_process, process)) Append("[ошибка] " + args.Data + Environment.NewLine); });
            };
            process.Exited += (_, _) => PostToUi(() =>
            {
                if (!ReferenceEquals(_process, process)) return;
                int exitCode;
                try { exitCode = process.ExitCode; }
                catch { exitCode = -1; }
                _lastExitCode = exitCode;
                _runFinishedAt = DateTimeOffset.Now;
                _runCancelled = false;
                StatusText.Text = exitCode == 0
                    ? "Операция завершена успешно."
                    : $"Операция завершена с ошибкой · код {exitCode}.";
                InputBox.IsEnabled = false;
                SendButton.IsEnabled = false;
                StopButton.IsEnabled = false;
                RunningIndicator.IsIndeterminate = false;
                RunningIndicator.Visibility = Visibility.Collapsed;
            });

            _runStartedAt = DateTimeOffset.Now;
            _runFinishedAt = null;
            _runMode = mode ?? "interactive";
            _lastExitCode = null;
            _runCancelled = false;
            _process = process;
            Append($"\r\n> Запуск: {(_kind == DiagnosticToolKind.CoreAffinity ? "CoreAffinity" : "InputLatency")} {_runMode}\r\n");
            if (!process.Start())
            {
                _process = null;
                process.Dispose();
                throw new InvalidOperationException("Windows не запустила встроенный сценарий.");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            InputBox.IsEnabled = true;
            SendButton.IsEnabled = true;
            StopButton.IsEnabled = true;
            RunningIndicator.Visibility = Visibility.Visible;
            RunningIndicator.IsIndeterminate = true;
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

    private void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        var profilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WinOptimizer", "InputLatency", "profile.json");
        if (!File.Exists(profilePath))
        {
            Append("Профиль ещё не создан. Сначала выполните «Сканировать HID» — сценарий создаст профиль по умолчанию.\r\n");
            return;
        }

        var editor = new DiagnosticProfileEditorWindow(profilePath) { Owner = this };
        editor.ShowDialog();
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

    private void CopyOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(OutputBox.Text))
            {
                StatusText.Text = "Журнал пуст.";
                return;
            }
            Clipboard.SetText(OutputBox.Text);
            StatusText.Text = "Вывод скопирован в буфер обмена.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось скопировать вывод.";
            Logger.Error("Не удалось скопировать журнал диагностики", ex);
        }
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OutputBox.Text))
        {
            StatusText.Text = "Нет данных для отчёта.";
            return;
        }

        try
        {
            var tool = _kind == DiagnosticToolKind.CoreAffinity ? "CoreAffinity" : "InputLatency";
            var report = new DiagnosticRunReport(
                tool,
                _runMode,
                _runStartedAt ?? DateTimeOffset.Now,
                _runFinishedAt,
                _lastExitCode,
                _runCancelled,
                OutputBox.Text);

            var dialog = new SaveFileDialog
            {
                Title = "Сохранить отчёт диагностики",
                Filter = "Отчёт WinOptimizer (*.json)|*.json",
                FileName = $"WinOptimizer-{tool}-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                AddExtension = true,
                DefaultExt = ".json",
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;

            File.WriteAllText(dialog.FileName, DiagnosticReportService.Serialize(report), new UTF8Encoding(true));
            StatusText.Text = "Структурированный отчёт сохранён.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось сохранить отчёт.";
            Logger.Error("Не удалось сохранить отчёт диагностики", ex);
        }
    }

    private void ClearOutput_Click(object sender, RoutedEventArgs e)
    {
        OutputBox.Clear();
        StatusText.Text = "Локальный вывод очищен. Файловый журнал не изменён.";
    }

    private void PostToUi(Action action)
    {
        try
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                Dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The window may be closing while redirected process streams are draining.
        }
    }

    private void Append(string text)
    {
        OutputBox.AppendText(text);
        if (OutputBox.Text.Length > MaxOutputCharacters)
        {
            var trimAt = OutputBox.Text.IndexOf('\n', OutputBox.Text.Length - MaxOutputCharacters / 2);
            if (trimAt < 0) trimAt = OutputBox.Text.Length - MaxOutputCharacters;
            OutputBox.Select(0, trimAt + 1);
            OutputBox.SelectedText = "[...ранний вывод сокращён...]\r\n";
        }
        OutputBox.ScrollToEnd();
    }

    private void StopProcess(bool userRequested = true)
    {
        var process = _process;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                try { process.StandardInput.Close(); } catch { }
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
                if (userRequested)
                {
                    _runFinishedAt = DateTimeOffset.Now;
                    _lastExitCode = null;
                    _runCancelled = true;
                    StatusText.Text = "Операция остановлена пользователем.";
                    Append("[!] Выполнение остановлено. Проверьте состояние системы перед повторным запуском.\r\n");
                }
            }
        }
        catch (Exception ex) { Logger.Error("Не удалось корректно остановить диагностический сценарий", ex); }
        finally
        {
            _process = null;
            process.Dispose();
            InputBox.IsEnabled = false;
            SendButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            RunningIndicator.IsIndeterminate = false;
            RunningIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopProcess();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
