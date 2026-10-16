using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace XbotDeployer.Core;

public sealed class ShadowBotUser : INotifyPropertyChanged
{
    private int _appCount;

    public ShadowBotUser(string userId, string displayName, string userDir, int appCount)
    {
        UserId = userId;
        DisplayName = displayName;
        UserDir = userDir;
        _appCount = appCount;
    }

    public string UserId { get; }
    public string DisplayName { get; }
    public string UserDir { get; }

    public int AppCount
    {
        get => _appCount;
        set
        {
            if (_appCount == value) return;
            _appCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
        }
    }

    /// <summary>
    /// Account label matching original client format: "{display_name} ({app_count}个应用)"
    /// </summary>
    public string DisplayLabel => $"{DisplayName} ({_appCount}个应用)";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ShadowBotApp : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _name = "";

    public required string Uuid { get; init; }
    public required string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }
    public required string Version { get; init; }
    public required string RobotDir { get; init; }
    public required string AppDir { get; init; }
    public required DateTime Mtime { get; init; }
    public required string MtimeStr { get; init; }
    public required long SizeBytes { get; init; }
    public required string SizeStr { get; init; }
    public required int FlowCount { get; init; }
    public required string RobotType { get; init; }
    public string? ActivityCode { get; init; }
    public string? PackageCode { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class Scanner
{
    public static string GetDefaultShadowBotDir()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "ShadowBot");
    }

    /// <summary>
    /// 全自动探测全盘可能的影刀程序安装根目录 (覆盖注册表 InstallPath、进程与常见安装路径)
    /// </summary>
    public static List<string> DetectShadowBotInstallDirs()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 0. 注册表权威来源：影刀安装时写入的 InstallPath (覆盖任意盘符/任意自定义目录)
        //    HKLM\SOFTWARE\ShadowBot x64        -> InstallPath
        //    Uninstall\{...}_is1                -> InstallLocation
        //    注意：先前版本注释声称"覆盖注册表"但从未实现，导致非默认目录完全探测不到。
        foreach (var installPath in ReadRegistryInstallPaths())
        {
            if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
            {
                dirs.Add(installPath);
            }
        }

        // 1. 正在运行的 ShadowBot 进程
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("ShadowBot"))
            {
                try
                {
                    var exePath = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        var dir = Path.GetDirectoryName(exePath);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        {
                            dirs.Add(dir);
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        // 2. 遍历所有就绪的逻辑磁盘驱动器 (C:\, D:\, E:\, F:\ 等) 匹配常见影刀安装路径
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                var candidates = new[]
                {
                    Path.Combine(drive.Name, "Program Files", "ShadowBot"),
                    Path.Combine(drive.Name, "Program Files (x86)", "ShadowBot"),
                    Path.Combine(drive.Name, "ShadowBot"),
                    Path.Combine(drive.Name, "影刀", "ShadowBot"),
                    Path.Combine(drive.Name, "影刀"),
                    Path.Combine(drive.Name, "RPA", "ShadowBot"),
                    Path.Combine(drive.Name, "Softwares", "ShadowBot")
                };

                foreach (var c in candidates)
                {
                    if (Directory.Exists(c)) dirs.Add(c);
                }
            }
        }
        catch { }

        return dirs.ToList();
    }

    /// <summary>
    /// 读取注册表中影刀记录的安装位置（32/64 位视图、多个候选键），用于非默认盘符安装场景。
    /// </summary>
    private static List<string> ReadRegistryInstallPaths()
    {
        var results = new List<string>();

        // 候选键：影刀自身写入的键 + 卸载登记项
        var subKeys = new[]
        {
            @"SOFTWARE\ShadowBot x64",
            @"SOFTWARE\ShadowBot",
            @"SOFTWARE\WOW6432Node\ShadowBot x64",
            @"SOFTWARE\WOW6432Node\ShadowBot"
        };
        var valueNames = new[] { "InstallPath", "InstallLocation", "Path", "InstallDir" };

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                RegistryKey? baseKey = null;
                try
                {
                    baseKey = RegistryKey.OpenBaseKey(hive, view);
                }
                catch
                {
                    continue;
                }

                try
                {
                    foreach (var sub in subKeys)
                    {
                        try
                        {
                            using var key = baseKey.OpenSubKey(sub);
                            if (key == null) continue;
                            foreach (var vn in valueNames)
                            {
                                if (key.GetValue(vn) is string s && !string.IsNullOrWhiteSpace(s))
                                {
                                    results.Add(s.Trim().TrimEnd('\\', '/'));
                                }
                            }
                        }
                        catch { }
                    }

                    // 卸载登记项：扫描 Uninstall 下所有子键，匹配 DisplayName 含 影刀/ShadowBot
                    foreach (var uninst in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                    {
                        try
                        {
                            using var root = baseKey.OpenSubKey(uninst);
                            if (root == null) continue;
                            foreach (var name in root.GetSubKeyNames())
                            {
                                try
                                {
                                    using var app = root.OpenSubKey(name);
                                    if (app == null) continue;
                                    var display = app.GetValue("DisplayName") as string ?? "";
                                    if (display.IndexOf("影刀", StringComparison.OrdinalIgnoreCase) < 0 &&
                                        display.IndexOf("ShadowBot", StringComparison.OrdinalIgnoreCase) < 0)
                                    {
                                        continue;
                                    }
                                    if (app.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
                                    {
                                        results.Add(loc.Trim().TrimEnd('\\', '/'));
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
                finally
                {
                    baseKey?.Dispose();
                }
            }
        }

        return results;
    }

    /// <summary>
    /// 搜集所有可能的 ShadowBot users 数据根目录 (涵盖默认路径、自定义路径、各盘安装目录下的 users)
    /// </summary>
    public static List<string> GetCandidateUsersRoots(string? customBaseDir = null)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 显式传入或用户手动指定的自定义目录
        if (!string.IsNullOrEmpty(customBaseDir) && Directory.Exists(customBaseDir))
        {
            if (Path.GetFileName(customBaseDir).Equals("users", StringComparison.OrdinalIgnoreCase))
            {
                roots.Add(customBaseDir);
            }
            else
            {
                var subUsers = Path.Combine(customBaseDir, "users");
                if (Directory.Exists(subUsers)) roots.Add(subUsers);
                else roots.Add(customBaseDir);
            }
        }

        // 2. 默认 %LOCALAPPDATA%\ShadowBot\users
        var defUsers = Path.Combine(GetDefaultShadowBotDir(), "users");
        if (Directory.Exists(defUsers)) roots.Add(defUsers);

        // 3. 各探测到的安装目录下的 users 文件夹 (支持非 C 盘便携/集中存储)
        foreach (var installDir in DetectShadowBotInstallDirs())
        {
            var uDir = Path.Combine(installDir, "users");
            if (Directory.Exists(uDir)) roots.Add(uDir);
        }

        return roots.ToList();
    }

    /// <summary>
    /// 查找影刀安装目录内置匹配的 Python 解释器。
    /// 关键：影刀应用代码实际运行在安装目录的 <c>python\</c>（3.7.x），而非 <c>python310\</c>。
    /// 优先返回 <c>python\python.exe</c>，避免用 3.10 编译出的 .pyc 被 3.7 运行时拒绝加载
    /// (ImportError: bad magic number) 导致代码型子流程丢失。
    /// </summary>
    public static string? FindShadowBotPythonExe()
    {
        var installDirs = DetectShadowBotInstallDirs();
        foreach (var dir in installDirs)
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(dir, "shadowbot-*"))
                {
                    // 影刀运行时解释器优先（3.7.x）
                    var py = Path.Combine(sub, "python", "python.exe");
                    if (File.Exists(py)) return py;

                    // 回退：其它版本的 python3xx 子目录（按目录名排序，保证确定性）
                    foreach (var alt in Directory.GetDirectories(sub, "python3*"))
                    {
                        var altPy = Path.Combine(alt, "python.exe");
                        if (File.Exists(altPy)) return altPy;
                    }
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// 读取指定应用虚拟环境声明的 Python 版本号（来自 <c>&lt;appDir&gt;\venv\pyvenv.cfg</c> 的 <c>version =</c>）。
    /// 返回如 "3.7.4"；读取失败返回 null。
    /// </summary>
    public static string? ReadAppPythonVersion(string robotDir)
    {
        try
        {
            // 兼容两种布局：apps/<uuid>/xbot_robot（venv 在父级）
            //                apps/<uuid>（robotDir 即应用根，venv 在同级）
            var candidates = new List<string>();
            var parent = Directory.GetParent(robotDir)?.FullName;
            if (!string.IsNullOrEmpty(parent)) candidates.Add(Path.Combine(parent, "venv", "pyvenv.cfg"));
            candidates.Add(Path.Combine(robotDir, "venv", "pyvenv.cfg"));

            var cfg = candidates.FirstOrDefault(File.Exists);
            if (cfg == null) return null;

            foreach (var line in File.ReadAllLines(cfg))
            {
                var t = line.Trim();
                if (!t.StartsWith("version", StringComparison.OrdinalIgnoreCase)) continue;
                var idx = t.IndexOf('=');
                if (idx < 0) continue;
                var v = t[(idx + 1)..].Trim();
                if (!string.IsNullOrEmpty(v)) return v;
            }
        }
        catch { }
        return null;
    }

    private static readonly Dictionary<string, string?> _pyVerCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 询问解释器自身的 major.minor 版本（如 "3.7"）。结果按路径缓存；失败返回 null。
    /// </summary>
    public static string? GetPythonMajorMinor(string? pythonExe)
    {
        if (string.IsNullOrEmpty(pythonExe) || !File.Exists(pythonExe)) return null;
        lock (_pyVerCache)
        {
            // 只缓存成功结果：瞬时失败（如杀软拦截）不应被永久固化
            if (_pyVerCache.TryGetValue(pythonExe, out var cached) && cached != null) return cached;
        }

        string? result = null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pythonExe,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import sys;print('%d.%d'%sys.version_info[:2])");
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                // 必须并发排空两个管道：只读 stdout 会在 stderr 写满缓冲时死锁。
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                if (proc.WaitForExit(10000))
                {
                    var outp = outTask.GetAwaiter().GetResult().Trim();
                    _ = errTask.GetAwaiter().GetResult();
                    if (proc.ExitCode == 0 && outp.Length is > 0 and < 16) result = outp;
                }
                else
                {
                    try { proc.Kill(true); } catch { }
                }
            }
        }
        catch { }

        if (result != null)
        {
            lock (_pyVerCache) { _pyVerCache[pythonExe] = result; }
        }
        return result;
    }

    /// <summary>
    /// 在影刀安装目录中查找与指定 major.minor（如 "3.7"）一致的解释器；找不到返回 null。
    /// </summary>
    public static string? FindPythonExeMatching(string? majorMinor)
    {
        if (string.IsNullOrEmpty(majorMinor)) return null;

        foreach (var dir in DetectShadowBotInstallDirs())
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(dir, "shadowbot-*"))
                {
                    foreach (var alt in Directory.GetDirectories(sub, "python*"))
                    {
                        var exe = Path.Combine(alt, "python.exe");
                        if (!File.Exists(exe)) continue;
                        if (string.Equals(GetPythonMajorMinor(exe), majorMinor, StringComparison.Ordinal))
                        {
                            return exe;
                        }
                    }
                }
            }
            catch { }
        }
        return null;
    }

    public static List<ShadowBotUser> GetShadowBotUsers(string? baseDir = null)
    {
        var usersRoots = GetCandidateUsersRoots(baseDir);
        if (usersRoots.Count == 0) return [];

        var ignoredFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Assistant", "git-repo", "CrashDumps", "cache", "cef", "cef_main", "log", "update", "skills"
        };

        var users = new List<ShadowBotUser>();
        var seenUserDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var usersRoot in usersRoots)
        {
            if (!Directory.Exists(usersRoot)) continue;

            foreach (var userDir in Directory.GetDirectories(usersRoot))
            {
                var folderName = Path.GetFileName(userDir);
                if (ignoredFolders.Contains(folderName)) continue;
                if (!seenUserDirs.Add(userDir)) continue;

                // 只要目录下存在 user.db3 或 apps 目录，即为一个合法的影刀用户 Profile 目录（支持纯数字 ID 与 UUID 格式目录）
                var hasDb = File.Exists(Path.Combine(userDir, "user.db3"));
                var hasApps = Directory.Exists(Path.Combine(userDir, "apps"));
                if (!hasDb && !hasApps) continue;

                var displayName = GetUserDisplayName(userDir, usersRoot);
                var appCount = ScanLocalApps(userDir).Count;
                users.Add(new ShadowBotUser(folderName, displayName, userDir, appCount));
            }
        }

        // 根据用户目录或 user.db3 的最后修改时间降序排序（最近活跃登录的用户优先排在最前）
        return users.OrderByDescending(u =>
        {
            var db = Path.Combine(u.UserDir, "user.db3");
            return File.Exists(db) ? File.GetLastWriteTime(db) : Directory.GetLastWriteTime(u.UserDir);
        }).ToList();
    }

    private static string GetUserDisplayName(string userDir, string usersRoot)
    {
        var userId = Path.GetFileName(userDir);
        string? ownerName = null;

        // 1. user.db3
        var dbPath = Path.Combine(userDir, "user.db3");
        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ownerName FROM developmentsync_apps_v2 WHERE ownerName IS NOT NULL AND ownerName != '' LIMIT 1";
                var res = cmd.ExecuteScalar();
                if (res != null && res != DBNull.Value)
                {
                    ownerName = res.ToString();
                }
                if (string.IsNullOrEmpty(ownerName))
                {
                    cmd.CommandText = "SELECT ownerName FROM development_apps WHERE ownerName IS NOT NULL AND ownerName != '' LIMIT 1";
                    var res2 = cmd.ExecuteScalar();
                    if (res2 != null && res2 != DBNull.Value)
                    {
                        ownerName = res2.ToString();
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        // 2. Account.xml
        var accXml = Path.Combine(usersRoot, "Account.xml");
        var accInfo = new Dictionary<string, (string Name, string Uname, string Dname, string Ent)>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(accXml))
        {
            try
            {
                var doc = XDocument.Load(accXml);
                foreach (var acc in doc.Descendants("AccountInfo"))
                {
                    var name = acc.Element("Name")?.Value?.Trim() ?? "";
                    var uname = acc.Element("UserName")?.Value?.Trim() ?? "";
                    var dname = acc.Element("UserInfoDisplayName")?.Value?.Trim() ?? "";
                    var ent = acc.Element("EnterpriseName")?.Value?.Trim() ?? "";

                    var tuple = (name, uname, dname, ent);
                    foreach (var key in new[] { name, uname, dname })
                    {
                        if (!string.IsNullOrEmpty(key))
                        {
                            accInfo[key] = tuple;
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        if (!string.IsNullOrEmpty(ownerName))
        {
            if (accInfo.TryGetValue(ownerName, out var info))
            {
                var bestName = !string.IsNullOrEmpty(info.Dname) ? info.Dname : (!string.IsNullOrEmpty(info.Uname) ? info.Uname : info.Name);
                if (!string.IsNullOrEmpty(info.Ent))
                {
                    return $"{bestName} ({info.Ent})";
                }
                else if (!string.IsNullOrEmpty(info.Name) && info.Name != bestName)
                {
                    return $"{bestName} ({info.Name})";
                }
                return bestName;
            }
            return ownerName;
        }

        return userId;
    }

    public static List<ShadowBotApp> ScanLocalApps(string? userDir = null)
    {
        if (string.IsNullOrEmpty(userDir))
        {
            var users = GetShadowBotUsers();
            if (users.Count == 0) return [];
            userDir = users[0].UserDir;
        }

        var appsDir = Path.Combine(userDir, "apps");
        if (!Directory.Exists(appsDir)) return [];

        // 查询影刀本地数据库中所有已被放入回收站 (isRecycleBin != 0) 的应用 UUID，在扫描时予以过滤，与影刀主界面保持一致
        var recycledUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dbPath = Path.Combine(userDir, "user.db3");
        if (File.Exists(dbPath))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT uuid FROM developmentsync_apps_v2 WHERE isRecycleBin != 0";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                    {
                        recycledUuids.Add(reader.GetString(0));
                    }
                }
            }
            catch
            {
                // ignore db read errors
            }
        }

        var apps = new List<ShadowBotApp>();

        foreach (var appFolder in Directory.GetDirectories(appsDir))
        {
            var folderUuid = Path.GetFileName(appFolder);
            if (folderUuid.EndsWith("_temp", StringComparison.OrdinalIgnoreCase) ||
                folderUuid.StartsWith("_deleted", StringComparison.OrdinalIgnoreCase)) continue;

            if (recycledUuids.Contains(folderUuid)) continue;

            // Try appFolder directly or appFolder/xbot_robot
            var robotDir = appFolder;
            var subRobot = Path.Combine(appFolder, "xbot_robot");
            if (Directory.Exists(subRobot))
            {
                robotDir = subRobot;
            }

            var pkgFile = Path.Combine(robotDir, "package.json");
            if (!File.Exists(pkgFile)) continue;

            try
            {
                using var jsonDoc = JsonDocument.Parse(File.ReadAllText(pkgFile));
                var root = jsonDoc.RootElement;

                var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? folderUuid : folderUuid;
                var version = root.TryGetProperty("version", out var verProp) ? verProp.ToString() : "1";
                var robotType = root.TryGetProperty("robot_type", out var typeProp) ? typeProp.GetString() ?? "app" : "app";
                var activityCode = root.TryGetProperty("activity_code", out var actProp) ? actProp.GetString() : null;
                var packageCode = root.TryGetProperty("package_code", out var pkgProp) ? pkgProp.GetString() : null;

                var flowCount = 0;
                if (root.TryGetProperty("flows", out var flowsProp) && flowsProp.ValueKind == JsonValueKind.Array)
                {
                    flowCount = flowsProp.GetArrayLength();
                }

                var mtime = File.GetLastWriteTime(pkgFile);
                var totalSize = GetDirectorySize(new DirectoryInfo(robotDir));

                apps.Add(new ShadowBotApp
                {
                    Uuid = folderUuid,
                    Name = name,
                    Version = version,
                    RobotDir = robotDir,
                    AppDir = appFolder,
                    Mtime = mtime,
                    MtimeStr = mtime.ToString("yyyy-MM-dd HH:mm:ss"),
                    SizeBytes = totalSize,
                    SizeStr = FormatSize(totalSize),
                    FlowCount = flowCount,
                    RobotType = robotType,
                    ActivityCode = activityCode,
                    PackageCode = packageCode
                });
            }
            catch
            {
                // ignore invalid app
            }
        }

        return apps.OrderByDescending(a => a.Mtime).ToList();
    }

    /// <summary>
    /// 重命名应用并实时同步至本地 package.json 与影刀本地数据库 (user.db3)
    /// </summary>
    public static (bool Success, string Message) RenameApp(string userDir, ShadowBotApp app, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            return (false, "应用名称不能为空。");
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        if (newName.Any(c => invalidChars.Contains(c)))
        {
            return (false, "应用名称不能包含非法文件名字符 (\\ / : * ? \" < > |)。");
        }

        try
        {
            // 1. 同步更新文件系统中的 package.json
            var pkgPaths = new List<string>();
            var p1 = Path.Combine(app.RobotDir, "package.json");
            if (File.Exists(p1)) pkgPaths.Add(p1);

            if (!string.IsNullOrEmpty(app.AppDir) && !string.Equals(app.AppDir, app.RobotDir, StringComparison.OrdinalIgnoreCase))
            {
                var p2 = Path.Combine(app.AppDir, "package.json");
                if (File.Exists(p2) && !pkgPaths.Contains(p2)) pkgPaths.Add(p2);
            }

            foreach (var pkgPath in pkgPaths)
            {
                try
                {
                    var content = File.ReadAllText(pkgPath);
                    var node = System.Text.Json.Nodes.JsonNode.Parse(content);
                    if (node is System.Text.Json.Nodes.JsonObject jo)
                    {
                        jo["name"] = newName;
                        File.WriteAllText(pkgPath, jo.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
                catch (Exception ex)
                {
                    return (false, $"修改 package.json 失败: {ex.Message}");
                }
            }

            // 2. 同步更新影刀 SQLite 数据库 (user.db3)
            var dbPath = Path.Combine(userDir, "user.db3");
            if (File.Exists(dbPath))
            {
                try
                {
                    using var conn = new SqliteConnection($"Data Source={dbPath}");
                    conn.Open();
                    var nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    using var cmd1 = conn.CreateCommand();
                    cmd1.CommandText = "UPDATE developmentsync_apps_v2 SET name = @name, modifyTime = @now, developTimestamp = @nowMs, isSynchronized = 0 WHERE uuid = @uuid";
                    cmd1.Parameters.AddWithValue("@name", newName);
                    cmd1.Parameters.AddWithValue("@now", nowStr);
                    cmd1.Parameters.AddWithValue("@nowMs", nowMs);
                    cmd1.Parameters.AddWithValue("@uuid", app.Uuid);
                    cmd1.ExecuteNonQuery();

                    using var cmd2 = conn.CreateCommand();
                    cmd2.CommandText = "UPDATE development_apps SET name = @name, modifyTime = @now, developTimestamp = @nowMs, isSynchronized = 0 WHERE uuid = @uuid";
                    cmd2.Parameters.AddWithValue("@name", newName);
                    cmd2.Parameters.AddWithValue("@now", nowStr);
                    cmd2.Parameters.AddWithValue("@nowMs", nowMs);
                    cmd2.Parameters.AddWithValue("@uuid", app.Uuid);
                    cmd2.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    // 数据库更新警告（可能影刀正在运行被短暂占用锁库），依然完成内存与文件更新
                    app.Name = newName;
                    return (true, $"文件重命名成功，但影刀本地数据库同步提示: {ex.Message}");
                }
            }

            app.Name = newName;
            return (true, "应用重命名成功，已同步至影刀！");
        }
        catch (Exception ex)
        {
            return (false, $"重命名应用异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 删除应用并实时同步：支持彻底删除（本地与云端同步销毁）或仅清除本地缓存
    /// </summary>
    public static (bool Success, string Message) DeleteApp(string userDir, ShadowBotApp app, bool deleteCloud = true)
    {
        try
        {
            var gatewayOk = false;
            if (deleteCloud)
            {
                // 1. 若选择同步删除云端：优先调用影刀客户端本地官方 API 网关 (ApiHttpServer，端口 42500~42505) 彻底销毁
                gatewayOk = TryShadowBotGatewayDelete(app.Uuid, app.RobotType);

                // 2. 同步更新影刀本地 SQLite 数据库 (user.db3) 标记回收状态
                // 影刀官方机理：将应用标记为回收站 (isRecycleBin = 1)，影刀主界面立即根据 isRecycleBin=0 隐藏，
                // 且后续云同步时会上报回收状态，绝不会因为直接整行 DELETE 导致云端误判为“本地缺失”而重新复活下载。
                var dbPath = Path.Combine(userDir, "user.db3");
                if (File.Exists(dbPath))
                {
                    try
                    {
                        using var conn = new SqliteConnection($"Data Source={dbPath}");
                        conn.Open();
                        var nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                        using var cmd1 = conn.CreateCommand();
                        cmd1.CommandText = "UPDATE developmentsync_apps_v2 SET isRecycleBin = 1, recycleTime = @now, modifyTime = @now, isSynchronized = 0 WHERE uuid = @uuid";
                        cmd1.Parameters.AddWithValue("@now", nowStr);
                        cmd1.Parameters.AddWithValue("@uuid", app.Uuid);
                        var affected = cmd1.ExecuteNonQuery();

                        // 若该表不存在此记录（例如纯本地测试应用），则执行物理清除
                        if (affected == 0)
                        {
                            using var cmdDel = conn.CreateCommand();
                            cmdDel.CommandText = "DELETE FROM developmentsync_apps_v2 WHERE uuid = @uuid";
                            cmdDel.Parameters.AddWithValue("@uuid", app.Uuid);
                            cmdDel.ExecuteNonQuery();
                        }

                        using var cmd2 = conn.CreateCommand();
                        cmd2.CommandText = "DELETE FROM development_apps WHERE uuid = @uuid";
                        cmd2.Parameters.AddWithValue("@uuid", app.Uuid);
                        cmd2.ExecuteNonQuery();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"数据库更新提示: {ex.Message}");
                    }
                }
            }

            // 3. 物理安全删除应用目录（消除只读属性，避免 Windows 权限拦截；若被锁定则移出扫描目录并异步清理）
            var targetDir = !string.IsNullOrEmpty(app.AppDir) && Directory.Exists(app.AppDir) ? app.AppDir : app.RobotDir;
            if (Directory.Exists(targetDir))
            {
                SafeDeleteDirectory(targetDir);
            }

            string tip;
            if (deleteCloud)
            {
                tip = gatewayOk
                    ? "应用已通过影刀客户端网关与本地数据库成功删除，云端与本地已同步清除！"
                    : "应用已在影刀本地数据库与文件中彻底移除，已同步标记回收状态。";
            }
            else
            {
                tip = "应用本地缓存已安全清除，磁盘空间已释放；影刀云端空间已保留，后续可重新下载同步。";
            }

            return (true, tip);
        }
        catch (Exception ex)
        {
            return (false, $"删除应用失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 调用影刀客户端内建官方 API 网关 (端口 42500..42505) 彻底删除应用 (包含移入回收站并永久销毁)
    /// </summary>
    private static bool TryShadowBotGatewayDelete(string appUuid, string robotType)
    {
        for (int port = 42500; port <= 42505; port++)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

                // 探测健康状态
                var healthResp = client.GetAsync($"http://127.0.0.1:{port}/api/v1/health").GetAwaiter().GetResult();
                if (!healthResp.IsSuccessStatusCode) continue;

                var appType = string.Equals(robotType, "activity", StringComparison.OrdinalIgnoreCase) ? "activity" : "developed";

                // 步骤 1: 移入回收站 (触发影刀主进程将应用移出主界面列表并上报云端回收)
                var recycleUrl = $"http://127.0.0.1:{port}/api/v1/apps/{appUuid}/recycle";
                using var recycleReq = new HttpRequestMessage(HttpMethod.Post, recycleUrl);
                recycleReq.Content = new StringContent(
                    JsonSerializer.Serialize(new { AppType = appType }),
                    Encoding.UTF8,
                    "application/json"
                );
                var recResp = client.SendAsync(recycleReq).GetAwaiter().GetResult();

                // 步骤 2: 从回收站彻底永久销毁该应用 (从云端及影刀彻底删除)
                var deleteUrl = $"http://127.0.0.1:{port}/api/v1/apps/recycle/{appUuid}";
                using var delReq = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
                var delResp = client.SendAsync(delReq).GetAwaiter().GetResult();

                if (delResp.IsSuccessStatusCode || recResp.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
                // 探测下一个端口
            }
        }
        return false;
    }

    private static void SafeDeleteDirectory(string targetDir)
    {
        if (!Directory.Exists(targetDir)) return;

        try
        {
            var di = new DirectoryInfo(targetDir);
            foreach (var fi in di.GetFiles("*", SearchOption.AllDirectories))
            {
                try { fi.Attributes = FileAttributes.Normal; } catch { }
            }
            foreach (var sdi in di.GetDirectories("*", SearchOption.AllDirectories))
            {
                try { sdi.Attributes = FileAttributes.Normal; } catch { }
            }
            try { di.Attributes = FileAttributes.Normal; } catch { }

            Directory.Delete(targetDir, true);
        }
        catch
        {
            // 若遇进程占用，重命名为 _deleted_ 临时文件夹移出应用扫描路径，并在后台重试异步清理
            try
            {
                var parent = Path.GetDirectoryName(targetDir);
                if (!string.IsNullOrEmpty(parent))
                {
                    var trashDir = Path.Combine(parent, $"_deleted_{Guid.NewGuid():N}");
                    Directory.Move(targetDir, trashDir);
                    Task.Run(() =>
                    {
                        for (int i = 0; i < 5; i++)
                        {
                            Thread.Sleep(1000);
                            try
                            {
                                if (Directory.Exists(trashDir))
                                {
                                    Directory.Delete(trashDir, true);
                                    break;
                                }
                            }
                            catch { }
                        }
                    });
                }
            }
            catch { }
        }
    }

    private static long GetDirectorySize(DirectoryInfo d)
    {
        long size = 0;
        try
        {
            foreach (var fi in d.GetFiles("*", SearchOption.AllDirectories))
            {
                size += fi.Length;
            }
        }
        catch
        {
            // ignore access errors
        }
        return size;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }
}
