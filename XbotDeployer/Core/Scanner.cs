using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
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

    public required string Uuid { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string RobotDir { get; init; }
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

        var apps = new List<ShadowBotApp>();

        foreach (var appFolder in Directory.GetDirectories(appsDir))
        {
            var folderUuid = Path.GetFileName(appFolder);
            if (folderUuid.EndsWith("_temp", StringComparison.OrdinalIgnoreCase)) continue;

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
