using System.Linq;
using System.Windows;
using Wpf.Ui.Controls;
using UiMessageBox = Wpf.Ui.Controls.MessageBox;
using UiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace XbotDeployer;

public enum DeleteAppMode
{
    Cancel = 0,
    CloudAndLocal = 1,
    LocalOnly = 2
}

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
    /// 弹出包含删除模式选项的高级删除确认弹窗（支持“云端同步删除”与“仅清除本地缓存”）
    /// </summary>
    public static DeleteAppMode ConfirmDeleteWithOptions(string appName)
    {
        var owner = System.Windows.Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(w => w.IsActive) ?? System.Windows.Application.Current?.MainWindow;

        var win = new FluentWindow
        {
            Title = "删除应用确认",
            Width = 500,
            Height = 330,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = owner
        };

        var rootGrid = new System.Windows.Controls.Grid { Margin = new Thickness(24) };
        rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

        // 标题区
        var headerPanel = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        var lblTitle = new Wpf.Ui.Controls.TextBlock
        {
            Text = $"确定要删除应用【{appName}】吗？",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        var lblSubtitle = new Wpf.Ui.Controls.TextBlock
        {
            Text = "请选择本次操作的执行方式：",
            FontSize = 12,
            Foreground = System.Windows.Media.Brushes.Gray
        };
        headerPanel.Children.Add(lblTitle);
        headerPanel.Children.Add(lblSubtitle);
        System.Windows.Controls.Grid.SetRow(headerPanel, 0);
        rootGrid.Children.Add(headerPanel);

        // 选项区 (单选卡片)
        var optionsPanel = new System.Windows.Controls.StackPanel();

        // 选项 1: 仅清除本地缓存 (默认推荐选中，避免用户误删云端数据)
        var rbLocalOnly = new System.Windows.Controls.RadioButton
        {
            GroupName = "DeleteOption",
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 14)
        };
        var rb1Content = new System.Windows.Controls.StackPanel();
        rb1Content.Children.Add(new Wpf.Ui.Controls.TextBlock
        {
            Text = "仅清除本地缓存与文件 [默认推荐，防误删]",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13
        });
        rb1Content.Children.Add(new Wpf.Ui.Controls.TextBlock
        {
            Text = "仅删除本电脑上的本地代码目录以释放空间；保留影刀云端空间，可在影刀中随时重新下载。",
            FontSize = 11,
            Foreground = System.Windows.Media.Brushes.DarkGray,
            Margin = new Thickness(0, 2, 0, 0)
        });
        rbLocalOnly.Content = rb1Content;
        optionsPanel.Children.Add(rbLocalOnly);

        // 选项 2: 云端与本地同步彻底删除 (需用户主动手动勾选)
        var rbCloudAndLocal = new System.Windows.Controls.RadioButton
        {
            GroupName = "DeleteOption",
            IsChecked = false,
            Margin = new Thickness(0, 0, 0, 6)
        };
        var rb2Content = new System.Windows.Controls.StackPanel();
        rb2Content.Children.Add(new Wpf.Ui.Controls.TextBlock
        {
            Text = "彻底删除（本地与影刀云端同步销毁）[高危操作]",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = System.Windows.Media.Brushes.OrangeRed
        });
        rb2Content.Children.Add(new Wpf.Ui.Controls.TextBlock
        {
            Text = "彻底清理本地代码，并调用影刀官方云端网关彻底注销销毁该应用，防止重新同步。",
            FontSize = 11,
            Foreground = System.Windows.Media.Brushes.DarkGray,
            Margin = new Thickness(0, 2, 0, 0)
        });
        rbCloudAndLocal.Content = rb2Content;
        optionsPanel.Children.Add(rbCloudAndLocal);

        System.Windows.Controls.Grid.SetRow(optionsPanel, 1);
        rootGrid.Children.Add(optionsPanel);

        // 底部按钮栏
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
            Width = 90,
            Margin = new Thickness(0, 0, 10, 0)
        };

        var btnConfirm = new Wpf.Ui.Controls.Button
        {
            Content = "确认删除",
            Appearance = ControlAppearance.Danger,
            Width = 100
        };

        var selectedMode = DeleteAppMode.Cancel;

        btnConfirm.Click += (_, _) =>
        {
            selectedMode = rbCloudAndLocal.IsChecked == true
                ? DeleteAppMode.CloudAndLocal
                : DeleteAppMode.LocalOnly;
            win.DialogResult = true;
            win.Close();
        };

        btnCancel.Click += (_, _) =>
        {
            selectedMode = DeleteAppMode.Cancel;
            win.DialogResult = false;
            win.Close();
        };

        btnPanel.Children.Add(btnCancel);
        btnPanel.Children.Add(btnConfirm);
        rootGrid.Children.Add(btnPanel);

        win.Content = rootGrid;
        win.ShowDialog();

        return selectedMode;
    }

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
