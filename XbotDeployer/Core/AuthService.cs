using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace XbotDeployer.Core;

public static class AuthService
{
    private const string DefaultBasicAuth = "Basic c25zOlQ3c3ZGY0lMNGZvUGoxajk=";

    public static async Task<(bool Success, string Message, JsonElement? Data)> LoginShadowBotAsync(
        string username,
        string password,
        string authHeader = DefaultBasicAuth)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return (false, "用户名或密码不能为空", null);
        }

        string encryptedPwd;
        try
        {
            encryptedPwd = CryptoHelper.EncryptPassword(password);
        }
        catch (Exception ex)
        {
            return (false, $"密码 RSA 加密失败: {ex.Message}", null);
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeader);

        var formContent = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("crypt", "metal"),
            new KeyValuePair<string, string>("grant_type", "password"),
            new KeyValuePair<string, string>("scope", "all"),
            new KeyValuePair<string, string>("username", username.Trim()),
            new KeyValuePair<string, string>("password", encryptedPwd)
        });

        try
        {
            var response = await client.PostAsync("https://api.yingdao.com/oauth/token", formContent);
            var resStr = await response.Content.ReadAsStringAsync();

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(resStr);
            }
            catch
            {
                // Non JSON payload falls through to the HTTP status based message below.
            }

            if (doc is not null && doc.RootElement.TryGetProperty("access_token", out var tokenProp) &&
                !string.IsNullOrWhiteSpace(tokenProp.GetString()))
            {
                return (true, "登录成功", doc.RootElement.Clone());
            }

            if (doc is not null)
            {
                var root = doc.RootElement;
                var msg = root.TryGetProperty("msg", out var m) ? m.GetString() :
                          (root.TryGetProperty("error_description", out var ed) ? ed.GetString() : null);
                if (string.IsNullOrEmpty(msg))
                {
                    msg = "账号或密码不正确";
                }
                return (false, $"登录失败: {msg}", null);
            }

            return (false, $"登录失败 (HTTP {response.StatusCode}): {resStr}", null);
        }
        catch (Exception ex)
        {
            return (false, $"登录网络异常: {ex.Message}", null);
        }
    }
}
