using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using NightStud.Server;

try
{
if(args.FirstOrDefault()=="certificate"){await CertificateTool.Run(args.Skip(1).ToArray());return;}
if(args.FirstOrDefault()=="backup"){StorageTool.Backup();return;}
if(args.FirstOrDefault()=="service"){ServiceStatus.Command(args.Skip(1).ToArray());return;}
if(args.FirstOrDefault()=="announce"){Announcements.Command(args.Skip(1).ToArray());return;}
ServerConfig.Load(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "config.env")));
if(Environment.GetEnvironmentVariable("NIGHTSTUD_CERT_PASSWORD_FILE") is string certificatePasswordFile&&certificatePasswordFile.Length>0)
    Environment.SetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__Password",File.ReadAllText(certificatePasswordFile).Trim());
ServerConfig.SetBinding();
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => { options.SingleLine = true; options.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(options => { options.AddServerHeader = false; options.Limits.MaxRequestBodySize = 4096; });
bool legacy=Environment.GetEnvironmentVariable("NIGHTSTUD_ALLOW_LEGACY_LOGIN")=="1";
bool guest=Environment.GetEnvironmentVariable("NIGHTSTUD_ALLOW_GUEST")=="1";
bool webTestUi=Environment.GetEnvironmentVariable("NIGHTSTUD_WEB_TEST_UI")=="1";
var key=legacy?ServerConfig.AccessKey():"";
string dataDirectory=Environment.GetEnvironmentVariable("NIGHTSTUD_DATA_DIR")??Path.Combine(AppContext.BaseDirectory,"..","data");
var accounts=new Accounts(dataDirectory);var moderation=new Moderation(dataDirectory);
var serviceStatus=new ServiceStatus(dataDirectory);
var announcements=new Announcements(dataDirectory);
var steam=new SteamLogin(Environment.GetEnvironmentVariable("NIGHTSTUD_PUBLIC_ORIGIN"));
int grace = int.TryParse(Environment.GetEnvironmentVariable("NIGHTSTUD_RECONNECT_SECONDS"), out var seconds) ? Math.Clamp(seconds, 3, 300) : 90;
int logMb=int.TryParse(Environment.GetEnvironmentVariable("NIGHTSTUD_LOG_MAX_MB"),out var mb)?mb:8;
int logDays=int.TryParse(Environment.GetEnvironmentVariable("NIGHTSTUD_LOG_DAYS"),out var days)?days:14;
using var diskLog=new RollingLog(Environment.GetEnvironmentVariable("NIGHTSTUD_LOG_DIR")??Path.Combine(AppContext.BaseDirectory,"..","logs"),key,logMb,logDays);
builder.Logging.AddProvider(diskLog);
ILogger? hubLogger=null;
var hub = new RoomHub(grace,e=>hubLogger?.LogInformation("{Event} player={Player} room={Room} game={Game} reason={Reason}",e.Event,e.Player,e.Room,e.Game,e.Reason),accounts,moderation);
builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = 429;
    options.GlobalLimiter=PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext,string>(_=>RateLimitPartition.GetConcurrencyLimiter("server",_=>new ConcurrencyLimiterOptions{PermitLimit=256,QueueLimit=0})),
        PartitionedRateLimiter.Create<HttpContext,string>(context=>RateLimitPartition.GetConcurrencyLimiter(context.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new ConcurrencyLimiterOptions{PermitLimit=16,QueueLimit=0})));
    options.AddPolicy("register",context=>RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=5,Window=TimeSpan.FromMinutes(10),QueueLimit=0}));
    options.AddPolicy("poll",context=>RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=90,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions {
            PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("socket", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions {
            PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
hubLogger=app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NightStud.Rooms");
hub.SetAvailability(serviceStatus.Read());
hub.SetAnnouncement(announcements.Read());
app.Use(async (context, next) => {
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; connect-src 'self' ws: wss:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; base-uri 'none'";
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path == "/health")
        context.Response.Headers.CacheControl = "no-store";
    try{await next();}
    catch(WireFault error){
        app.Logger.LogWarning("request_rejected code={Code} trace={Trace}",error.Code,context.TraceIdentifier);
        context.Response.StatusCode=error.Code=="https_required"?426:error.Code is "login_failed" or "account_banned"?401:error.Code.EndsWith("cooldown")||error.Code=="auth_busy"?429:400;
        await context.Response.WriteAsJsonAsync(new{error=error.Code,message=error.Message});
    }
});
app.UseRateLimiter();
app.Use(async(context,next)=>{
    var state=hub.Availability;
    if(state.Status!="online"&&(context.Request.Path=="/ws"||context.Request.Path.StartsWithSegments("/api/auth")&&context.Request.Path!="/api/auth/logout"||context.Request.Path=="/api/sessions")){
        context.Response.StatusCode=503;context.Response.Headers.RetryAfter=state.RetryAfterSeconds.ToString();
        await context.Response.WriteAsJsonAsync(new{error=state.Status,message=state.Message,retryAfterSeconds=state.RetryAfterSeconds});return;
    }
    await next();
});
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(20) });
// Default-off also covers web files left behind by earlier overlay installations.
// Native game APIs and Steam callbacks do not depend on this optional test UI.
if(webTestUi){app.UseDefaultFiles();app.UseStaticFiles();}
app.MapGet("/health", () => Results.Json(hub.Status()));
app.MapGet("/api/announcements",()=>Results.Json(new{announcement=hub.Announcement}));
AuthRoutes.Map(app,accounts,steam,hub,moderation,guest);
app.MapPost("/api/sessions", (Login login,HttpContext context) => {
    if(!legacy)return Results.Json(new{error="client_update_required",message="本服已启用账号登录，请更新客户端。"},statusCode:426);
    if (login.AccessKey is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)), SHA256.HashData(Encoding.UTF8.GetBytes(login.AccessKey))))
    { app.Logger.LogWarning("login_denied trace={Trace}",context.TraceIdentifier);
        return Results.Json(new { error = "access_denied", message = "测试口令不正确。" }, statusCode: 401); }
    try {
        var session = hub.Login(login.Name);
        return Results.Json(new { token = session.Token, playerId = session.Id, name = session.Name, protocol = Wire.Version });
    } catch (WireFault ex) { return Results.Json(new { error = ex.Code, message = ex.Message }, statusCode: 400); }
}).RequireRateLimiting("login");
app.Map("/ws", async (HttpContext context) => {
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    // Browsers use same-origin; native game clients normally omit Origin.
    var origin = context.Request.Headers.Origin.ToString();
    var expectedOrigin = Environment.GetEnvironmentVariable("NIGHTSTUD_PUBLIC_ORIGIN") ?? $"{context.Request.Scheme}://{context.Request.Host}";
    if (origin.Length > 0 && !string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase))
    { context.Response.StatusCode = 403; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var client = new Client(socket);
    Session? session = null;
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
    var pump = client.Pump(stop.Token);
    try {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        handshake.CancelAfter(TimeSpan.FromSeconds(5));
        var first = await Wire.Receive(socket, handshake.Token);
        if (first is null) return;
        session = hub.Attach(JsonSerializer.Deserialize<Command>(first, Wire.Json) ?? throw new WireFault("bad_json", "消息格式无效。"), client);
        while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested) {
            using var idle=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);idle.CancelAfter(TimeSpan.FromSeconds(45));
            var value = await Wire.Receive(socket, idle.Token);
            if (value is null) break;
            client.CheckTraffic(Encoding.UTF8.GetByteCount(value));
            hub.Handle(session, client, JsonSerializer.Deserialize<Command>(value, Wire.Json) ?? throw new WireFault("bad_json", "消息格式无效。"));
        }
    } catch (WireFault ex) {
        app.Logger.LogWarning("handshake_rejected code={Code} trace={Trace}",ex.Code,context.TraceIdentifier);
        client.Send(new { type = "error", error = ex.Code, message = ex.Message });
        await Task.Delay(40, CancellationToken.None);
    } catch (Exception ex) when (ex is JsonException or WebSocketException or OperationCanceledException or IOException or DecoderFallbackException) {
        if(ex is not OperationCanceledException)app.Logger.LogWarning("transport_closed reason={Reason} player={Player}",ex.GetType().Name,session?.Id);
    } catch(Exception ex) {
        app.Logger.LogError(ex,"websocket_failure player={Player} trace={Trace}",session?.Id,context.TraceIdentifier);
    } finally {
        if (session is not null) hub.Detach(session, client);
        client.Stop(); stop.Cancel(); await pump;
    }
}).RequireRateLimiting("socket");
var sweeper = Task.Run(async () => {
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    try { while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping)) {
        try{hub.SetAvailability(serviceStatus.Read());hub.SetAnnouncement(announcements.Read());hub.Expire();}catch(Exception ex){app.Logger.LogError(ex,"room_sweep_failure");}
    } }
    catch (OperationCanceledException) { }
});
app.Logger.LogInformation("NightStud server {Version} / protocol {Protocol}; accounts; practice/store stakes; steamOpenId={Steam}; guest={Guest}; webTestUi={WebTestUi}; reconnect {Seconds}s", ServerBuild.Version, Wire.Version, steam.Enabled,guest,webTestUi,grace);
await app.RunAsync();
await sweeper;

}
catch(Exception error){RollingLog.StartupFailure(error);throw;}
