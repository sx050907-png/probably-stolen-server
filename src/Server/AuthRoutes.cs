using System.Net;

namespace NightStud.Server;

public static class AuthRoutes
{
    public sealed record Credentials(string Username,string Password,string Name="店主",bool Remember=false);
    public sealed record SteamRequest(string Name="Steam 玩家");
    public sealed record SteamPoll(string State,string PollSecret,bool Remember=false);
    public sealed record RememberRequest(string RefreshToken,string Token="");
    public static bool Secure(HttpContext context)=>context.Request.IsHttps||context.Connection.RemoteIpAddress is {} ip&&IPAddress.IsLoopback(ip);
    static void RequireSecure(HttpContext context){if(!Secure(context))throw new WireFault("https_required","账号登录需要 HTTPS，请使用服主提供的 HTTPS 地址。");}
    public static object SessionReply(Session session,string? refreshToken=null)=>new{token=session.Token,playerId=session.Id,name=session.Name,provider=session.Provider,protocol=Wire.Version,refreshToken};
    public static void Map(WebApplication app,Accounts accounts,SteamLogin steam,RoomHub hub,Moderation moderation,bool guest){
        app.MapGet("/api/auth/options",()=>Results.Json(new{version=ServerBuild.Version,accounts=true,steam=steam.Enabled,guest,secureRequired=true}));
        app.MapPost("/api/auth/register",async(Credentials credentials,HttpContext context)=>{
            RequireSecure(context);moderation.Content(credentials.Name);var account=await accounts.Register(credentials.Username,credentials.Password,credentials.Name,context.RequestAborted);
            app.Logger.LogInformation("account_registered account={Account}",account.Id);return Results.Json(new{ok=true,message="注册成功，请登录。"});
        }).RequireRateLimiting("register");
        app.MapPost("/api/auth/login",async(Credentials credentials,HttpContext context)=>{
            RequireSecure(context);var account=await accounts.Login(credentials.Username,credentials.Password,context.RequestAborted);
            if(moderation.Banned(account.Id))throw new WireFault("account_banned","该账号已被服主停用。");
            return Results.Json(SessionReply(hub.LoginAccount(account),credentials.Remember?accounts.Remember(account):null));
        }).RequireRateLimiting("login");
        app.MapPost("/api/auth/refresh",(RememberRequest request,HttpContext context)=>{
            RequireSecure(context);var resumed=accounts.Resume(request.RefreshToken);
            if(moderation.Banned(resumed.Account.Id)){accounts.Forget(resumed.Token);throw new WireFault("account_banned","该账号已被服主停用。");}
            return Results.Json(SessionReply(hub.LoginAccount(resumed.Account),resumed.Token));
        }).RequireRateLimiting("login");
        app.MapPost("/api/auth/logout",(RememberRequest request,HttpContext context)=>{
            RequireSecure(context);accounts.Forget(request.RefreshToken);hub.Logout(request.Token);return Results.Json(new{ok=true});
        }).RequireRateLimiting("login");
        app.MapPost("/api/auth/guest",(SteamRequest request,HttpContext context)=>{
            if(!guest)throw new WireFault("guest_disabled","本服使用账号或 Steam 登录。");
            return Results.Json(SessionReply(hub.Login(request.Name)));
        }).RequireRateLimiting("login");
        app.MapPost("/api/auth/steam/begin",(SteamRequest request,HttpContext context)=>{
            RequireSecure(context);moderation.Content(request.Name);return Results.Json(steam.Begin(request.Name));
        }).RequireRateLimiting("login");
        app.MapPost("/api/auth/steam/poll",(SteamPoll request,HttpContext context)=>{
            RequireSecure(context);var account=steam.Poll(request.State,request.PollSecret,accounts);
            if(account==null)return Results.Json(new{pending=true});
            if(moderation.Banned(account.Id))throw new WireFault("account_banned","该账号已被服主停用。");
            return Results.Json(SessionReply(hub.LoginAccount(account),request.Remember?accounts.Remember(account):null));
        }).RequireRateLimiting("poll");
        app.MapGet("/auth/steam/start",(string state,HttpContext context)=>{
            RequireSecure(context);var flow=steam.Get(state);
            context.Response.Cookies.Append("nightstud-steam",flow.State,new CookieOptions{Secure=true,HttpOnly=true,SameSite=SameSiteMode.Lax,MaxAge=TimeSpan.FromMinutes(3),Path="/auth/steam"});
            return Results.Redirect(steam.Authorization(flow));
        }).RequireRateLimiting("poll");
        app.MapGet("/auth/steam/callback",async(HttpContext context)=>{
            RequireSecure(context);var query=context.Request.Query;
            string state=query["state"].ToString();
            if(context.Request.Cookies["nightstud-steam"]!=state)throw new WireFault("steam_invalid","请从游戏内重新打开 Steam 登录。");
            var flow=steam.Get(state);var fields=new Dictionary<string,string>();
            foreach(var pair in query){if(pair.Value.Count!=1||pair.Key.Length>100||pair.Value.ToString().Length>2048)throw new WireFault("steam_invalid","Steam 响应格式无效。");fields[pair.Key]=pair.Value.ToString();}
            await steam.Verify(flow,fields,context.RequestAborted);context.Response.Cookies.Delete("nightstud-steam",new CookieOptions{Path="/auth/steam",Secure=true});
            return Results.Content("<!doctype html><html lang=zh><meta charset=utf-8><title>Steam 登录成功</title><p>Steam 身份已验证。回到游戏后会自动进入大厅。</p></html>","text/html; charset=utf-8");
        }).RequireRateLimiting("poll");
    }
}
