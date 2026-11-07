using System.Windows;

namespace WinOptimizer.Views;

public enum DialogChoice
{
    Primary,
    Secondary,
    Cancel,
}

public interface IDialogService
{
    DialogChoice Ask(string title, string message, string primary, string? secondary = null, string cancel = "Отмена",
        DialogKind kind = DialogKind.Default, string? subtitle = null);

    void Info(string title, string message, DialogKind kind = DialogKind.Default);

    /// <summary>Да / Нет. True — нажата основная кнопка.</summary>
    bool Confirm(string title, string message, string yes = "Да", string no = "Нет",
        DialogKind kind = DialogKind.Warning);
}

public sealed class DialogService : IDialogService
{
    public DialogChoice Ask(string title, string message, string primary, string? secondary = null, string cancel = "Отмена",
        DialogKind kind = DialogKind.Default, string? subtitle = null)
    {
        var dialog = new DialogWindow(title, message, primary, secondary, cancel, kind, subtitle)
        {
            Owner = Application.Current?.MainWindow,
        };

        dialog.ShowDialog();
        return dialog.Choice;
    }

    public void Info(string title, string message, DialogKind kind = DialogKind.Default) =>
        Ask(title, message, "OK", cancel: string.Empty, kind: kind);

    public bool Confirm(string title, string message, string yes = "Да", string no = "Нет",
        DialogKind kind = DialogKind.Warning) =>
        Ask(title, message, yes, cancel: no, kind: kind) == DialogChoice.Primary;
}
