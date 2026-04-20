using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YouTubeTool.Services;

public class ChromeCookieService
{
    private static readonly string[] UserDataPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "User Data"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Edge", "User Data"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BraveSoftware", "Brave-Browser", "User Data"),
    ];

    // Returns YouTube session cookies needed for InnerTube auth.
    // Tries Chrome first, then Edge, then Brave.
    public async Task<Dictionary<string, string>> GetYouTubeCookiesAsync()
    {
        var log = new System.Text.StringBuilder();
        foreach (var userDataPath in UserDataPaths)
        {
            log.AppendLine($"Checking: {userDataPath} — exists={Directory.Exists(userDataPath)}");
            if (!Directory.Exists(userDataPath)) continue;
            try
            {
                var cookies = await ExtractCookiesAsync(userDataPath, log);
                log.AppendLine($"  Got {cookies.Count} cookies. Has SAPISID={cookies.ContainsKey("SAPISID")}");
                if (cookies.ContainsKey("SAPISID"))
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "yt_cookies_debug.txt"), log.ToString());
                    return cookies;
                }
            }
            catch (Exception ex)
            {
                log.AppendLine($"  ERROR: {ex.GetType().Name}: {ex.Message}");
            }
        }

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "yt_cookies_debug.txt"), log.ToString());
        return [];
    }

    private static async Task<Dictionary<string, string>> ExtractCookiesAsync(string userDataPath, System.Text.StringBuilder log)
    {
        // Read AES key from Local State (encrypted with Windows DPAPI)
        var localStatePath = Path.Combine(userDataPath, "Local State");
        var localStateJson = await File.ReadAllTextAsync(localStatePath);
        using var doc = JsonDocument.Parse(localStateJson);

        var encryptedKeyB64 = doc.RootElement
            .GetProperty("os_crypt")
            .GetProperty("encrypted_key")
            .GetString()!;

        var encryptedKey = Convert.FromBase64String(encryptedKeyB64);
        var dpApiBytes = encryptedKey[5..]; // strip "DPAPI" prefix
        var aesKey = ProtectedData.Unprotect(dpApiBytes, null, DataProtectionScope.CurrentUser);

        // Copy cookies DB to temp file — Chrome may have the original locked
        var cookiesDb = Path.Combine(userDataPath, "Default", "Network", "Cookies");
        if (!File.Exists(cookiesDb))
            cookiesDb = Path.Combine(userDataPath, "Default", "Cookies");
        log.AppendLine($"  Cookies DB: {cookiesDb} — exists={File.Exists(cookiesDb)}");

        // Copy to a temp file first — Chrome holds an exclusive SQLite lock on the original.
        // FileShare.ReadWrite allows us to read past it at the OS level.
        var tempDb = Path.GetTempFileName();
        try
        {
            using (var src = new FileStream(cookiesDb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = new FileStream(tempDb, FileMode.Create, FileAccess.Write, FileShare.None))
                await src.CopyToAsync(dst);

            return await ReadYouTubeCookiesAsync(tempDb, aesKey, log);
        }
        finally
        {
            try { File.Delete(tempDb); } catch { }
        }
    }

    private static Task<Dictionary<string, string>> ReadYouTubeCookiesAsync(
        string dbPath, byte[] aesKey, System.Text.StringBuilder log)
    {
        var cookies = new Dictionary<string, string>();

        log.AppendLine($"  Opening with IMMUTABLE flag: {dbPath}");

        // SQLITE_OPEN_READONLY | SQLITE_OPEN_IMMUTABLE
        // IMMUTABLE skips WAL/SHM files entirely so Chrome's exclusive lock on them is irrelevant
        const int SQLITE_OPEN_READONLY   = 0x00000001;
        const int SQLITE_OPEN_IMMUTABLE  = 0x00200000;
        const int SQLITE_ROW = 100;

        SQLitePCL.Batteries_V2.Init();

        var rc = SQLitePCL.raw.sqlite3_open_v2(dbPath, out var db,
            SQLITE_OPEN_READONLY | SQLITE_OPEN_IMMUTABLE, null);
        if (rc != 0)
            throw new Exception($"SQLite open failed (code {rc}) — Chrome may have the database locked.");

        try
        {
            rc = SQLitePCL.raw.sqlite3_prepare_v2(db,
                "SELECT name, encrypted_value FROM cookies WHERE host_key LIKE '%.youtube.com'",
                out var stmt);
            if (rc != 0)
                throw new Exception($"SQLite prepare failed (code {rc})");

            try
            {
                while (SQLitePCL.raw.sqlite3_step(stmt) == SQLITE_ROW)
                {
                    var name = SQLitePCL.raw.sqlite3_column_text(stmt, 0).utf8_to_string();
                    var blob = SQLitePCL.raw.sqlite3_column_blob(stmt, 1).ToArray();
                    var value = DecryptCookieValue(blob, aesKey);
                    if (value != null)
                        cookies[name] = value;
                }
            }
            finally
            {
                SQLitePCL.raw.sqlite3_finalize(stmt);
            }
        }
        finally
        {
            SQLitePCL.raw.sqlite3_close(db);
        }

        return Task.FromResult(cookies);
    }

    // Chrome v80+ AES-256-GCM cookie decryption
    private static string? DecryptCookieValue(byte[] encrypted, byte[] aesKey)
    {
        try
        {
            if (encrypted.Length < 3 + 12 + 16) return null;
            var prefix = Encoding.UTF8.GetString(encrypted, 0, 3);
            if (prefix is not ("v10" or "v11")) return null;

            var nonce = encrypted[3..15];             // 12 bytes IV
            var ciphertextWithTag = encrypted[15..];
            var tag = ciphertextWithTag[^16..];       // last 16 bytes
            var ciphertext = ciphertextWithTag[..^16];

            var plaintext = new byte[ciphertext.Length];
            using var aesGcm = new AesGcm(aesKey, 16);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch { return null; }
    }

    // Compute the SAPISIDHASH auth header for InnerTube
    public static string BuildSapiSidHash(string sapisid)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var input = $"{timestamp} {sapisid} https://www.youtube.com";
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        return $"SAPISIDHASH {timestamp}_{Convert.ToHexString(hash).ToLower()}";
    }
}
