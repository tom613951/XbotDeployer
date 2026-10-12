using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using XbotDeployer.Core;

namespace XbotDeployer;

public partial class MainWindow : FluentWindow
{
    private readonly ContactsDb _contactsDb;
    private readonly ShadowBotDeployer _deployer;

    private List<ShadowBotUser> _users = [];
    private List<ShadowBotApp> _allApps = [];
    private List<ShadowBotApp> _filteredApps = [];
    private List<ContactItem> _contacts = [];

    private bool _isMigrating;
    private bool _isScanning;
    private bool _suppressUserChange;
    private int _scanVersion;
    private string? _currentUserDir;
    private string? _customBaseDir;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            SystemThemeWatcher.Watch(this);
        }
        catch
        {
            // Theme watching is optional; a failure here must not block startup.
        }

        _contactsDb = new ContactsDb();
        _deployer = new ShadowBotDeployer();

        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ = LoadUsersAndApps();
        LoadContacts();
    }

    // ---------------------------------------------------------------- scanning

    private async Task LoadUsersAndApps(string? preferredUserDir = null)
    {
        LblAppSummary.Text = "正在扫描影刀账号...";

        try
        {
            _users = await Task.Run(() => Scanner.GetShadowBotUsers(_customBaseDir));

            _suppressUserChange = true;
            CmbUsers.ItemsSource = null;
            CmbUsers.ItemsSource = _users;

            if (_users.Count == 0)
            {
                CmbUsers.SelectedIndex = -1;
                _allApps = [];
                _currentUserDir = null;
                _suppressUserChange = false;
                FilterApps();
                LblAppSummary.Text = "未找到本地影刀账号";
                AppendLog("[WARN] 未在默认路径检测到 ShadowBot 用户数据目录");
                return;
            }

            // 优先选择指定的目录；若未指定或新用户刚登录，默认选中最新活跃的第一个用户
            var targetUser = !string.IsNullOrEmpty(preferredUserDir)
                ? _users.FirstOrDefault(u => string.Equals(u.UserDir, preferredUserDir, StringComparison.OrdinalIgnoreCase))
                : null;

            targetUser ??= _users[0];
            CmbUsers.SelectedItem = targetUser;
            _suppressUserChange = false;

            await RefreshAppsAsync(targetUser.UserDir);
            AppendLog($"[SUCCESS] 检测到 {_users.Count} 个影刀账号，当前聚焦: {targetUser.DisplayName}");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] 扫描影刀账号失败: {ex.Message}");
            LblAppSummary.Text = "扫描影刀账号失败";
        }
    }

    private void LoadContacts()
    {
        try
        {
            _contacts = _contactsDb.GetAll();
            CmbContacts.ItemsSource = null;
            CmbContacts.ItemsSource = _contacts;
            CmbContacts.DisplayMemberPath = "Username";
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] 读取常用联系人失败: {ex.Message}");
        }
    }

    private void CmbUsers_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUserChange) return;
        if (CmbUsers.SelectedItem is ShadowBotUser user)
        {
            _ = RefreshAppsAsync(user.UserDir);
        }
    }

    private async Task RefreshAppsAsync(string userDir)
    {
        // Ignore duplicate requests for the account already being scanned.
        if (_isScanning && string.Equals(_currentUserDir, userDir, StringComparison.OrdinalIgnoreCase)) return;

        var scanVersion = ++_scanVersion;
        _currentUserDir = userDir;
        _isScanning = true;
        LblAppSummary.Text = "正在扫描本地应用...";

        try
        {
            var apps = await Task.Run(() => Scanner.ScanLocalApps(userDir));
            if (scanVersion != _scanVersion) return;

            _allApps = apps;
            FilterApps();
            UpdateSelectedUserLabel(apps.Count);
            AppendLog($"已扫描到 {apps.Count} 个本地应用");
        }
        catch (Exception ex)
        {
            if (scanVersion == _scanVersion)
            {
                _allApps = [];
                FilterApps();
                AppendLog($"[ERROR] 扫描本地应用失败: {ex.Message}");
            }
        }
        finally
        {
            if (scanVersion == _scanVersion)
            {
                _isScanning = false;
            }
        }
    }

    private void UpdateSelectedUserLabel(int appCount)
    {
        if (CmbUsers.SelectedItem is ShadowBotUser current)
        {
            current.AppCount = appCount;
        }
    }

    private void FilterApps()
    {
        var keyword = TxtSearch.Text?.Trim() ?? "";
        var selected = _allApps.Where(a => a.IsSelected).Select(a => a.Uuid).ToHashSet(StringComparer.OrdinalIgnoreCase);

        _filteredApps = string.IsNullOrEmpty(keyword)
            ? _allApps.ToList()
            : _allApps.Where(a =>
                a.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                a.Uuid.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var app in _filteredApps)
        {
            app.IsSelected = selected.Contains(app.Uuid);
        }

        GridApps.ItemsSource = null;
        GridApps.ItemsSource = _filteredApps;

        ChkSelectAllHeader.IsChecked = _filteredApps.Count > 0 && _filteredApps.All(a => a.IsSelected);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var selected = _allApps.Count(a => a.IsSelected);
        LblAppSummary.Text = $"共 {_allApps.Count} 个应用（显示 {_filteredApps.Count} 个，已勾选 {selected} 个）";
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterApps();
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        AppendLog("[INFO] 正在刷新影刀账号与本地应用列表...");
        var currentDir = (CmbUsers.SelectedItem as ShadowBotUser)?.UserDir;
        await LoadUsersAndApps(currentDir);
    }

    private async void BtnCustomDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择非默认盘符（D盘/E盘等）的影刀安装目录或 users 数据目录"
        };

        if (dialog.ShowDialog() != true) return;

        var selected = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(selected) || !Directory.Exists(selected)) return;

        _customBaseDir = selected;
        AppendLog($"[INFO] 已手动指定影刀目录: {_customBaseDir}，正在重新扫描...");
        await LoadUsersAndApps();
    }

    private void SetBusy(bool busy)
    {
        GridApps.IsEnabled = !busy;
        BtnMigrate.IsEnabled = !busy;
        CmbUsers.IsEnabled = !busy;
    }

    // -------------------------------------------------------------- selection

    private void ChkSelectAllHeader_Click(object sender, RoutedEventArgs e)
    {
        var check = ChkSelectAllHeader.IsChecked == true;
        foreach (var app in _filteredApps)
        {
            app.IsSelected = check;
        }
        UpdateSummary();
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        ChkSelectAllHeader.IsChecked = true;
        ChkSelectAllHeader_Click(sender, e);
    }

    private void BtnInvertSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var app in _filteredApps)
        {
            app.IsSelected = !app.IsSelected;
        }
        ChkSelectAllHeader.IsChecked = null;
        UpdateSummary();
    }

    private List<ShadowBotApp> GetSelectedApps() => _allApps.Where(a => a.IsSelected).ToList();

    private void GridApps_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_isMigrating)
        {
            e.Handled = true;
            return;
        }

        // 自动选中鼠标右键所在的行
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridRow)
        {
            dep = VisualTreeHelper.GetParent(dep);
        }

        if (dep is DataGridRow row && row.Item is ShadowBotApp clickedApp)
        {
            GridApps.SelectedItem = clickedApp;
        }

        if (GridApps.SelectedItem is not ShadowBotApp)
        {
            e.Handled = true;
        }
    }

    private void GridApps_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (GridApps.SelectedItem is not ShadowBotApp app) return;
        ShowAppDetail(app);
    }

    private void MenuItem_Detail_Click(object sender, RoutedEventArgs e)
    {
        var app = (sender as FrameworkElement)?.DataContext as ShadowBotApp ?? GridApps.SelectedItem as ShadowBotApp;
        if (app != null) ShowAppDetail(app);
    }

    private void ShowAppDetail(ShadowBotApp app)
    {
        var detail = $"""
            应用名称: {app.Name}
            UUID: {app.Uuid}
            版本: {app.Version}
            流程数量: {app.FlowCount}
            文件大小: {app.SizeStr}
            修改时间: {app.MtimeStr}
            应用根目录: {app.AppDir}
            代码路径: {app.RobotDir}
            """;

        Dialogs.Info(detail, "应用详情");
    }

    private void MenuItem_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var app = (sender as FrameworkElement)?.DataContext as ShadowBotApp ?? GridApps.SelectedItem as ShadowBotApp;
        if (app == null) return;

        var targetDir = !string.IsNullOrEmpty(app.AppDir) && Directory.Exists(app.AppDir) ? app.AppDir : app.RobotDir;
        if (Directory.Exists(targetDir))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{targetDir}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Dialogs.Error($"无法打开目录: {ex.Message}", "打开目录失败");
            }
        }
        else
        {
            Dialogs.Warn($"本地目录不存在: {targetDir}");
        }
    }

    private void MenuItem_Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_isMigrating)
        {
            Dialogs.Warn("迁移任务正在进行中，无法重命名应用。");
            return;
        }

        var app = (sender as FrameworkElement)?.DataContext as ShadowBotApp ?? GridApps.SelectedItem as ShadowBotApp;
        if (app == null) return;

        var newName = Dialogs.Prompt("请输入新的应用名称:", app.Name, "重命名应用");
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == app.Name) return;

        newName = newName.Trim();
        var userDir = (CmbUsers.SelectedItem as ShadowBotUser)?.UserDir ?? _currentUserDir;
        if (string.IsNullOrEmpty(userDir))
        {
            Dialogs.Error("未获取到当前影刀用户目录。", "重命名失败");
            return;
        }

        var oldName = app.Name;
        var (ok, msg) = Scanner.RenameApp(userDir, app, newName);
        if (ok)
        {
            FilterApps();
            AppendLog($"[SUCCESS] 应用【{oldName}】已成功重命名为【{newName}】并在影刀中同步生效。");
            Dialogs.Info($"应用已重命名为【{newName}】！\n影刀客户端已实时同步。", "重命名成功");
        }
        else
        {
            AppendLog($"[ERROR] 重命名应用【{oldName}】失败: {msg}");
            Dialogs.Error(msg, "重命名失败");
        }
    }

    private void MenuItem_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_isMigrating)
        {
            Dialogs.Warn("迁移任务正在进行中，无法删除应用。");
            return;
        }

        var app = (sender as FrameworkElement)?.DataContext as ShadowBotApp ?? GridApps.SelectedItem as ShadowBotApp;
        if (app == null) return;

        var mode = Dialogs.ConfirmDeleteWithOptions(app.Name);
        if (mode == DeleteAppMode.Cancel) return;

        var deleteCloud = mode == DeleteAppMode.CloudAndLocal;

        var userDir = (CmbUsers.SelectedItem as ShadowBotUser)?.UserDir ?? _currentUserDir;
        if (string.IsNullOrEmpty(userDir))
        {
            Dialogs.Error("未获取到当前影刀用户目录。", "删除失败");
            return;
        }

        var deletedName = app.Name;
        var (ok, msg) = Scanner.DeleteApp(userDir, app, deleteCloud);
        if (ok)
        {
            _allApps.Remove(app);
            UpdateSelectedUserLabel(_allApps.Count);
            FilterApps();

            var actionDesc = deleteCloud ? "彻底删除（本地与云端同步）" : "清除本地缓存（云端已保留）";
            AppendLog($"[SUCCESS] 应用【{deletedName}】{actionDesc}: {msg}");
            Dialogs.Info(msg, "操作完成");
        }
        else
        {
            AppendLog($"[ERROR] 处理应用【{deletedName}】失败: {msg}");
            Dialogs.Error(msg, "操作失败");
        }
    }

    // ----------------------------------------------------------------- export

    private async void BtnExportZip_Click(object sender, RoutedEventArgs e)
    {
        var selectedApps = GetSelectedApps();
        if (selectedApps.Count == 0)
        {
            Dialogs.Warn("请先在列表中勾选要导出的应用。");
            return;
        }

        var dialog = new OpenFolderDialog { Title = "选择导出 Zip 保存目录" };
        if (dialog.ShowDialog() != true) return;

        var exportDir = dialog.FolderName;
        var encryptPy = ChkEncryptPython.IsChecked == true;

        AppendLog($"开始导出 {selectedApps.Count} 个应用至: {exportDir}");

        var success = 0;
        await Task.Run(() =>
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (var app in selectedApps)
            {
                try
                {
                    Dispatcher.BeginInvoke(() => AppendLog($"[INFO] 正在打包: {app.Name} ({app.Uuid})..."));

                    var cleanName = new string(app.Name.Where(c => !invalidChars.Contains(c)).ToArray()).Trim();
                    if (string.IsNullOrEmpty(cleanName)) cleanName = "xbot_app";
                    var shortUuid = app.Uuid.Length >= 8 ? app.Uuid[..8] : app.Uuid;
                    var outName = $"{cleanName}_{shortUuid}.zip";
                    var outPath = Path.Combine(exportDir, outName);

                    var (zipPath, md5Val, _, jsonPath) = Packager.BuildAppPackage(
                        robotDir: app.RobotDir,
                        encryptPython: encryptPy
                    );

                    if (File.Exists(outPath)) File.Delete(outPath);
                    File.Move(zipPath, outPath);

                    // 清理伴生临时目录
                    try
                    {
                        var tempDir = Path.GetDirectoryName(zipPath);
                        if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
                        {
                            Directory.Delete(tempDir, true);
                        }
                    }
                    catch { }

                    success++;
                    Dispatcher.BeginInvoke(() => AppendLog($"[SUCCESS] 成功导出: {outName} (MD5: {md5Val})"));
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(() => AppendLog($"[ERROR] 导出 [{app.Name}] 失败: {ex.Message}"));
                }
            }
        });

        AppendLog($"[SUCCESS] 导出完成: 成功 {success}/{selectedApps.Count} 个。");
        Dialogs.Info($"导出完成！成功导出 {success} 个应用包。", "完成");
    }

    // ---------------------------------------------------------------- contacts

    private void CmbContacts_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbContacts.SelectedItem is ContactItem contact)
        {
            TxtTargetUser.Text = contact.Username;
            TxtTargetPassword.Password = contact.Password;
        }
    }

    private void BtnManageContacts_Click(object sender, RoutedEventArgs e)
    {
        var window = new ContactsWindow(_contactsDb) { Owner = this };
        window.ShowDialog();
        LoadContacts();
    }

    // --------------------------------------------------------------- migration

    private async void BtnMigrate_Click(object sender, RoutedEventArgs e)
    {
        if (_isMigrating) return;

        var selectedApps = GetSelectedApps();
        if (selectedApps.Count == 0)
        {
            Dialogs.Warn("请先在列表中勾选要迁移的应用。");
            return;
        }

        var username = TxtTargetUser.Text.Trim();
        var password = TxtTargetPassword.Password;
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            Dialogs.Warn("请输入接收方影刀账号与密码。");
            return;
        }

        if (ChkRememberContact.IsChecked == true)
        {
            _contactsDb.AddOrUpdate(username, password, "自动保存");
            LoadContacts();
        }

        _isMigrating = true;
        SetBusy(true);
        ProgressBarMigration.Value = 0;
        ProgressBarMigration.Maximum = selectedApps.Count;

        var addSuffix = ChkAddSuffix.IsChecked == true;
        var encryptPy = ChkEncryptPython.IsChecked == true;

        try
        {
            AppendLog($"正在登录接收方账号 [{username}]...");

            var (loginOk, loginMsg, tokenRes) = await AuthService.LoginShadowBotAsync(username, password);
            if (!loginOk || tokenRes is null)
            {
                AppendLog($"[ERROR] 接收方账号登录失败: {loginMsg}");
                Dialogs.Error($"接收方登录失败：\n{loginMsg}", "登录失败");
                return;
            }

            var token = tokenRes.Value.TryGetProperty("access_token", out var tokenProp) ? tokenProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(token))
            {
                const string message = "登录响应中未包含 access_token。";
                AppendLog($"[ERROR] {message}");
                Dialogs.Error(message, "登录失败");
                return;
            }

            var userUuid = tokenRes.Value.TryGetProperty("userUuid", out var uuidProp) ? uuidProp.GetString() ?? "未知" : "未知";
            AppendLog($"[SUCCESS] 接收方账号登录成功 (UserUUID: {userUuid})");

            var total = selectedApps.Count;
            var successCount = 0;

            for (var i = 0; i < total; i++)
            {
                var app = selectedApps[i];
                var migratedName = addSuffix
                    ? $"{app.Name}_云迁_接收于{DateTime.Now:yyyy年MM月dd日 HH时mm分ss秒}"
                    : app.Name;

                AppendLog($"--- 正在处理 [{i + 1}/{total}] {app.Name} ---");

                var (ok, _) = await Task.Run(() => _deployer.DeploySingleAppAsync(
                    targetToken: token,
                    robotDir: app.RobotDir,
                    newAppName: migratedName,
                    encryptPython: encryptPy,
                    logCallback: message => Dispatcher.BeginInvoke(() => AppendLog(message))
                ));

                if (ok) successCount++;
                ProgressBarMigration.Value = i + 1;
            }

            var summary = $"迁移任务结束: 共 {total} 个应用，成功 {successCount} 个，失败 {total - successCount} 个";
            AppendLog($"[SUCCESS] {summary}");
            Dialogs.Info(summary, "任务完成");
        }
        finally
        {
            _isMigrating = false;
            SetBusy(false);
        }
    }

    // ------------------------------------------------------------------- misc

    private void BtnCleanCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var count = 0;
            long freed = 0;
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "xbot_*"))
            {
                try
                {
                    foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        freed += new FileInfo(file).Length;
                    }
                    Directory.Delete(dir, true);
                    count++;
                }
                catch
                {
                    // A locked directory is skipped and reported through the aggregate count.
                }
            }

            var message = $"已清理 {count} 个缓存目录，释放约 {freed / 1024.0 / 1024.0:F2} MB";
            AppendLog($"[SUCCESS] {message}");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] 清理临时缓存异常: {ex.Message}");
        }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        RtfLog.Document.Blocks.Clear();
    }

    private void AppendLog(string message)
    {
        var brush = Brushes.LightGray;

        if (message.Contains("[ERROR]"))
        {
            brush = new SolidColorBrush(Color.FromRgb(255, 99, 99));
        }
        else if (message.Contains("[SUCCESS]"))
        {
            brush = new SolidColorBrush(Color.FromRgb(92, 209, 96));
        }
        else if (message.Contains("[WARN]"))
        {
            brush = new SolidColorBrush(Color.FromRgb(240, 180, 60));
        }
        else if (message.Contains("[INFO]"))
        {
            brush = new SolidColorBrush(Color.FromRgb(90, 170, 255));
        }

        var paragraph = new Paragraph(new Run($"[{DateTime.Now:HH:mm:ss}] {message}") { Foreground = brush })
        {
            Margin = new Thickness(0, 1, 0, 1)
        };

        RtfLog.Document.Blocks.Add(paragraph);
        RtfLog.ScrollToEnd();
    }
}
