using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XbotDeployer.Core;

public static class Packager
{
    /// <summary>
    /// 影刀自身写出的 json 均为无 BOM UTF-8；带 BOM 会让影刀读取失败（其 Trim 无法去除 U+FEFF）。
    /// </summary>
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 标准 JSON 输出配置：保持缩进，同时使用 UnsafeRelaxedJsonEscaping 确保中文等字符原样输出为 UTF-8，杜绝 \uXXXX 转义。
    /// </summary>
    public static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string CalculateMd5(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        var hash = md5.ComputeHash(stream);
        return Convert.ToHexStringLower(hash);
    }

    public static (string ZipPath, string PackageMd5, JsonObject PkgData, string PkgFilePath) BuildAppPackage(
        string robotDir,
        string? newAppName = null,
        string? newUuid = null,
        bool encryptPython = false,
        string? outputDir = null)
    {
        if (!Directory.Exists(robotDir))
        {
            throw new DirectoryNotFoundException($"应用目录不存在: {robotDir}");
        }

        var workTempDir = Path.Combine(Path.GetTempPath(), $"xbot_pack_{Guid.NewGuid():N}");
        var stageDir = Path.Combine(workTempDir, "xbot_robot");
        Directory.CreateDirectory(stageDir);

        try
        {
            // Copy files ignoring vcs and cache
            CopyDirectory(robotDir, stageDir, encryptPython);

            // Read and update package.json
            var pkgFile = Path.Combine(stageDir, "package.json");
            JsonObject pkgData = [];
            if (File.Exists(pkgFile))
            {
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(pkgFile, Encoding.UTF8));
                    if (node is JsonObject jo) pkgData = jo;
                }
                catch
                {
                    // ignore
                }
            }

            if (!string.IsNullOrEmpty(newAppName))
            {
                pkgData["name"] = newAppName;
            }

            if (!string.IsNullOrEmpty(newUuid))
            {
                pkgData["uuid"] = newUuid;
            }

            pkgData["version"] = "1";

            var robotType = pkgData["robot_type"]?.ToString();
            if (string.Equals(robotType, "activity", StringComparison.OrdinalIgnoreCase))
            {
                var actCode = pkgData["activity_code"]?.ToString();
                if (string.IsNullOrEmpty(actCode))
                {
                    actCode = $"activity_{Guid.NewGuid():N}"[..17];
                    pkgData["activity_code"] = actCode;
                }
                pkgData["package_code"] = actCode;
            }

            // 确保依赖指令集完整性
            CollectMissingInternalDependencies(stageDir, pkgData, robotDir, encryptPython);

            // 确保每个在 package.json flows 中声明的子流程文件完整就绪（Visual/Code），并保留 workspace.state.json
            EnsureSubflowIntegrity(stageDir, pkgData, robotDir);

            if (encryptPython)
            {
                // 按应用 venv 声明的运行时版本选择匹配解释器，避免 .pyc 魔数不匹配
                var runtimeVer = Scanner.ReadAppPythonVersion(robotDir);
                var runtimeMM = string.IsNullOrEmpty(runtimeVer)
                    ? null
                    : string.Join('.', runtimeVer.Split('.').Take(2));
                CompilePythonFiles(stageDir, runtimeMM);
                pkgData["encrypt_bot"] = true;
            }

            // 修复流程内部积木块显示名与明文 flow.json 同步，确保接收端影刀编辑器完整加载子流程
            RepairFlowBlockDisplays(stageDir, pkgData);

            // Write back package.json (无 BOM：影刀自身写出的 package.json 均无 BOM，并杜绝 Unicode 转义)
            File.WriteAllText(pkgFile, pkgData.ToJsonString(JsonWriteOptions), Utf8NoBom);

            // Target output directory
            string outTargetDir;
            if (string.IsNullOrEmpty(outputDir))
            {
                outTargetDir = Path.Combine(Path.GetTempPath(), $"xbot_out_{Guid.NewGuid():N}");
            }
            else
            {
                outTargetDir = outputDir;
            }
            Directory.CreateDirectory(outTargetDir);

            var zipPath = Path.Combine(outTargetDir, "package.bot");
            if (File.Exists(zipPath)) File.Delete(zipPath);

