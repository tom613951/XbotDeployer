using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using XbotDeployer.Core;

namespace XbotDeployer;

public partial class ContactsWindow : FluentWindow
{
    private readonly ContactsDb _db;

    public ContactsWindow(ContactsDb db)
    {
        InitializeComponent();
        _db = db;
        try
        {
            SystemThemeWatcher.Watch(this);
        }
        catch
        {
            // Theme watching is optional.
        }

        LoadData();
    }

    private void LoadData()
    {
        try
        {
            GridContacts.ItemsSource = null;
            GridContacts.ItemsSource = _db.GetAll();
        }
        catch (Exception ex)
        {
            Dialogs.Error($"读取联系人失败：{ex.Message}");
        }
    }

    private void GridContacts_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GridContacts.SelectedItem is ContactItem item)
        {
            TxtUser.Text = item.Username;
            TxtPwd.Password = item.Password;
            TxtRemark.Text = item.Remark;
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var user = TxtUser.Text.Trim();
        var password = TxtPwd.Password;
        var remark = TxtRemark.Text.Trim();

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
        {
            Dialogs.Warn("账号与密码不能为空。");
            return;
        }

        try
        {
            _db.AddOrUpdate(user, password, remark);
            LoadData();
            ClearInputs();
            Dialogs.Info($"接收人 [{user}] 已保存。");
        }
        catch (Exception ex)
        {
            Dialogs.Error($"保存联系人失败：{ex.Message}");
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (GridContacts.SelectedItem is not ContactItem item)
        {
            Dialogs.Warn("请先在列表中选中要删除的接收人。");
            return;
        }

        if (!Dialogs.Confirm($"确定要删除接收人 [{item.Username}] 吗？", "确认删除")) return;

        try
        {
            _db.Delete(item.Username);
            LoadData();
            ClearInputs();
        }
        catch (Exception ex)
        {
            Dialogs.Error($"删除联系人失败：{ex.Message}");
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        ClearInputs();
    }

    private void ClearInputs()
    {
        TxtUser.Clear();
        TxtPwd.Clear();
        TxtRemark.Clear();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
