using System.Linq;
using System.Windows;
using Wpf.Ui.Controls;
using UiMessageBox = Wpf.Ui.Controls.MessageBox;
using UiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace XbotDeployer;

/// <summary>
/// Shared WinUI 3 styled confirmation and notification dialogs.
/// </summary>
internal static class Dialogs
{
    public static void Info(string message, string title = "提示") =>
        Show(message, title, fatal: false, confirm: false);

    public static void Warn(string message, string title = "提示") =>
        Show(message, title, fatal: false, confirm: false);

    public static void Error(string message, string title = "错误") =>
        Show(message, title, fatal: true, confirm: false);

    public static bool Confirm(string message, string title = "确认操作") =>
        Show(message, title, fatal: false, confirm: true);

    private static bool Show(string message, string title, bool fatal, bool confirm)
    {
        var box = new UiMessageBox
        {
            Title = title,
            Content = message,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            PrimaryButtonText = confirm ? "确定" : "OK",
            PrimaryButtonAppearance = fatal
                ? ControlAppearance.Danger
                : ControlAppearance.Primary,
            IsSecondaryButtonEnabled = false,
            IsCloseButtonEnabled = confirm,
            CloseButtonText = confirm ? "取消" : string.Empty
        };

        var owner = System.Windows.Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive) ?? System.Windows.Application.Current?.MainWindow;

        if (owner is not null)
        {
            box.Owner = owner;
        }

        var result = box.ShowDialogAsync().GetAwaiter().GetResult();
        return confirm && result == UiMessageBoxResult.Primary;
    }
}
