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

    public static List<ShadowBotUser> GetShadowBotUsers(string? baseDir = null)
    {
        baseDir ??= GetDefaultShadowBotDir();
        var usersRoot = Path.Combine(baseDir, "users");
        if (!Directory.Exists(usersRoot)) return [];

        var users = new List<ShadowBotUser>();
        foreach (var userDir in Directory.GetDirectories(usersRoot))
        {
            var userId = Path.GetFileName(userDir);
            if (!long.TryParse(userId, out _)) continue;

            var appsDir = Path.Combine(userDir, "apps");
            if (Directory.Exists(appsDir))
            {
                var displayName = GetUserDisplayName(userDir, usersRoot);
                var appCount = ScanLocalApps(userDir).Count;
                users.Add(new ShadowBotUser(userId, displayName, userDir, appCount));
            }
        }
        return users;
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
    /// 删除应用并实时同步：标记影刀本地数据库 (user.db3) 回收状态、物理清理目录，并尝试同步云端回收
    /// </summary>
    public static (bool Success, string Message) DeleteApp(string userDir, ShadowBotApp app)
    {
        try
        {
            // 1. 同步影刀本地 SQLite 数据库 (user.db3)
            // 影刀官方逻辑：将应用标记为回收站 (isRecycleBin = 1)，影刀主界面立即根据 isRecycleBin=0 隐藏，
            // 且云同步时会上报回收状态，绝不会因为整行 DELETE 导致云端误判为“本地缺失”而自动复活。
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

            // 2. 物理安全删除应用目录（消除只读属性，避免 Windows 权限拦截；若被锁定则移出扫描目录并异步清理）
            var targetDir = !string.IsNullOrEmpty(app.AppDir) && Directory.Exists(app.AppDir) ? app.AppDir : app.RobotDir;
            if (Directory.Exists(targetDir))
            {
                SafeDeleteDirectory(targetDir);
            }

            // 3. 尝试调用云端回收 API（若本地日志存在当前登录 token）
            _ = Task.Run(() => TryCloudRecycleAppAsync(app.Uuid));

            return (true, "应用已成功删除并在影刀中同步移除。");
        }
        catch (Exception ex)
        {
            return (false, $"删除应用失败: {ex.Message}");
        }
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

    private static async Task TryCloudRecycleAppAsync(string appUuid)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logDir = Path.Combine(localAppData, "ShadowBot", "log");
            if (!Directory.Exists(logDir)) return;

            string? token = null;
            var logFiles = Directory.GetFiles(logDir, "*.log").OrderByDescending(File.GetLastWriteTime).Take(3);
            var tokenRegex = new System.Text.RegularExpressions.Regex(@"current token:\[(.*?)\]");

            foreach (var lf in logFiles)
            {
                try
                {
                    using var fs = new FileStream(lf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    string? line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        var m = tokenRegex.Match(line);
                        if (m.Success && !string.IsNullOrEmpty(m.Groups[1].Value))
                        {
                            token = m.Groups[1].Value.Trim();
                        }
                    }
                    if (!string.IsNullOrEmpty(token)) break;
                }
                catch { }
            }

            if (string.IsNullOrEmpty(token)) return;

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"bearer {token}");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Xybot-Client-RequestId", Guid.NewGuid().ToString());

            // 影刀控制台移入回收站接口
            var url = $"https://api.yingdao.com/api/client/app/apps/{appUuid}/recycle";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            await client.SendAsync(req);
        }
        catch
        {
            // 云端静默尝试，失败不影响本地流程
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
