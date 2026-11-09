using System.Windows;

namespace Zephyron.Gui.Views;

/// <summary>Вопрос «да/нет» в теме программы; замена MessageBox для подтверждений.</summary>
public partial class ConfirmDialog : Window
{
    private bool _answer;

    private ConfirmDialog(string title, string text, string yes, string no, bool danger, bool defaultNo)
    {
        InitializeComponent();
        Head.Text = title;
        Body.Text = text;
        Yes.Content = yes;
        No.Content = no;
        System.Windows.Automation.AutomationProperties.SetName(this, title);

        if (danger)
            Yes.Style = (Style)FindResource("Destructive");

        // Enter подтверждает то, что выбрано по умолчанию; у опасного действия это «нет».
        (defaultNo ? No : Yes).IsDefault = true;
        Loaded += (_, _) => (defaultNo ? No : Yes).Focus();
    }

    /// <summary><c>true</c> — «да»; <c>false</c> — «нет», Esc или крестик.</summary>
    public static bool Ask(Window? owner, string title, string text, string yes, string no, bool danger = false,
        bool defaultNo = false)
    {
        var dialog = new ConfirmDialog(title, text, yes, no, danger, defaultNo);

        // Скрытое в трей окно владельцем быть не может — иначе ShowDialog бросит исключение.
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, dialog))
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.ShowDialog();
        return dialog._answer;
    }

    /// <summary>Тот же вопрос в виде вызова MessageBox.Show: Yes/OK → да, остальное → нет.</summary>
    /// <remarks>Image: Warning/Error/Stop — опасное действие (красная кнопка).</remarks>
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons,
        MessageBoxImage image = MessageBoxImage.Question, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        bool yesNo = buttons == MessageBoxButton.YesNo;
        bool danger = image is MessageBoxImage.Warning or MessageBoxImage.Error;
        var title = caption == "Zephyron" ? "Подтвердите" : caption;
        bool yes = Ask(owner ?? Application.Current?.MainWindow, title, text, yesNo ? "Да" : "ОК", yesNo ? "Нет" : "Отмена",
            danger, defaultNo: defaultResult is MessageBoxResult.No or MessageBoxResult.Cancel);

        return yes
            ? (yesNo ? MessageBoxResult.Yes : MessageBoxResult.OK)
            : (yesNo ? MessageBoxResult.No : MessageBoxResult.Cancel);
    }

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons,
        MessageBoxImage image = MessageBoxImage.Question, MessageBoxResult defaultResult = MessageBoxResult.None) =>
        Show(null, text, caption, buttons, image, defaultResult);

    private void OnYes(object sender, RoutedEventArgs e)
    {
        _answer = true;
        Close();
    }

    private void OnNo(object sender, RoutedEventArgs e) => Close();
}
