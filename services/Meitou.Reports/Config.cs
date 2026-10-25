using Npgsql;

namespace Meitou.Reports;

/// <summary>The service's settings, from environment variables (README.md lists them).</summary>
sealed record Config(
    string ConnectionString,
    byte[] Salt,
    string? AdminToken,
    Uri RoadmapUrl,
    string Repo,
    string[] Origins,
    string? TurnstileSecret,
    string? DiscordPublicKey,
    string? DiscordAppId,
    string? DiscordBotToken,
    string[] DiscordTrustedRoles)
{
    public static Config Load(IConfiguration c)
    {
        string? Get(string key) => c[key] is { Length: > 0 } v ? v : null;
        string[] List(string key) => (Get(key) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string connection = Get("ConnectionStrings:Reports") ?? FromUrl(Get("DATABASE_URL"))
            ?? throw new InvalidOperationException("Set ConnectionStrings__Reports (ADO.NET) or DATABASE_URL (postgresql://...).");
        string salt = Get("REPORTS_SALT") ?? throw new InvalidOperationException("Set REPORTS_SALT: a long random string that keeps reporter hashes unguessable.");
        return new Config(
            connection,
            System.Text.Encoding.UTF8.GetBytes(salt),
            Get("REPORTS_ADMIN_TOKEN"),
            new Uri(Get("ROADMAP_URL") ?? "https://meitou.kenshi.wiki/roadmap.json"),
            Get("GITHUB_REPO") ?? "VlcekM/MeitouClient",
            List("ALLOWED_ORIGINS") is { Length: > 0 } o ? o : ["https://meitou.kenshi.wiki"],
            Get("TURNSTILE_SECRET"),
            Get("DISCORD_PUBLIC_KEY"),
            Get("DISCORD_APP_ID"),
            Get("DISCORD_BOT_TOKEN"),
            List("DISCORD_TRUSTED_ROLES"));
    }

    /// <summary>A <c>postgresql://user:pass@host:port/db?sslmode=...</c> URL as an Npgsql connection string.</summary>
    static string? FromUrl(string? url)
    {
        if (url is null) return null;
        var u = new Uri(url);
        var userInfo = u.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = u.Host,
            Port = u.Port > 0 ? u.Port : 5432,
            Database = Uri.UnescapeDataString(u.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            SslMode = SslMode.VerifyFull,
        };
        foreach (var part in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv[0] == "sslmode" && kv.Length == 2)
                b.SslMode = kv[1] switch { "disable" => SslMode.Disable, "require" => SslMode.Require, "verify-ca" => SslMode.VerifyCA, "prefer" => SslMode.Prefer, _ => SslMode.VerifyFull };
        }
        return b.ConnectionString;
    }
}
