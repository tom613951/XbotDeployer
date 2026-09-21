using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace XbotDeployer.Core;

public record ContactItem(long Id, string Username, string Password, string Remark, string CreatedAt, string? LastUsedAt);

public sealed class ContactsDb
{
    private static readonly byte[] LocalKey = Encoding.UTF8.GetBytes("xbot_deployer_local_key");
    private readonly string _dbPath;

    public ContactsDb(string? dbPath = null)
    {
        _dbPath = dbPath ?? ResolveDefaultDbPath();
        InitDb();
    }

    private static string ResolveDefaultDbPath()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XbotDeployer");
        var appDataDb = Path.Combine(appData, "contacts.db");
        if (File.Exists(appDataDb)) return appDataDb;

        var legacyDb = Path.Combine(AppContext.BaseDirectory, "contacts.db");
        Directory.CreateDirectory(appData);
        if (File.Exists(legacyDb))
        {
            try
            {
                File.Move(legacyDb, appDataDb);
            }
            catch (IOException)
            {
                // The legacy process may still be running; retry migration on the next launch.
            }
        }

        return appDataDb;
    }

    private static string Obfuscate(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(bytes[i] ^ LocalKey[i % LocalKey.Length]);
        }
        return "xbot:" + Convert.ToBase64String(bytes);
    }

    private static string Deobfuscate(string b64Text)
    {
        if (string.IsNullOrEmpty(b64Text) || !b64Text.StartsWith("xbot:")) return b64Text;
        try
        {
            var raw = Convert.FromBase64String(b64Text[5..]);
            for (var i = 0; i < raw.Length; i++)
            {
                raw[i] = (byte)(raw[i] ^ LocalKey[i % LocalKey.Length]);
            }
            return Encoding.UTF8.GetString(raw);
        }
        catch
        {
            return b64Text;
        }
    }

    private SqliteConnection GetConnection() => new($"Data Source={_dbPath}");

    private void InitDb()
    {
        using var conn = GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS user (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                username TEXT UNIQUE NOT NULL,
                password TEXT NOT NULL,
                remark TEXT,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                last_used_at TIMESTAMP
            );";
        cmd.ExecuteNonQuery();
    }

    public List<ContactItem> GetAll()
    {
        var list = new List<ContactItem>();
        using var conn = GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, password, remark, created_at, last_used_at FROM user ORDER BY id DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var user = reader.GetString(1);
            var pwd = Deobfuscate(reader.GetString(2));
            var remark = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var created = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var lastUsed = reader.IsDBNull(5) ? null : reader.GetString(5);
            list.Add(new ContactItem(id, user, pwd, remark, created, lastUsed));
        }
        return list;
    }

    public bool AddOrUpdate(string username, string password, string remark = "")
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return false;

        var encPassword = Obfuscate(password.Trim());
        using var conn = GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO user (username, password, remark, last_used_at)
            VALUES ($u, $p, $r, CURRENT_TIMESTAMP)
            ON CONFLICT(username) DO UPDATE SET
                password = excluded.password,
                remark = excluded.remark,
                last_used_at = CURRENT_TIMESTAMP;";
        cmd.Parameters.AddWithValue("$u", username.Trim());
        cmd.Parameters.AddWithValue("$p", encPassword);
        cmd.Parameters.AddWithValue("$r", remark.Trim());
        cmd.ExecuteNonQuery();
        return true;
    }

    public bool Delete(string username)
    {
        using var conn = GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM user WHERE username = $u";
        cmd.Parameters.AddWithValue("$u", username.Trim());
        return cmd.ExecuteNonQuery() > 0;
    }

    public ContactItem? GetByUsername(string username)
    {
        using var conn = GetConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, username, password, remark, created_at, last_used_at FROM user WHERE username = $u";
        cmd.Parameters.AddWithValue("$u", username.Trim());
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var id = reader.GetInt64(0);
            var user = reader.GetString(1);
            var pwd = Deobfuscate(reader.GetString(2));
            var remark = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var created = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var lastUsed = reader.IsDBNull(5) ? null : reader.GetString(5);
            return new ContactItem(id, user, pwd, remark, created, lastUsed);
        }
        return null;
    }
}
