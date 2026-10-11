using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XbotDeployer.Core;

public static class Packager
{
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

            // 检查父级应用目录是否伴生扩展指令库 (xbot_extensions) 与元素选择器库 (xbot_selectors)
            var parentDir = Directory.GetParent(robotDir)?.FullName;
            if (!string.IsNullOrEmpty(parentDir) && !string.Equals(parentDir, robotDir, StringComparison.OrdinalIgnoreCase))
            {
                var extDir = Path.Combine(parentDir, "xbot_extensions");
                if (Directory.Exists(extDir))
                {
                    var targetExtDir = Path.Combine(stageDir, "xbot_extensions");
                    if (!Directory.Exists(targetExtDir))
                    {
                        CopyDirectory(extDir, targetExtDir, encryptPython);
                    }
                }

                var selDir = Path.Combine(parentDir, "xbot_selectors");
                if (Directory.Exists(selDir))
                {
                    var targetSelDir = Path.Combine(stageDir, "xbot_selectors");
                    if (!Directory.Exists(targetSelDir))
                    {
                        CopyDirectory(selDir, targetSelDir, encryptPython);
                    }
                }
            }

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

            if (encryptPython)
            {
                CompilePythonFiles(stageDir);
                pkgData["encrypt_bot"] = true;
            }

            // Write back package.json
            File.WriteAllText(pkgFile, pkgData.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

            // Repair .dev/*.flow.json display names
            RepairFlowBlockDisplays(stageDir, pkgData);

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
            File.WriteAllText(outJsonPath, pkgData.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

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
            if (ext.Equals(".pyc", StringComparison.OrdinalIgnoreCase) && !encryptPython) continue;

            var fileName = Path.GetFileName(file);
            var targetFile = Path.Combine(destDir, fileName);
            File.Copy(file, targetFile, true);
        }
    }

    private static void CompilePythonFiles(string stageDir)
    {
        var pyFiles = Directory.GetFiles(stageDir, "*.py", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals("__init__.py", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (pyFiles.Count == 0) return;

        const string compileCode = "import py_compile,sys; py_compile.compile(sys.argv[1], cfile=sys.argv[2], doraise=True)";
        foreach (var pyPath in pyFiles)
        {
            var pycPath = Path.ChangeExtension(pyPath, ".pyc");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "python.exe",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(compileCode);
            psi.ArgumentList.Add(pyPath);
            psi.ArgumentList.Add(pycPath);

            using var process = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 python.exe，无法编译 Python 源码。");
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(pycPath))
            {
                throw new InvalidOperationException($"Python 源码编译失败: {Path.GetFileName(pyPath)}{(string.IsNullOrWhiteSpace(error) ? "" : $" - {error.Trim()}")}");
            }

            File.Delete(pyPath);
        }
    }

    private static void RepairFlowBlockDisplays(string stageDir, JsonObject pkgData)
    {
        var devDirs = new List<string>();
        var rootDev = Path.Combine(stageDir, ".dev");
        if (Directory.Exists(rootDev)) devDirs.Add(rootDev);
        try
        {
            foreach (var d in Directory.GetDirectories(stageDir, ".dev", SearchOption.AllDirectories))
            {
                if (!devDirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                {
                    devDirs.Add(d);
                }
            }
        }
        catch { }
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

                var updatedJson = flowObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

                // 确定对应的明文 *.flow.json 路径
                var plainFlowFile = flowFile.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)
                    ? flowFile[..^4]
                    : flowFile;

                // 核心修复：始终确保明文 *.flow.json 存在且包含完整内容，解决 ShadowBot 编辑器加载子流程为空的根本问题
                if (modified || !File.Exists(plainFlowFile) || flowFile.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(plainFlowFile, updatedJson, Encoding.UTF8);
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
            if (proc != null || bName.Contains("process.run", StringComparison.OrdinalIgnoreCase))
            {
                var val = proc?["value"]?.ToString();
                if (!string.IsNullOrEmpty(val))
                {
                    var targetFn = val.Contains(':') ? val.Split(':')[^1] : val;
                    if (flowMap.TryGetValue(targetFn, out var expectedName))
                    {
                        if (proc!["display"]?.ToString() != expectedName)
                        {
                            proc["display"] = expectedName;
                            modified = true;
                        }
                    }
                }
            }

            // 检查 process.invoke_module (如调用 Python 模块或代码子流程 B_Down 等)
            var mod = inputs?["module"] as JsonObject;
            if (mod != null || bName.Contains("process.invoke_module", StringComparison.OrdinalIgnoreCase))
            {
                var val = mod?["value"]?.ToString();
                if (!string.IsNullOrEmpty(val))
                {
                    var targetFn = val.Contains(':') ? val.Split(':')[^1] : val;
                    if (flowMap.TryGetValue(targetFn, out var expectedName))
                    {
                        if (mod!["display"]?.ToString() != expectedName)
                        {
                            mod["display"] = expectedName;
                            modified = true;
                        }
                    }
                }
            }

            // 递归处理积木块内部嵌套的容器块（如 if、loop、try 等子流程积木集合）
            foreach (var containerKey in new[] { "children", "blocks", "body", "subBlocks" })
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
