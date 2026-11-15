using System.Windows;

namespace Zephyron.Gui.Views;

/// <summary>Ввод пароля копии настроек; на сохранении просит повтор и длину не короче минимума.</summary>
public partial class PasswordDialog : Window
{
    private readonly bool _confirm;
    private readonly int _min;

    private PasswordDialog(string title, string hint, bool confirm, int min)
    {
        InitializeComponent();
        _confirm = confirm;
        _min = min;
        Head.Text = title;
        Hint.Text = hint;
        ConfirmRow.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(this, title);
        Loaded += (_, _) => First.Focus();
    }

    /// <summary>Введённый пароль; пуст, пока окно не подтверждено.</summary>
    public string Password { get; private set; } = string.Empty;

    /// <summary>Пароль или <c>null</c>, если отказались. Пароль нигде не пишется.</summary>
    public static string? Ask(Window? owner, string title, string hint, bool confirm, int min)
    {
        var dialog = new PasswordDialog(title, hint, confirm, min);

        if (owner is { IsVisible: true })
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var text = First.Password;

        if (_confirm && text.Length < _min)
            Say($"Пароль короче {_min} знаков.");
        else if (_confirm && text != Second.Password)
            Say("Пароли не совпадают.");
        else if (text.Length == 0)
            Say("Введите пароль.");
        else
        {
            Password = text;
            DialogResult = true;
        }
    }

    private void Say(string text)
    {
        Problem.Text = text;
        Problem.Visibility = Visibility.Visible;
    }
}
