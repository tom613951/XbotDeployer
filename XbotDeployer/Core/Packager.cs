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

            // Zip archive
            ZipFile.CreateFromDirectory(stageDir, zipPath, CompressionLevel.Optimal, false);

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
        var devDir = Path.Combine(stageDir, ".dev");
        if (!Directory.Exists(devDir)) return;

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

        foreach (var flowFile in Directory.GetFiles(devDir, "*.flow.json"))
        {
            try
            {
                var rawData = File.ReadAllBytes(flowFile);
                var (doc, wasEncrypted) = CryptoHelper.DecryptFlowData(rawData);
                if (doc == null) continue;

                var rootJson = doc.RootElement.GetRawText();
                var node = JsonNode.Parse(rootJson);
                if (node is not JsonObject flowObj || flowObj["blocks"] is not JsonArray blocks) continue;

                var modified = false;
                foreach (var b in blocks)
                {
                    if (b is not JsonObject bObj) continue;
                    if (bObj["name"]?.ToString() == "process.run")
                    {
                        var inputs = bObj["inputs"] as JsonObject;
                        var proc = inputs?["process"] as JsonObject;
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
                }

                if (modified)
                {
                    var updatedJson = flowObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    if (wasEncrypted)
                    {
                        var encryptedBytes = CryptoHelper.EncryptFlowData(updatedJson);
                        File.WriteAllBytes(flowFile, encryptedBytes);
                    }
                    else
                    {
                        File.WriteAllText(flowFile, updatedJson, Encoding.UTF8);
                    }
                }
            }
            catch
            {
                // ignore flow repair errors
            }
        }
    }
}
