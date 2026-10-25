using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Meitou.Reports;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var config = Config.Load(builder.Configuration);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(NpgsqlDataSource.Create(config.ConnectionString));
builder.Services.AddSingleton<Store>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp => new Roadmap(sp.GetRequiredService<IHttpClientFactory>().CreateClient(), config, sp.GetRequiredService<ILogger<Roadmap>>()));
builder.Services.AddSingleton<Intake>();
builder.Services.AddSingleton<Discord>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(config.Origins).WithMethods("GET", "POST").WithHeaders("Content-Type")));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("post", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ => new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(10) }));
});

var app = builder.Build();
await app.Services.GetRequiredService<Store>().Migrate();
app.UseCors();
app.UseRateLimiter();

app.MapGet("/", () => "Meitou play reports. https://github.com/VlcekM/MeitouClient/tree/master/services/Meitou.Reports");
app.MapGet("/health", () => Results.Ok());

// Public: counts per row since the cutoff (reports older than two releases stop counting), for the roadmap page.
app.MapGet("/summary", async (Store store, Roadmap roadmap, HttpResponse response) =>
{
    var since = await roadmap.Cutoff();
    response.Headers.CacheControl = "public, max-age=60";
    return new { since, rows = await store.Summary(since) };
});

// Public: the status changes the reports suggest; a person approves them on the roadmap.
app.MapGet("/proposals", async (Store store, Roadmap roadmap) =>
    Roadmap.Propose(await roadmap.Rows(), await store.Summary(await roadmap.Cutoff())));

// A report from the roadmap page (Turnstile when configured) or the launcher (may carry the end of log.txt).
app.MapPost("/reports", async (HttpContext ctx, ReportBody body, Intake intake, IHttpClientFactory http) =>
{
    bool launcher = body.Source == "launcher";
    if (!launcher && config.TurnstileSecret is { } secret && !await Turnstile(http, secret, body.Turnstile, ClientIp(ctx)))
        return Results.BadRequest(new { error = "bot check failed" });
    var report = new Incoming(body.Row, body.Verdict, body.Build, body.Platform, body.Modded, body.Note, launcher ? body.Log : null);
    var (id, error) = await intake.Add(report, launcher ? "launcher" : "web", intake.Reporter($"ip:{ClientIp(ctx)}"), trusted: false);
    return error is null ? Results.Ok(new { id }) : Results.BadRequest(new { error });
}).RequireRateLimiting("post");

// Admin (Authorization: Bearer REPORTS_ADMIN_TOKEN): the raw reports, a report's log, removing spam, registering the Discord commands.
var admin = app.MapGroup("").AddEndpointFilter(async (ctx, next) =>
    config.AdminToken is { } token && ctx.HttpContext.Request.Headers.Authorization.ToString() == $"Bearer {token}" ? await next(ctx) : Results.Unauthorized());
admin.MapGet("/reports", async (Store store, DateTimeOffset? since, string? row, int? limit) =>
    await store.List(since ?? DateTimeOffset.UtcNow.AddDays(-7), row, Math.Clamp(limit ?? 500, 1, 5000)));
admin.MapGet("/reports/{id:long}/log", async (Store store, long id) =>
    await store.Log(id) is { } log ? Results.Text(Intake.Unzip(log), "text/plain; charset=utf-8") : Results.NotFound());
admin.MapDelete("/reports/{id:long}", async (Store store, long id) => await store.Delete(id) ? Results.NoContent() : Results.NotFound());
admin.MapPost("/discord/register", async (Discord discord) => Results.Text(await discord.Register()));

app.MapPost("/discord", async (HttpRequest request, Discord discord) =>
{
    if (!discord.Enabled) return Results.NotFound();
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    var body = buffer.ToArray();
    if (!discord.Verify(request.Headers["X-Signature-Ed25519"], request.Headers["X-Signature-Timestamp"], body)) return Results.Unauthorized();
    return Results.Json(await discord.Handle(JsonNode.Parse(body)!));
});

app.Run();

// Behind Cloudflare and the Cloudovna edge: Cloudflare's header, else the address the edge appended last, else the socket.
static string ClientIp(HttpContext ctx)
{
    var h = ctx.Request.Headers;
    if (h["CF-Connecting-IP"].ToString() is { Length: > 0 } cf) return cf;
    if (h["X-Forwarded-For"].ToString() is { Length: > 0 } xff) return xff.Split(',')[^1].Trim();
    return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

// Cloudflare Turnstile: the page's widget gives a token, checked here with the secret.
static async Task<bool> Turnstile(IHttpClientFactory http, string secret, string? token, string ip)
{
    if (string.IsNullOrEmpty(token)) return false;
    try
    {
        using var response = await http.CreateClient().PostAsync("https://challenges.cloudflare.com/turnstile/v0/siteverify",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["secret"] = secret, ["response"] = token, ["remoteip"] = ip }));
        var result = await response.Content.ReadFromJsonAsync<JsonObject>();
        return result?["success"]?.GetValue<bool>() == true;
    }
    catch (HttpRequestException) { return false; }
}

/// <summary>The JSON body of <c>POST /reports</c>.</summary>
sealed record ReportBody(string? Row, string? Verdict, string? Build, string? Platform, bool? Modded, string? Note, string? Source, string? Turnstile, string? Log);
