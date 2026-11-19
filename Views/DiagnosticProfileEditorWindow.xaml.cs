using System.Text;
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
