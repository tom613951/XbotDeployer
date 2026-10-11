using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace XbotDeployer.Core;

public sealed class ShadowBotDeployer
{
    private readonly string _apiBaseUrl;
    private readonly HttpClient _httpClient;

    public ShadowBotDeployer(string apiBaseUrl = "https://api.yingdao.com")
    {
        _apiBaseUrl = apiBaseUrl.TrimEnd('/');
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
    }

    public async Task<(bool Success, string Message, JsonElement? Data)> AssignUploadUrlAsync(
        string targetToken,
        string appId,
        int version = 1,
        string appType = "app",
        bool isBot = true)
    {
        var url = $"{_apiBaseUrl}/api/client/app/file/assignUploadUrl";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"bearer {targetToken}");
        request.Headers.TryAddWithoutValidation("Xybot-Client-RequestId", Guid.NewGuid().ToString());

        var payload = new
        {
            appId,
            appType,
            version,
            isBot
        };
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            var resp = await _httpClient.SendAsync(request);
            var resStr = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                var doc = JsonDocument.Parse(resStr);
                var root = doc.RootElement;
                var code = root.TryGetProperty("code", out var cp) ? cp.GetInt32() : 0;
                var success = root.TryGetProperty("success", out var sp) && sp.GetBoolean();

                if (code == 200 || success)
                {
                    var data = root.TryGetProperty("data", out var dp) ? dp.Clone() : default;
                    return (true, "云存储资源分配成功", data);
                }
                var msg = root.TryGetProperty("msg", out var mp) ? mp.GetString() : "未知错误";
                return (false, $"分配云存储资源失败: {msg}", null);
            }

            if ((int)resp.StatusCode == 401)
            {
                return (false, "分配云存储资源失败: 接收方 Token 已过期，请重新登录", null);
            }

            return (false, $"分配云存储资源失败 (HTTP {resp.StatusCode}): {resStr}", null);
        }
        catch (Exception ex)
        {
            return (false, $"请求云存储资源异常: {ex.Message}", null);
        }
    }

    public async Task<(bool Success, string Message)> UploadFileToCloudAsync(
        string uploadUrl,
        string filePath,
        string description = "文件",
        Action<string>? progressCallback = null)
    {
        if (!File.Exists(filePath))
        {
            return (false, $"本地文件不存在: {filePath}");
        }

        var fileSize = new FileInfo(filePath).Length;
        progressCallback?.Invoke($"正在上传 {description} 至云端 ({fileSize} 字节)...");

        try
        {
            using var putStream = File.OpenRead(filePath);
            using var putContent = new StreamContent(putStream);
            using var putResponse = await _httpClient.PutAsync(uploadUrl, putContent);
            if (putResponse.IsSuccessStatusCode)
            {
                return (true, $"{description}云端上传成功");
            }

            var putError = await putResponse.Content.ReadAsStringAsync();

            // Fallback try POST with a fresh stream because the PUT request consumed the first stream.
            using var postStream = File.OpenRead(filePath);
            using var postContent = new StreamContent(postStream);
            using var postResponse = await _httpClient.PostAsync(uploadUrl, postContent);
            if (postResponse.IsSuccessStatusCode)
            {
                return (true, $"{description}云端上传成功");
            }

            return (false, $"上传失败 (PUT {putResponse.StatusCode}): {putError}");
        }
        catch (Exception ex)
        {
            return (false, $"上传 {description} 网络异常: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message, JsonElement? Data)> CreateDevelopAppAsync(
        string targetToken,
        string appId,
        string appName,
        JsonObject pkgData,
        string packageMd5)
    {
        var url = $"{_apiBaseUrl}/api/client/app/develop/create";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"bearer {targetToken}");
        request.Headers.TryAddWithoutValidation("Xybot-Client-RequestId", Guid.NewGuid().ToString());

        var stats = pkgData["statistics"] as JsonObject;
        var flows = pkgData["flows"] as JsonArray;
        var flowCount = flows?.Count ?? 1;
        var blockCount = stats?["blockCount"]?.GetValue<int>() ?? 1;
        var magicBlockCount = stats?["magicBlockCount"]?.GetValue<int>() ?? 0;
        var sourceLineCount = stats?["sourceLineCount"]?.GetValue<int>() ?? 0;

        var appPackage = new JsonObject
        {
            ["activities"] = new JsonArray(),
            ["appFlowParamList"] = new JsonArray(),
            ["appIcon"] = pkgData["icon"]?.ToString() ?? "",
            ["appType"] = pkgData["robot_type"]?.ToString() ?? "app",
            ["blockCount"] = blockCount,
            ["customItems"] = pkgData["customItems"] != null ? JsonNode.Parse(pkgData["customItems"]!.ToJsonString()) : new JsonObject
            {
                ["gifUrl"] = "",
                ["imageName"] = "",
                ["imageUrl"] = "",
                ["uiaType"] = "PC",
                ["videoUrl"] = ""
            },
            ["description"] = pkgData["description"]?.ToString() ?? "",
            ["elementLibraryCodes"] = new JsonArray(),
            ["enableViewSource"] = "false",
            ["externalDependencies"] = pkgData["external_dependencies"] != null ? JsonNode.Parse(pkgData["external_dependencies"]!.ToJsonString()) : new JsonArray(),
            ["flows"] = pkgData["flows"] != null ? JsonNode.Parse(pkgData["flows"]!.ToJsonString()) : new JsonArray(),
            ["flow_groups"] = pkgData["flow_groups"] != null ? JsonNode.Parse(pkgData["flow_groups"]!.ToJsonString()) : new JsonArray(),
            ["flowCount"] = flowCount,
            ["gifUrl"] = "",
            ["imageName"] = "",
            ["imageUrl"] = "",
            ["instruction"] = pkgData["instruction"]?.ToString() ?? "",
            ["internalDependencies"] = pkgData["internaldependencies"] != null ? JsonNode.Parse(pkgData["internaldependencies"]!.ToJsonString()) : new JsonArray(),
            ["internalautodependencies"] = pkgData["internalautodependencies"] != null ? JsonNode.Parse(pkgData["internalautodependencies"]!.ToJsonString()) : new JsonArray(),
            ["ipaasDependencies"] = pkgData["ipaasDependencies"] != null ? JsonNode.Parse(pkgData["ipaasDependencies"]!.ToJsonString()) : new JsonArray(),
            ["magicBlockCount"] = magicBlockCount,
            ["name"] = appName,
            ["packageCode"] = pkgData["package_code"]?.ToString() ?? "",
            ["sourceLineCount"] = sourceLineCount,
            ["statistics"] = new JsonObject
            {
                ["blockCount"] = blockCount,
                ["flowCount"] = flowCount,
                ["magicBlockCount"] = magicBlockCount,
                ["sourceLineCount"] = sourceLineCount
            },
            ["uiTags"] = pkgData["tags"]?.ToString() ?? "",
            ["uiaType"] = pkgData["uia_type"]?.ToString() ?? "PC",
            ["videoUrl"] = ""
        };

        var payload = new JsonObject
        {
            ["appId"] = appId,
            ["appPackage"] = appPackage,
            ["elementLibraryStatus"] = 0,
            ["groupId"] = "",
            ["packageMd5"] = packageMd5
        };

        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        try
        {
            var resp = await _httpClient.SendAsync(request);
            var resStr = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                var doc = JsonDocument.Parse(resStr);
                var root = doc.RootElement;
                var code = root.TryGetProperty("code", out var cp) ? cp.GetInt32() : 0;
                var success = root.TryGetProperty("success", out var sp) && sp.GetBoolean();

                if (code == 200 || success)
                {
                    var data = root.TryGetProperty("data", out var dp) ? dp.Clone() : default;
                    return (true, "接收方账号应用同步创建成功", data);
                }

                var msg = root.TryGetProperty("msg", out var mp) ? mp.GetString() : "创建应用失败";
                return (false, $"创建应用失败: {msg}", null);
            }

            return (false, $"创建应用请求异常 (HTTP {resp.StatusCode}): {resStr}", null);
        }
        catch (Exception ex)
        {
            return (false, $"创建应用网络异常: {ex.Message}", null);
        }
    }

    public async Task<bool> SaveDevelopTimeAsync(string targetToken, string appId)
    {
        var url = $"{_apiBaseUrl}/api/client/app/developInfo/developTime/save";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"bearer {targetToken}");

        var now = DateTime.Now;
        var payload = new
        {
            appId,
            startTime = (now - TimeSpan.FromMinutes(2)).ToString("yyyy-MM-dd HH:mm:ss"),
            endTime = now.ToString("yyyy-MM-dd HH:mm:ss")
        };
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            await _httpClient.SendAsync(request);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> VerifyAppInTargetListAsync(string targetToken, string appId)
    {
        var url = $"{_apiBaseUrl}/api/client/app/develop/list";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("Authorization", $"bearer {targetToken}");

        var payload = new
        {
            pageDTO = new { page = "1", size = "1000" },
            groupId = (string?)null,
            name = "",
            pageType = "1",
            sortBy = "4"
        };
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            var resp = await _httpClient.SendAsync(request);
            if (resp.IsSuccessStatusCode)
            {
                var resStr = await resp.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(resStr);
                if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataArr.EnumerateArray())
                    {
                        if (item.TryGetProperty("appId", out var idProp) && idProp.GetString() == appId)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(bool Success, string Message)> DeploySingleAppAsync(
        string targetToken,
        string robotDir,
        string? newAppName = null,
        bool encryptPython = false,
        Action<string>? logCallback = null)
    {
        string? zipPath = null;
        try
        {
            var freshUuid = Guid.NewGuid().ToString();
            var appDisplayName = newAppName ?? Path.GetFileName(robotDir);

            logCallback?.Invoke($"[INFO] 开始打包应用: {appDisplayName} (UUID: {freshUuid})");
            var (builtZip, pkgMd5, pkgData, pkgJsonPath) = Packager.BuildAppPackage(
                robotDir: robotDir,
                newAppName: newAppName,
                newUuid: freshUuid,
                encryptPython: encryptPython
            );
            zipPath = builtZip;

            var zipSize = new FileInfo(zipPath).Length;
            logCallback?.Invoke($"[SUCCESS] 打包完成 | 包大小: {zipSize} 字节 | MD5: {pkgMd5}");

            var robotType = pkgData["robot_type"]?.ToString() ?? "app";
            var typeDesc = robotType == "activity" ? "指令集" : "应用包";

            // 1. Assign upload URL for package.bot
            logCallback?.Invoke($"[INFO] 正在申请云存储资源 ({typeDesc} package.bot)...");
            var (okBot, msgBot, resDataBot) = await AssignUploadUrlAsync(
                targetToken: targetToken,
                appId: freshUuid,
                version: 1,
                appType: "app",
                isBot: true
            );
            if (!okBot || resDataBot == null)
            {
                logCallback?.Invoke($"[ERROR] {msgBot}");
                return (false, msgBot);
            }

            var uploadBotUrl = resDataBot.Value.TryGetProperty("uploadUrl", out var ub) ? ub.GetString() : null;
            if (string.IsNullOrEmpty(uploadBotUrl))
            {
                var err = "云存储返回信息缺少 uploadUrl (package.bot)";
                logCallback?.Invoke($"[ERROR] {err}");
                return (false, err);
            }

            // 2. Upload package.bot
            var (upOkBot, upMsgBot) = await UploadFileToCloudAsync(uploadBotUrl, zipPath, $"{typeDesc} (package.bot)", logCallback);
            if (!upOkBot)
            {
                logCallback?.Invoke($"[ERROR] {upMsgBot}");
                return (false, upMsgBot);
            }

            // 3. Assign upload URL for package.json
            logCallback?.Invoke($"[INFO] 正在申请元数据云存储资源 (package.json)...");
            var (okJson, msgJson, resDataJson) = await AssignUploadUrlAsync(
                targetToken: targetToken,
                appId: freshUuid,
                version: 1,
                appType: "app",
                isBot: false
            );
            if (!okJson || resDataJson == null)
            {
                logCallback?.Invoke($"[ERROR] {msgJson}");
                return (false, msgJson);
            }

            var uploadJsonUrl = resDataJson.Value.TryGetProperty("uploadUrl", out var uj) ? uj.GetString() : null;
            var jsonFileKeyMd5 = resDataJson.Value.TryGetProperty("fileKeyMd5", out var jm) ? jm.GetString() : null;
            if (string.IsNullOrEmpty(uploadJsonUrl) || string.IsNullOrEmpty(jsonFileKeyMd5))
            {
                var err = "云存储返回信息缺少 uploadUrl 或 fileKeyMd5 (package.json)";
                logCallback?.Invoke($"[ERROR] {err}");
                return (false, err);
            }

            // 4. Upload package.json
            var (upOkJson, upMsgJson) = await UploadFileToCloudAsync(uploadJsonUrl, pkgJsonPath, "元数据 (package.json)", logCallback);
            if (!upOkJson)
            {
                logCallback?.Invoke($"[ERROR] {upMsgJson}");
                return (false, upMsgJson);
            }

            // 5. Create app in cloud
            logCallback?.Invoke("[INFO] 正在目标账号下注册创建应用...");
            var (createOk, createMsg, _) = await CreateDevelopAppAsync(
                targetToken: targetToken,
                appId: freshUuid,
                appName: pkgData["name"]?.ToString() ?? appDisplayName,
                pkgData: pkgData,
                packageMd5: jsonFileKeyMd5
            );
            if (!createOk)
            {
                logCallback?.Invoke($"[ERROR] {createMsg}");
                return (false, createMsg);
            }

            // 6. Save develop time
            await SaveDevelopTimeAsync(targetToken, freshUuid);

            // 7. Verify presence in cloud list
            await Task.Delay(500);
            var verified = await VerifyAppInTargetListAsync(targetToken, freshUuid);
            if (verified)
            {
                logCallback?.Invoke("[SUCCESS] 目标云端应用列表校验通过，应用已就绪。");
            }
            else
            {
                logCallback?.Invoke("[WARN] 目标云端列表正在同步索引中...");
            }

            var finalMsg = $"迁移完成：应用【{pkgData["name"]}】已成功推送至目标账号。";
            logCallback?.Invoke($"[SUCCESS] {finalMsg}");
            return (true, finalMsg);
        }
        catch (Exception ex)
        {
            var err = $"应用打包或迁移失败: {ex.Message}";
            logCallback?.Invoke($"[ERROR] {err}");
            return (false, err);
        }
        finally
        {
            if (!string.IsNullOrEmpty(zipPath) && File.Exists(zipPath))
            {
                try
                {
                    var pDir = Path.GetDirectoryName(zipPath);
                    if (!string.IsNullOrEmpty(pDir) && Directory.Exists(pDir))
                    {
                        Directory.Delete(pDir, true);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
    }
}
