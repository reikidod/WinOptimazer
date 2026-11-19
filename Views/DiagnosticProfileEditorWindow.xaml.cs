using System.Text;
using System.Text.Json;
using System.Windows;

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
        try
        {
            using var document = JsonDocument.Parse(ProfileText.Text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Корнем профиля должен быть JSON-объект.");

            var backupPath = _profilePath + ".bak";
            if (File.Exists(_profilePath))
                File.Copy(_profilePath, backupPath, overwrite: true);

            var temporaryPath = _profilePath + ".tmp";
            File.WriteAllText(temporaryPath, ProfileText.Text, new UTF8Encoding(true));
            File.Move(temporaryPath, _profilePath, overwrite: true);
            StatusText.Text = "Профиль сохранён. Предыдущая версия: profile.json.bak";
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось сохранить: " + ex.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
