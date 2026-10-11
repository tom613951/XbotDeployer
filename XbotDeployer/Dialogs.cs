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

    /// <summary>
    /// 弹出美观的单行文本输入对话框
    /// </summary>
    public static string? Prompt(string promptText, string defaultText = "", string title = "重命名应用")
    {
        var owner = System.Windows.Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive) ?? System.Windows.Application.Current?.MainWindow;

        var win = new FluentWindow
        {
            Title = title,
            Width = 420,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = owner
        };

        var rootGrid = new System.Windows.Controls.Grid { Margin = new Thickness(20) };
        rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

        var lblPrompt = new Wpf.Ui.Controls.TextBlock
        {
            Text = promptText,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        System.Windows.Controls.Grid.SetRow(lblPrompt, 0);
        rootGrid.Children.Add(lblPrompt);

        var txtInput = new Wpf.Ui.Controls.TextBox
        {
            Text = defaultText,
            VerticalAlignment = VerticalAlignment.Center,
            ClearButtonEnabled = true
        };
        System.Windows.Controls.Grid.SetRow(txtInput, 1);
        rootGrid.Children.Add(txtInput);

        var btnPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        System.Windows.Controls.Grid.SetRow(btnPanel, 2);

        var btnCancel = new Wpf.Ui.Controls.Button
        {
            Content = "取消",
            Width = 80,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var btnOk = new Wpf.Ui.Controls.Button
        {
            Content = "确定",
            Appearance = ControlAppearance.Primary,
            Width = 80
        };

        string? result = null;

        btnOk.Click += (_, _) =>
        {
            result = txtInput.Text;
            win.DialogResult = true;
            win.Close();
        };

        btnCancel.Click += (_, _) =>
        {
            win.DialogResult = false;
            win.Close();
        };

        txtInput.KeyDown += (_, ke) =>
        {
            if (ke.Key == System.Windows.Input.Key.Enter)
            {
                result = txtInput.Text;
                win.DialogResult = true;
                win.Close();
            }
            else if (ke.Key == System.Windows.Input.Key.Escape)
            {
                win.DialogResult = false;
                win.Close();
            }
        };

        btnPanel.Children.Add(btnCancel);
        btnPanel.Children.Add(btnOk);
        rootGrid.Children.Add(btnPanel);

        win.Content = rootGrid;

        win.Loaded += (_, _) =>
        {
            txtInput.Focus();
            txtInput.SelectAll();
        };

        return win.ShowDialog() == true ? result : null;
    }

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
