using System.Text;
using System.Windows;
using System.Text.Json;
using WinOptimizer.Services;

namespace WinOptimizer.Views;

public partial class DiagnosticProfileEditorWindow : Window
{
    private readonly string _profilePath;

    public DiagnosticProfileEditorWindow(string profilePath)
    {
        InitializeComponent();
        _profilePath = profilePath;
        EnsureTrustedPath();
        ProfileText.Text = File.ReadAllText(_profilePath, Encoding.UTF8);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string? temporaryPath = null;
        try
        {
            EnsureTrustedPath();
            DiagnosticProfileValidator.Validate(ProfileText.Text);

            if (DiagnosticProfileValidator.HasCustomCommands(ProfileText.Text))
            {
                using var document = JsonDocument.Parse(ProfileText.Text);
                var autoApply = document.RootElement.TryGetProperty("AutoApplyOnServiceStart", out var setting) &&
                    setting.ValueKind == JsonValueKind.True;
                var warning = "Профиль содержит пользовательскую команду. Она может запускаться через cmd.exe с правами администратора при применении твика.";
                if (autoApply)
                    warning += "\\n\\nТакже включён автоматический повтор сохранённых настроек при запуске службы.";
                warning += "\\n\\nСохраняйте профиль только если полностью доверяете каждой команде. Продолжить?";
                if (MessageBox.Show(warning, "Проверка безопасности профиля",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
            }

            var backupPath = _profilePath + ".bak";
            if (File.Exists(_profilePath))
                File.Copy(_profilePath, backupPath, overwrite: true);

            temporaryPath = _profilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporaryPath, ProfileText.Text, new UTF8Encoding(true));
            File.Move(temporaryPath, _profilePath, overwrite: true);
            StatusText.Text = "Профиль проверен и сохранён. Предыдущая версия: profile.json.bak";
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось сохранить: " + ex.Message;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var backupPath = _profilePath + ".bak";
        if (!File.Exists(backupPath))
        {
            StatusText.Text = "Резервная копия ещё не создана.";
            return;
        }

        string? temporaryPath = null;
        try
        {
            EnsureTrustedPath();
            var backup = File.ReadAllText(backupPath, Encoding.UTF8);
            DiagnosticProfileValidator.Validate(backup);
            if (MessageBox.Show("Заменить текущий профиль последней резервной копией?",
                    "Восстановление профиля", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            temporaryPath = _profilePath + "." + Guid.NewGuid().ToString("N") + ".restore";
            File.WriteAllText(temporaryPath, backup, new UTF8Encoding(true));
            File.Move(temporaryPath, _profilePath, overwrite: true);
            ProfileText.Text = backup;
            StatusText.Text = "Профиль восстановлен из profile.json.bak.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось восстановить копию: " + ex.Message;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    private void EnsureTrustedPath()
    {
        var directory = Path.GetDirectoryName(_profilePath)
            ?? throw new InvalidOperationException("Не удалось определить каталог профиля.");
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Отказ от доступа к каталогу профиля через reparse point.");
        if (File.Exists(_profilePath) &&
            (File.GetAttributes(_profilePath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Отказ от доступа к файлу профиля через reparse point.");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
