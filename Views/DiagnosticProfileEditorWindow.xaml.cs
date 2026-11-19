using System.Text;
using System.Text.Json;
using System.Windows;
using WinOptimizer.Services;

namespace WinOptimizer.Views;

public partial class DiagnosticProfileEditorWindow : Window
{
    private readonly string _profilePath;

    public DiagnosticProfileEditorWindow(string profilePath)
    {
        InitializeComponent();
        _profilePath = profilePath;
        ProfileText.Text = File.ReadAllText(_profilePath, Encoding.UTF8);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string? temporaryPath = null;
        try
        {
            DiagnosticProfileValidator.Validate(ProfileText.Text);

            if (DiagnosticProfileValidator.HasCustomCommands(ProfileText.Text))
            {
                using var document = JsonDocument.Parse(ProfileText.Text);
                var autoApply = document.RootElement.TryGetProperty("AutoApplyOnServiceStart", out var setting) &&
                    setting.ValueKind == JsonValueKind.True;
                var warning = "Профиль содержит пользовательскую команду. Она может запускаться через cmd.exe с правами администратора при применении твика.";
                if (autoApply)
                    warning += "\n\nТакже включён автоматический повтор сохранённых настроек при запуске службы.";
                warning += "\n\nСохраняйте профиль только если полностью доверяете каждой команде. Продолжить?";
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

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
