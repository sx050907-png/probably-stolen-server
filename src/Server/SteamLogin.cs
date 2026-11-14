using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NightStud.Server;

// Steam's OpenID flow proves an identity without receiving the user's Steam password.
public sealed class SteamLogin
{
    public const string Endpoint="https://steamcommunity.com/openid/login",Namespace="http://specs.openid.net/auth/2.0";
    public sealed class Flow(string state,string secret,string name,DateTimeOffset expires){
        public string State=state,Secret=secret,Name=name;public DateTimeOffset Expires=expires;public string? SteamId;
    }
    readonly ConcurrentDictionary<string,Flow> pending=new();
    readonly ConcurrentDictionary<string,DateTimeOffset> nonces=new();
    readonly HttpClient client;
    readonly string origin;
    public bool Enabled=>origin.Length>0;
    public SteamLogin(string? publicOrigin,HttpClient? verificationClient=null){
        origin=Uri.TryCreate(publicOrigin,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.UserInfo.Length==0&&uri.AbsolutePath=="/"&&uri.Query.Length==0&&uri.Fragment.Length==0?uri.GetLeftPart(UriPartial.Authority):"";
        client=verificationClient??new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(12)};
    }
    void Clean(){foreach(var pair in pending.Where(p=>p.Value.Expires<DateTimeOffset.UtcNow).ToArray())pending.TryRemove(pair.Key,out _);foreach(var pair in nonces.Where(p=>p.Value<DateTimeOffset.UtcNow.AddMinutes(-10)).ToArray())nonces.TryRemove(pair.Key,out _);}
    public object Begin(string nickname){
        Clean();if(!Enabled)throw new WireFault("steam_unconfigured","服主配置 HTTPS 回调地址后可使用 Steam 登录。");
        if(pending.Count>=256)throw new WireFault("auth_busy","登录人数较多，请稍后再试。");
        string state=Wire.Token(),secret=Wire.Token();var flow=new Flow(state,secret,Accounts.Nickname(nickname),DateTimeOffset.UtcNow.AddMinutes(3));
        pending[state]=flow;return new{state,pollSecret=secret,url=origin+"/auth/steam/start?state="+state,expiresIn=180};
    }
    public Flow Get(string state){Clean();if(!pending.TryGetValue(state,out var flow))throw new WireFault("steam_expired","Steam 登录已过期，请重新点击登录。");return flow;}
    public string ReturnTo(Flow flow)=>origin+"/auth/steam/callback?state="+flow.State;
    public string Authorization(Flow flow){
        var fields=new Dictionary<string,string>{{"openid.ns",Namespace},{"openid.mode","checkid_setup"},{"openid.return_to",ReturnTo(flow)},{"openid.realm",origin+"/"},
            {"openid.identity","http://specs.openid.net/auth/2.0/identifier_select"},{"openid.claimed_id","http://specs.openid.net/auth/2.0/identifier_select"}};
        return Endpoint+"?"+string.Join('&',fields.Select(p=>Uri.EscapeDataString(p.Key)+"="+Uri.EscapeDataString(p.Value)));
    }
    public async Task Verify(Flow flow,IReadOnlyDictionary<string,string> fields,CancellationToken cancel=default){
        string Read(string key)=>fields.TryGetValue("openid."+key,out var value)?value:"";
        if(Read("ns")!=Namespace||Read("mode")!="id_res"||Read("op_endpoint")!=Endpoint||Read("return_to")!=ReturnTo(flow))
            throw new WireFault("steam_invalid","Steam 授权响应无效。");
        var signed=Read("signed").Split(',').ToHashSet(StringComparer.Ordinal);
        if(new[]{"op_endpoint","claimed_id","identity","return_to","response_nonce","assoc_handle"}.Any(k=>!signed.Contains(k))||Read("sig").Length==0)
            throw new WireFault("steam_invalid","Steam 授权签名不完整。");
        var match=Regex.Match(Read("claimed_id"),"^https?://steamcommunity\\.com/openid/id/([0-9]{17})$");
        string nonce=Read("response_nonce");
        if(!match.Success||Read("identity")!=Read("claimed_id")||nonce.Length is <21 or >255||
            !DateTimeOffset.TryParseExact(nonce[..20],"yyyy-MM-dd'T'HH:mm:ss'Z'",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var time)||
            time<DateTimeOffset.UtcNow.AddMinutes(-5)||time>DateTimeOffset.UtcNow.AddMinutes(1)||nonces.ContainsKey(nonce))
            throw new WireFault("steam_invalid","Steam 授权已失效或重复使用。");
        var body=fields.Where(p=>p.Key.StartsWith("openid.",StringComparison.Ordinal)).ToDictionary(p=>p.Key,p=>p.Value);
        body["openid.mode"]="check_authentication";
        using var response=await client.PostAsync(Endpoint,new FormUrlEncodedContent(body),cancel);
        string text=await response.Content.ReadAsStringAsync(cancel);
        if(!response.IsSuccessStatusCode||text.Length>4096||!text.Split('\n').Any(line=>line.TrimEnd('\r')=="is_valid:true")||!nonces.TryAdd(nonce,DateTimeOffset.UtcNow))
            throw new WireFault("steam_invalid","Steam 未通过身份验证，请重新授权。");
        lock(flow){if(flow.SteamId!=null)throw new WireFault("steam_reused","这次授权已经完成。");flow.SteamId=match.Groups[1].Value;}
    }
    public AccountIdentity? Poll(string state,string secret,Accounts accounts){
        var flow=Get(state);
        if(secret.Length!=flow.Secret.Length||!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(secret),System.Text.Encoding.UTF8.GetBytes(flow.Secret)))
            throw new WireFault("steam_expired","Steam 登录会话无效，请重新登录。");
        lock(flow){
            if(flow.SteamId==null)return null;
            if(!pending.TryRemove(state,out _))throw new WireFault("steam_reused","这次授权已经使用。");
            return accounts.Steam(flow.SteamId,flow.Name);
        }
    }
}