            var outJsonPath = Path.Combine(outTargetDir, "package.json");
            // 该文件会上传为云端元数据，必须与 package.bot 内的一致：无 BOM，且中文原样 UTF-8 传输。
            File.WriteAllText(outJsonPath, pkgData.ToJsonString(JsonWriteOptions), Utf8NoBom);

            // Zip archive (统一使用标准正斜杠路径，确保影刀云端与客户端跨平台解压兼容)
            CreateStandardZip(stageDir, zipPath);

            var md5 = CalculateMd5(zipPath);
            return (zipPath, md5, pkgData, outJsonPath);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workTempDir))
                {
                    Directory.Delete(workTempDir, true);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void CreateStandardZip(string sourceDir, string zipPath)
    {
        using var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, false, Encoding.UTF8);

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            var entry = archive.CreateEntry(relPath, CompressionLevel.Optimal);
            entry.LastWriteTime = File.GetLastWriteTime(file);

            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(file);
            fileStream.CopyTo(entryStream);
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir, bool encryptPython)
    {
        var ignoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".svn", "__pycache__", ".vscode", ".idea", "venv", "venv310"
        };

        // 必须显式创建目标目录：源目录若不含（未被忽略的）子目录，
        // 下面的子目录循环不会创建它，随后的 File.Copy 会抛 DirectoryNotFoundException。
        Directory.CreateDirectory(destDir);

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            if (ignoredDirs.Contains(dirName)) continue;
            var targetDir = Path.Combine(destDir, dirName);
            Directory.CreateDirectory(targetDir);
            CopyDirectory(dir, targetDir, encryptPython);
        }

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var ext = Path.GetExtension(file);
            // 仅当存在同名 .py 源码时才跳过 .pyc（避免源码与字节码重复）；
            // 若应用此前已被 encrypt_bot 处理过（只剩 .pyc、无 .py），必须保留 .pyc，
            // 否则整条代码型子流程体会在打包时消失。
            if (ext.Equals(".pyc", StringComparison.OrdinalIgnoreCase) && !encryptPython)
            {
                var siblingPy = Path.ChangeExtension(file, ".py");
                if (File.Exists(siblingPy)) continue;
            }

            var fileName = Path.GetFileName(file);
            var targetFile = Path.Combine(destDir, fileName);
            File.Copy(file, targetFile, true);
        }
    }

    internal static void CollectMissingInternalDependencies(string stageDir, JsonObject pkgData, string robotDir, bool encryptPython)
    {
        var internalDeps = pkgData["internaldependencies"] as JsonArray;
        if (internalDeps == null || internalDeps.Count == 0) return;

        var targetExtDir = Path.Combine(stageDir, "xbot_extensions");
        Directory.CreateDirectory(targetExtDir);

        // 收集所有候选搜寻根目录 (覆盖应用父级目录、当前用户目录、各盘安装目录 activities/whls)
        var candidateSearchDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var parentDir = Directory.GetParent(robotDir)?.FullName;
        if (!string.IsNullOrEmpty(parentDir) && !string.Equals(parentDir, robotDir, StringComparison.OrdinalIgnoreCase))
        {
            candidateSearchDirs.Add(Path.Combine(parentDir, "xbot_extensions"));
            var grandParent = Directory.GetParent(parentDir)?.FullName;
            if (!string.IsNullOrEmpty(grandParent))
            {
                candidateSearchDirs.Add(Path.Combine(grandParent, "xbot_extensions"));
                candidateSearchDirs.Add(Path.Combine(grandParent, "activities"));
            }
        }

        var defaultCacheAct = Path.Combine(Scanner.GetDefaultShadowBotDir(), "cache", "activities");
        if (Directory.Exists(defaultCacheAct)) candidateSearchDirs.Add(defaultCacheAct);

        foreach (var installDir in Scanner.DetectShadowBotInstallDirs())
        {
            candidateSearchDirs.Add(Path.Combine(installDir, "activities"));
            candidateSearchDirs.Add(Path.Combine(installDir, "whls"));
            try
            {
                foreach (var sub in Directory.GetDirectories(installDir, "shadowbot-*"))
                {
                    candidateSearchDirs.Add(Path.Combine(sub, "whls"));
                }
            }
            catch { }
        }

        foreach (var depNode in internalDeps)
        {
            var rawDep = depNode?.ToString();
            if (string.IsNullOrEmpty(rawDep)) continue;

            var actName = rawDep.Contains("==") ? rawDep.Split("==")[0].Trim() : rawDep.Trim();
            if (string.IsNullOrEmpty(actName)) continue;

            var existingDest = Path.Combine(targetExtDir, actName);
            if (Directory.Exists(existingDest)) continue;

            var copied = false;
            foreach (var searchRoot in candidateSearchDirs)
            {
                if (!Directory.Exists(searchRoot)) continue;
                var candidateAct = Path.Combine(searchRoot, actName);
                if (Directory.Exists(candidateAct) && File.Exists(Path.Combine(candidateAct, "package.json")))
                {
                    CopyDirectory(candidateAct, existingDest, encryptPython);
                    copied = true;
                    break;
                }
            }

            // 缓存兜底：%LOCALAPPDATA%\ShadowBot\cache\activities\<act>\ 下只有版本号 .zip
            // （无解压后的 package.json），因此上面的目录判定永远不会命中。这里直接从 .zip 解出。
            if (!copied)
            {
                if (TryExtractFromCacheZip(actName, existingDest, rawDep))
                {
                    continue;
                }
            }
        }
    }

    /// <summary>
    /// 从影刀全局指令集缓存 <c>cache\activities\&lt;act&gt;\&lt;version&gt;.zip</c> 中解出扩展。
    /// 优先选择与依赖声明版本一致的 zip，其次取版本号最大的一个。
    /// 注意：影刀缓存 zip 为 ZipCrypto 加密，无口令不可解；此时必须放弃（返回 false），
    /// 否则 .NET 会把密文原样写出，导致依赖"静默损坏"——比缺失更难排查。
    /// </summary>
    private static bool TryExtractFromCacheZip(string actName, string destDir, string rawDep)
    {
        try
        {
            var cacheRoot = Path.Combine(Scanner.GetDefaultShadowBotDir(), "cache", "activities", actName);
            if (!Directory.Exists(cacheRoot)) return false;

            var zips = Directory.GetFiles(cacheRoot, "*.zip");
            if (zips.Length == 0) return false;

            string? chosen = null;
            if (rawDep.Contains("=="))
            {
                var wantVer = rawDep.Split("==")[1].Trim();
                chosen = zips.FirstOrDefault(z =>
                    string.Equals(Path.GetFileNameWithoutExtension(z), wantVer, StringComparison.OrdinalIgnoreCase));
            }
            chosen ??= zips
                .OrderByDescending(z => ParseVersion(Path.GetFileNameWithoutExtension(z)))
                .First();

            // 加密 zip 直接放弃：无法解密，强行解压只会产出密文垃圾
            if (IsZipEncrypted(chosen)) return false;

            var temp = Path.Combine(Path.GetTempPath(), $"xbot_act_{Guid.NewGuid():N}");
            try
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(chosen, temp);
                // zip 根可能是 {package.json, .dev/, *.py}（官方形态），也可能多一层目录
                var src = File.Exists(Path.Combine(temp, "package.json"))
                    ? temp
                    : Directory.GetDirectories(temp).FirstOrDefault(d => File.Exists(Path.Combine(d, "package.json")));
                if (src == null) return false;

                // 解出内容必须是合法 JSON，否则视为损坏，不得进包
                if (!IsValidJsonFile(Path.Combine(src, "package.json"))) return false;

                CopyDirectory(src, destDir, encryptPython: false);
                if (!File.Exists(Path.Combine(destDir, "package.json")) ||
                    !IsValidJsonFile(Path.Combine(destDir, "package.json")))
                {
                    TryDeleteDir(destDir);
                    return false;
                }
                return true;
            }
            finally
            {
                TryDeleteDir(temp);
            }
        }
        catch
        {
            TryDeleteDir(destDir);
            return false;
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    private static bool IsValidJsonFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检测 zip 是否使用加密条目（general purpose bit flag 的 bit0）。
    /// .NET 的 ZipFile 对加密的 STORED 条目既不抛异常也不解密，会静默写出密文，
    /// 因此必须在解压前用原始字节自行判断。
    /// 只在【中央目录】区间内扫描：若全文件扫描，文件数据中偶然出现的 PK\x01\x02
    /// 且其后 bit0 为 1 时，会把合法未加密 zip 误判为加密。
    /// </summary>
    private static bool IsZipEncrypted(string zipPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(zipPath);

            // 定位 EOCD（0x06054b50）：从尾部向前找，其 +12 为中央目录偏移，+10 为条目数
            int eocd = -1;
            for (int i = bytes.Length - 22; i >= 0 && i >= bytes.Length - 22 - 65536; i--)
            {
                if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x05 && bytes[i + 3] == 0x06)
                {
                    eocd = i;
                    break;
                }
            }
            if (eocd < 0) return false;   // 非 zip：交由下游解压抛错处理

            var cdSize = (uint)(bytes[eocd + 12] | (bytes[eocd + 13] << 8) | (bytes[eocd + 14] << 16) | (bytes[eocd + 15] << 24));
            var cdOffset = (uint)(bytes[eocd + 16] | (bytes[eocd + 17] << 8) | (bytes[eocd + 18] << 16) | (bytes[eocd + 19] << 24));

            long start = cdOffset;
            long end = Math.Min((long)cdOffset + cdSize, bytes.Length);
            if (start < 0 || start >= bytes.Length || end <= start) return false;

            // 只在中央目录内按条目头步进扫描（避免 O(n) 全量误命中）
            for (long i = start; i + 10 <= end;)
            {
                if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02)
                {
                    var flag = (ushort)(bytes[i + 8] | (bytes[i + 9] << 8));
                    if ((flag & 0x0001) != 0) return true;

                    // 跳到下一条目：固定 46 字节 + 文件名/扩展区/注释长度
                    int nameLen = bytes[i + 28] | (bytes[i + 29] << 8);
                    int extraLen = bytes[i + 30] | (bytes[i + 31] << 8);
                    int cmtLen = bytes[i + 32] | (bytes[i + 33] << 8);
                    i += 46 + nameLen + extraLen + cmtLen;
                }
                else
                {
                    break;   // 中央目录结构异常，不再继续
                }
            }
            return false;
        }
        catch
        {
            // 无法判定时按"不可用"处理，避免写入未知内容
            return true;
        }
    }

    private static Version ParseVersion(string s)
    {
        var parts = s.Split('.');
        var nums = new List<int>();
        foreach (var p in parts)
        {
            if (int.TryParse(p, out var n)) nums.Add(n);
            else break;
        }
        while (nums.Count < 4) nums.Add(0);
        return new Version(nums[0], nums[1], nums[2], nums[3]);
    }

    private static void CompilePythonFiles(string stageDir, string? runtimeMajorMinor)
    {
        var pyFiles = Directory.GetFiles(stageDir, "*.py", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals("__init__.py", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (pyFiles.Count == 0) return;

        // 关键修复：必须使用与"应用运行时"一致的 Python 解释器编译。
        // 影刀应用代码运行在安装目录的 python\ (3.7.x)；若用 python310\ 编译，产出的 .pyc
        // 魔数不同，运行时导入会抛 ImportError: bad magic number，导致代码型子流程丢失。
        // 无法确定运行时版本时，宁可不编译（保留 .py 源码），也绝不产出不可加载的 .pyc。
        if (string.IsNullOrEmpty(runtimeMajorMinor))
        {
            return;
        }

        var pyExe = Scanner.FindPythonExeMatching(runtimeMajorMinor);
        if (string.IsNullOrEmpty(pyExe))
        {
            return;
        }

        // 解释器自身版本必须与目标运行时一致，否则一律不编译
        var exeVer = Scanner.GetPythonMajorMinor(pyExe);
        if (!string.Equals(exeVer, runtimeMajorMinor, StringComparison.Ordinal))
        {
            return;
        }

        const string compileCode = "import py_compile,sys; py_compile.compile(sys.argv[1], cfile=sys.argv[2], doraise=True)";
        foreach (var pyPath in pyFiles)
        {
            var pycPath = Path.ChangeExtension(pyPath, ".pyc");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = pyExe,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(compileCode);
            psi.ArgumentList.Add(pyPath);
            psi.ArgumentList.Add(pycPath);

            try
            {
                using var process = System.Diagnostics.Process.Start(psi);
                if (process == null) continue;
                // 并发排空两个管道，避免 stderr 写满缓冲导致死锁；并设置超时防止解释器卡死。
                var errTask = process.StandardError.ReadToEndAsync();
                var errText = "";
                if (process.WaitForExit(30000))
                {
                    errText = errTask.GetAwaiter().GetResult();
                }
                else
                {
                    try { process.Kill(true); } catch { }
                    continue;   // 超时：保留 .py 源码
                }
                if (process.ExitCode == 0 && File.Exists(pycPath))
                {
                    File.Delete(pyPath);
                }
            }
            catch
            {
                // 编译异常时严格保留原 .py 源码文件，防止代码子流程丢失
            }
        }
    }

    internal static void EnsureSubflowIntegrity(string stageDir, JsonObject pkgData, string robotDir)
    {
        var flows = pkgData["flows"] as JsonArray;
        var stageDev = Path.Combine(stageDir, ".dev");
        Directory.CreateDirectory(stageDev);

        // 1. 确保并保留 workspace.state.json（影刀设计器打开流程标签页与工作区布局所必需）
        var stageStateFile = Path.Combine(stageDev, "workspace.state.json");
        if (!File.Exists(stageStateFile))
        {
            var candidateStateDirs = new[]
            {
                Path.Combine(robotDir, ".dev"),
                robotDir,
                Directory.GetParent(robotDir)?.FullName != null ? Path.Combine(Directory.GetParent(robotDir)!.FullName, ".dev") : null
            };
            foreach (var cDir in candidateStateDirs)
            {
                if (string.IsNullOrEmpty(cDir)) continue;
                var srcState = Path.Combine(cDir, "workspace.state.json");
                if (File.Exists(srcState))
                {
                    try
                    {
                        File.Copy(srcState, stageStateFile, true);
                        break;
                    }
                    catch { }
                }
            }
        }

        // 如果依然不存在，根据 flows 自动生成标准的 workspace.state.json，杜绝接收端编辑器打不开子流程标签
        if (!File.Exists(stageStateFile) && flows != null && flows.Count > 0)
        {
            try
            {
                var opened = new JsonArray();
                var startup = pkgData["startup"]?.ToString() ?? "main";
                opened.Add(startup);
                foreach (var fl in flows)
                {
                    var nm = fl?["name"]?.ToString() ?? fl?["filename"]?.ToString();
                    if (!string.IsNullOrEmpty(nm) && !opened.Any(x => x?.ToString() == nm))
                    {
                        opened.Add(nm);
                    }
                }
                var defaultState = new JsonObject
                {
                    ["openedFlows"] = opened,
                    ["activeBottomPanel"] = "流程参数",
                    ["activeFlow"] = startup,
                    ["breakPoints"] = new JsonArray()
                };
                File.WriteAllText(stageStateFile, defaultState.ToJsonString(JsonWriteOptions), Utf8NoBom);
            }
            catch { }
        }

        if (flows == null || flows.Count == 0) return;

        // 收集源目录及其父级目录（支持 apps/<uuid>/xbot_robot 与 apps/<uuid> 两种结构）
        var candidateSrcDirs = new List<string> { robotDir };
        var parentDir = Directory.GetParent(robotDir)?.FullName;
        if (!string.IsNullOrEmpty(parentDir) && !string.Equals(parentDir, robotDir, StringComparison.OrdinalIgnoreCase))
        {
            candidateSrcDirs.Add(parentDir);
            var subRobot = Path.Combine(parentDir, "xbot_robot");
            if (Directory.Exists(subRobot) && !candidateSrcDirs.Contains(subRobot))
            {
                candidateSrcDirs.Add(subRobot);
            }
        }

        // 2. 遍历校验并修复 package.json 中声明的每一个流程文件
        foreach (var fl in flows)
        {
            if (fl is not JsonObject fo) continue;
            var fn = fo["filename"]?.ToString();
            var kind = fo["kind"]?.ToString() ?? "Visual";
            if (string.IsNullOrEmpty(fn)) continue;

            if (string.Equals(kind, "Visual", StringComparison.OrdinalIgnoreCase))
            {
                var plainFlowPath = Path.Combine(stageDev, $"{fn}.flow.json");
                var encFlowPath = Path.Combine(stageDev, $"{fn}.flow.json.enc");

                // 若只有 .enc，将其解密生成明文 .flow.json
                if (!File.Exists(plainFlowPath) && File.Exists(encFlowPath))
                {
                    try
                    {
                        var encBytes = File.ReadAllBytes(encFlowPath);
                        var (doc, _) = CryptoHelper.DecryptFlowData(encBytes);
                        if (doc != null)
                        {
                            var text = doc.RootElement.GetRawText();
                            File.WriteAllText(plainFlowPath, text, Utf8NoBom);
                        }
                    }
                    catch { }
                }

                // 若 stageDir 内均未找到，从源目录各候选路径中深度检索
                if (!File.Exists(plainFlowPath))
                {
                    foreach (var sDir in candidateSrcDirs)
                    {
                        var srcPlain = Path.Combine(sDir, ".dev", $"{fn}.flow.json");
                        var srcEnc = Path.Combine(sDir, ".dev", $"{fn}.flow.json.enc");

                        if (File.Exists(srcPlain))
                        {
                            try
                            {
                                File.Copy(srcPlain, plainFlowPath, true);
                                break;
                            }
                            catch { }
                        }
                        else if (File.Exists(srcEnc))
                        {
                            try
                            {
                                File.Copy(srcEnc, encFlowPath, true);
                                var encBytes = File.ReadAllBytes(srcEnc);
                                var (doc, _) = CryptoHelper.DecryptFlowData(encBytes);
                                if (doc != null)
                                {
                                    var text = doc.RootElement.GetRawText();
                                    File.WriteAllText(plainFlowPath, text, Utf8NoBom);
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                }

                // 确保证书或旧版本遗留的 base64 密文文件被彻底解密为标准 JSON
                if (File.Exists(plainFlowPath))
                {
                    try
                    {
                        var rawBytes = File.ReadAllBytes(plainFlowPath);
                        var (doc, wasEncrypted) = CryptoHelper.DecryptFlowData(rawBytes);
                        if (doc != null && wasEncrypted)
                        {
                            var text = doc.RootElement.GetRawText();
                            File.WriteAllText(plainFlowPath, text, Utf8NoBom);
                        }
                    }
                    catch { }
                }
                else
                {
                    // 若完全找不到源文件，生成标准空可视化流程骨架，防止影刀打不开报错
                    try
                    {
                        var stub = new JsonObject
                        {
                            ["name"] = fo["name"]?.ToString() ?? fn,
                            ["memo"] = "",
                            ["kind"] = "visual",
                            ["blocks"] = new JsonArray()
                        };
                        File.WriteAllText(plainFlowPath, stub.ToJsonString(JsonWriteOptions), Utf8NoBom);
                    }
                    catch { }
                }
            }
            else if (string.Equals(kind, "Code", StringComparison.OrdinalIgnoreCase))
            {
                var pyFile = Path.Combine(stageDir, $"{fn}.py");
                if (!File.Exists(pyFile))
                {
                    foreach (var sDir in candidateSrcDirs)
                    {
                        var srcPy = Path.Combine(sDir, $"{fn}.py");
                        if (File.Exists(srcPy))
                        {
                            try
                            {
                                File.Copy(srcPy, pyFile, true);
                                break;
                            }
                            catch { }
                        }
                    }
                }
            }
        }
    }

    internal static void RepairFlowBlockDisplays(string stageDir, JsonObject pkgData)
    {
        // 仅处理【本应用自身】的 .dev 目录（stageDir 即 xbot_robot 的内容层）。
        // 不可递归扫描 stageDir 下所有 .dev：xbot_extensions\activity_*\ 内也有各自的 .dev，
        // 那些流程的显示名不属于本应用 package.json 的 flows[]，套用会导致 8% 的扩展流程显示名被错误覆盖。
        var devDirs = new List<string>();
        var rootDev = Path.Combine(stageDir, ".dev");
        if (Directory.Exists(rootDev)) devDirs.Add(rootDev);
        if (devDirs.Count == 0) return;

        var flows = pkgData["flows"] as JsonArray;
        if (flows == null || flows.Count == 0) return;

        var flowMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fl in flows)
        {
            if (fl is JsonObject fo)
            {
                var fn = fo["filename"]?.ToString();
                var nm = fo["name"]?.ToString();
                if (!string.IsNullOrEmpty(fn) && !string.IsNullOrEmpty(nm))
                {
                    flowMap[fn] = nm;
                }
            }
        }
        if (flowMap.Count == 0) return;

        // 全面检索所有 .dev 及其子目录下的所有 *.flow.json 与 *.flow.json.enc 文件
        var flowFiles = new List<string>();
        foreach (var d in devDirs)
        {
            try
            {
                var files = Directory.GetFiles(d, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".flow.json", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".flow.json.enc", StringComparison.OrdinalIgnoreCase));
                flowFiles.AddRange(files);
            }
            catch { }
        }

        foreach (var flowFile in flowFiles)
        {
            try
            {
                var rawData = File.ReadAllBytes(flowFile);
                var (doc, wasEncrypted) = CryptoHelper.DecryptFlowData(rawData);
                if (doc == null) continue;

                var rootJson = doc.RootElement.GetRawText();
                var node = JsonNode.Parse(rootJson);
                if (node is not JsonObject flowObj) continue;

                var modified = false;
                if (flowObj["blocks"] is JsonArray blocks)
                {
                    modified = RepairBlocksRecursive(blocks, flowMap);
                }

                var updatedJson = flowObj.ToJsonString(JsonWriteOptions);

                // 确定对应的明文 *.flow.json 路径
                var plainFlowFile = flowFile.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)
                    ? flowFile[..^4]
                    : flowFile;

                // 核心修复：始终确保明文 *.flow.json 存在且包含完整内容，解决 ShadowBot 编辑器加载子流程为空的根本问题
                // 无 BOM：影刀自身写出的 778 个 .flow.json 全部无 BOM，带 BOM 会被其 Trim 逻辑漏掉。
                if (modified || !File.Exists(plainFlowFile) || flowFile.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(plainFlowFile, updatedJson, Utf8NoBom);
                }

                // 如果原文件是以 .enc 结尾的加密文件，同时写回最新的 AES-CBC 加密内容，保持双格式严格同步
                if (flowFile.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
                {
                    var encryptedBytes = CryptoHelper.EncryptFlowData(updatedJson);
                    File.WriteAllBytes(flowFile, encryptedBytes);
                }
            }
            catch
            {
                // ignore flow repair errors for corrupted files
            }
        }
    }

    private static bool RepairBlocksRecursive(JsonArray blocks, Dictionary<string, string> flowMap)
    {
        var modified = false;
        foreach (var b in blocks)
        {
            if (b is not JsonObject bObj) continue;

            // 检查 process.run 或任何带有 process input 的调用积木块
            var bName = bObj["name"]?.ToString() ?? "";
            var inputs = bObj["inputs"] as JsonObject;
            var proc = inputs?["process"] as JsonObject;
            if (proc != null)
            {
                var val = proc["value"]?.ToString();
                if (!string.IsNullOrEmpty(val))
                {
                    var targetFn = val.Contains(':') ? val.Split(':')[^1] : val;
                    if (flowMap.TryGetValue(targetFn, out var expectedName))
                    {
                        if (proc["display"]?.ToString() != expectedName)
                        {
                            proc["display"] = expectedName;
                            modified = true;
                        }
                    }
                }
            }

            // 检查 process.invoke_module (如调用 Python 模块或代码子流程 B_Down 等)
            var mod = inputs?["module"] as JsonObject;
            if (mod != null)
            {
                var val = mod["value"]?.ToString();
                if (!string.IsNullOrEmpty(val))
                {
                    var targetFn = val.Contains(':') ? val.Split(':')[^1] : val;
                    if (flowMap.TryGetValue(targetFn, out var expectedName))
                    {
                        if (mod["display"]?.ToString() != expectedName)
                        {
                            mod["display"] = expectedName;
                            modified = true;
                        }
                    }
                }
            }

            // 递归处理积木块内部嵌套的容器块（如 if、loop、try 等子流程积木集合）
            foreach (var containerKey in new[] { "children", "blocks", "body", "subBlocks", "elseBody", "tryBody", "catchBody", "finallyBody" })
            {
                if (bObj[containerKey] is JsonArray subBlocks && subBlocks.Count > 0)
                {
                    if (RepairBlocksRecursive(subBlocks, flowMap))
                    {
                        modified = true;
                    }
                }
            }
        }
        return modified;
    }
}
