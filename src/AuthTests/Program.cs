using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightStud.Server;

int checks=0;void Check(bool value,string label){checks++;if(!value)throw new Exception(label);}
async Task Bad(Func<Task> action,string code){try{await action();throw new Exception("accepted "+code);}catch(WireFault e){Check(e.Code==code,code+": "+e.Code);}}
string directory=Path.GetFullPath(args.FirstOrDefault()??"work/auth030-tests");Directory.CreateDirectory(directory);
string data=Path.Combine(directory,Guid.NewGuid().ToString("N"));var accounts=new Accounts(data);var moderation=new Moderation(data);
const string password="correct-Horse-2946!";
var owner=await accounts.Register("shop_owner",password,"店主");var guest=await accounts.Register("shop_guest",password,"访客");
Check((await accounts.Login("SHOP_OWNER",password)).Id==owner.Id,"case folded login");
await Bad(()=>accounts.Register("shop_owner",password,"另一个人"),"username_taken");
await Bad(()=>accounts.Register("x' OR 1=1",password,"测试"),"bad_username");
await Bad(()=>accounts.Register("tiny_password","1234","测试"),"bad_password");
Check((await new Accounts(data).Login("shop_owner",password)).Id==owner.Id,"account survives restart");
using(var db=accounts.Open()){using var cmd=db.CreateCommand();cmd.CommandText="SELECT length(salt),length(hash),iterations FROM accounts LIMIT 1";using var row=cmd.ExecuteReader();Check(row.Read()&&row.GetInt32(0)==16&&row.GetInt32(1)==32&&row.GetInt32(2)==Accounts.Iterations,"salted adaptive password hashes stored");}
for(int i=0;i<6;i++)await Bad(()=>accounts.Login("not_registered","wrong-password-long"),"login_failed");
await Bad(()=>accounts.Login("not_registered","wrong-password-long"),"login_cooldown");
accounts.SetPrivacy(owner.Id,new(true,false));Check(new Accounts(data).GetPrivacy(owner.Id)==new Privacy(true,false),"privacy persists");
string remembered=accounts.Remember(owner);
using(var db=accounts.Open()){using var cmd=db.CreateCommand();cmd.CommandText="SELECT length(secret_hash) FROM remembered_logins";Check(Convert.ToInt32(cmd.ExecuteScalar())==32,"remembered secret stored as hash only");}
var resumed=new Accounts(data).Resume(remembered);Check(resumed.Account.Id==owner.Id&&resumed.Account.Provider=="password","remembered login survives server restart");
Check(resumed.Token!=remembered,"remembered credential rotates");await Bad(()=>Task.FromResult(accounts.Resume(remembered)),"remember_expired");
await Bad(()=>Task.FromResult(accounts.Resume(resumed.Token[..33]+new string('0',64))),"remember_expired");
accounts.Forget(resumed.Token);await Bad(()=>Task.FromResult(accounts.Resume(resumed.Token)),"remember_expired");
string expired=accounts.Remember(owner);
using(var db=accounts.Open()){using var cmd=db.CreateCommand();cmd.CommandText="UPDATE remembered_logins SET expires=$t";cmd.Parameters.AddWithValue("$t",DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));cmd.ExecuteNonQuery();}
await Bad(()=>Task.FromResult(accounts.Resume(expired)),"remember_expired");
string? oldData=Environment.GetEnvironmentVariable("NIGHTSTUD_DATA_DIR");
try{
    Environment.SetEnvironmentVariable("NIGHTSTUD_DATA_DIR",data);string backup=StorageTool.Backup();
    string restored=Path.Combine(directory,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(restored);File.Copy(backup,Path.Combine(restored,"accounts.db"));
    Check((await new Accounts(restored).Login("shop_owner",password)).Id==owner.Id,"consistent online database backup restores account");
    Check(new Accounts(restored).GetPrivacy(owner.Id)==new Privacy(true,false),"database backup restores privacy");
}finally{Environment.SetEnvironmentVariable("NIGHTSTUD_DATA_DIR",oldData);}

var verifier=new VerifySteam();var steam=new SteamLogin("https://casino.example",new HttpClient(verifier));
JsonElement begin=JsonSerializer.SerializeToElement(steam.Begin("Steam 访客"));string state=begin.GetProperty("state").GetString()!,secret=begin.GetProperty("pollSecret").GetString()!;
Check(!begin.GetProperty("url").GetString()!.Contains(secret),"poll secret absent from browser URL");
var flow=steam.Get(state);var fields=new Dictionary<string,string>{
 ["openid.ns"]=SteamLogin.Namespace,["openid.mode"]="id_res",["openid.op_endpoint"]=SteamLogin.Endpoint,
 ["openid.claimed_id"]="https://steamcommunity.com/openid/id/76561198000000001",["openid.identity"]="https://steamcommunity.com/openid/id/76561198000000001",
 ["openid.return_to"]=steam.ReturnTo(flow),["openid.response_nonce"]=DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")+Guid.NewGuid().ToString("N"),
 ["openid.signed"]="op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle",["openid.assoc_handle"]="fixture",["openid.sig"]="fixture-signature"};
var forged=new Dictionary<string,string>(fields){["openid.op_endpoint"]="https://attacker.example"};
await Bad(()=>steam.Verify(flow,forged),"steam_invalid");Check(verifier.Calls==0,"untrusted endpoint never contacted");
verifier.Valid=false;await Bad(()=>steam.Verify(flow,fields),"steam_invalid");Check(steam.Poll(state,secret,accounts)==null,"failed signature cannot create Steam account");
verifier.Valid=true;await steam.Verify(flow,fields);
await Bad(()=>Task.FromResult(steam.Poll(state,new string('x',64),accounts)),"steam_expired");
var steamAccount=steam.Poll(state,secret,accounts)!;Check(steamAccount.Provider=="steam","verified login gains Steam provider");
await Bad(()=>Task.FromResult(steam.Poll(state,secret,accounts)),"steam_expired");

var builder=WebApplication.CreateBuilder();builder.WebHost.UseTestServer();builder.Logging.ClearProviders();
builder.Services.AddRateLimiter(o=>{foreach(string policy in new[]{"register","login","poll"})o.AddPolicy(policy,_=>RateLimitPartition.GetNoLimiter("test"));});
var hub=new RoomHub(3,null,accounts,moderation);await using var app=builder.Build();
app.Use(async(context,next)=>{try{await next();}catch(WireFault e){context.Response.StatusCode=e.Code=="https_required"?426:400;await context.Response.WriteAsJsonAsync(new{error=e.Code});}});
app.UseRateLimiter();AuthRoutes.Map(app,accounts,steam,hub,moderation,false);await app.StartAsync();
using var http=app.GetTestClient();http.BaseAddress=new Uri("https://casino.example");
var login=await http.PostAsJsonAsync("/api/auth/login",new{username="shop_owner",password});Check(login.IsSuccessStatusCode,"HTTPS login route");
JsonElement reply=await login.Content.ReadFromJsonAsync<JsonElement>();Check(reply.GetProperty("provider").GetString()=="password","server assigns account provider");
Check(reply.GetProperty("refreshToken").ValueKind==JsonValueKind.Null,"unchecked remember does not issue durable credential");
var rememberedReply=await (await http.PostAsJsonAsync("/api/auth/login",new{username="shop_owner",password,remember=true})).Content.ReadFromJsonAsync<JsonElement>();
string durable=rememberedReply.GetProperty("refreshToken").GetString()!;
var resumedReply=await (await http.PostAsJsonAsync("/api/auth/refresh",new{refreshToken=durable})).Content.ReadFromJsonAsync<JsonElement>();
Check(resumedReply.GetProperty("playerId").GetString()==owner.Id,"HTTP resume restores same account");
Check(!(await http.PostAsJsonAsync("/api/auth/refresh",new{refreshToken=durable})).IsSuccessStatusCode,"HTTP replay rejected");
string finalCredential=resumedReply.GetProperty("refreshToken").GetString()!;
Check((await http.PostAsJsonAsync("/api/auth/logout",new{refreshToken=finalCredential,token=resumedReply.GetProperty("token").GetString()})).IsSuccessStatusCode,"explicit logout revokes device credential");
Check(!(await http.PostAsJsonAsync("/api/auth/refresh",new{refreshToken=finalCredential})).IsSuccessStatusCode,"logged out device cannot resume");
using var plain=app.GetTestClient();plain.BaseAddress=new Uri("http://casino.example");
Check((int)(await plain.PostAsJsonAsync("/api/auth/login",new{username="shop_owner",password})).StatusCode==426,"remote plaintext password login rejected");
Check(!(await plain.PostAsJsonAsync("/api/auth/guest",new{name="guest"})).IsSuccessStatusCode,"guest default disabled");
await app.StopAsync();

await using var a=new Peer(hub,owner);await using var b=new Peer(hub,guest);await using var s=new Peer(hub,steamAccount);await a.Flush();
await a.Command("playerFilter",c=>c.Filter="steam");Check(a.Lobby.GetProperty("players").GetArrayLength()==1&&a.Lobby.GetProperty("players")[0].GetProperty("provider").GetString()=="steam","server Steam-only directory");
await a.Command("playerFilter",c=>c.Filter="nonSteam");Check(a.Lobby.GetProperty("players").EnumerateArray().All(p=>p.GetProperty("provider").GetString()!="steam"),"server non-Steam directory");
Check((await b.Command("visit",c=>c.Target=a.Session.Id)).GetProperty("ok").GetBoolean()==false,"cannot visit unpublished shop");
Check((await b.Command("create",c=>c.Public=false)).GetProperty("ok").GetBoolean(),"private table create");
Check((await b.Command("invite",c=>c.Target=a.Session.Id)).GetProperty("error").GetString()=="invitations_disabled","server rejects disabled invites");
await b.Command("leave");
await a.Command("privacy",c=>{c.AllowVisits=true;c.AllowInvites=true;});
byte[] jpeg={255,216,255,192,0,17,8,0,1,0,1,3,1,17,0,2,17,0,3,17,0,255,218,0,12,3,1,0,2,0,3,0,0,63,0,0,255,217};
Check(RoomHub.ValidJpeg(jpeg),"bounded JPEG header");var bomb=jpeg.ToArray();bomb[9]=255;bomb[10]=255;Check(!RoomHub.ValidJpeg(bomb),"oversized JPEG dimensions rejected");
await a.Command("shop",c=>{c.Name="测试店铺";c.Payload=Convert.ToBase64String(jpeg);});
await b.Command("visit",c=>c.Target=a.Session.Id);Check(b.Last("visit").GetProperty("host").GetProperty("id").GetString()==a.Session.Id,"authorized shop visit");
await b.Command("chat",c=>c.Text="老板，这个多少钱？");await a.Flush();Check(a.Last("chat").GetProperty("entry").GetProperty("text").GetString()=="老板，这个多少钱？","visit chat reaches owner");
string trade=Guid.NewGuid().ToString("N"),payload="{\"label\":\"瓶装水\",\"nodes\":[{\"identifier\":\"small_bottled_water\",\"unitCount\":1,\"children\":[],\"slots\":[],\"tags\":[]}]}";
await a.Command("tradeOffer",c=>{c.Target=b.Session.Id;c.Trade=trade;c.Price=10;c.Payload=payload;});await b.Flush();Check(b.Last("trade").GetProperty("state").GetString()=="proposed","durable offer");
Check((await s.Command("tradeAccept",c=>c.Trade=trade)).GetProperty("ok").GetBoolean()==false,"other account cannot steal offer");
await b.Command("tradeAccept",c=>c.Trade=trade);Check(b.Last("trade").GetProperty("state").GetString()=="committed","trade commit");
await b.Command("tradeAccept",c=>c.Trade=trade);Check(b.Last("trade").GetProperty("state").GetString()=="committed","duplicate acceptance remains committed");
using(var db=accounts.Open()){using var cmd=db.CreateCommand();cmd.CommandText="SELECT length(payload) FROM trades WHERE id=$i";cmd.Parameters.AddWithValue("$i",trade);Check(Convert.ToInt32(cmd.ExecuteScalar())==0,"finished trade keeps receipt without retaining large item payload");}
await a.Command("privacy",c=>{c.AllowVisits=false;c.AllowInvites=false;});await b.Flush();Check(b.Last("visit").GetProperty("host").ValueKind==JsonValueKind.Null,"closing privacy evicts visitors");
await a.Command("tradeOffer",c=>{c.Target=b.Session.Id;c.Trade=trade;c.Price=10;c.Payload=payload;});Check(a.Last("trade").GetProperty("state").GetString()=="committed","saved seller escrow recovers receipt after visit ended");
Check((await b.Command("visit",c=>c.Target=a.Session.Id)).GetProperty("error").GetString()=="visits_disabled","crafted visit packet cannot bypass privacy");
await Bad(()=>Task.FromResult(moderation.Content("买\u200b卖 身 份 证")),"chat_filtered");
await Bad(()=>Task.FromResult(moderation.Content("https://spam.example")),"chat_filtered");
for(int i=0;i<5;i++)moderation.Check("flood","正常内容");await Bad(()=>Task.FromResult(moderation.Check("flood","刷屏")),"chat_rate");
Check(!RoomHub.ValidLot("{\"$type\":\"System.Diagnostics.Process\",\"nodes\":[]}"),"polymorphic item payload rejected");
string success=$"PASS {checks} assertions: persistent salted passwords, online database backup/restore, remembered login restart/rotation/replay rejection/expiry/logout revocation, duplicate account protection, lockout, HTTPS-only routes, Steam signature/endpoint/nonce and poll-secret validation with mocked Valve verifier, verified identity filters, privacy enforcement, shop/chat/trade ownership, repeated commit, image bounds, prohibited content and spam limits. In-memory HTTP/WebSocket only; real Steam approval remains manual.";
File.WriteAllText(Path.Combine(directory,"success.txt"),success);Console.WriteLine(success);

sealed class VerifySteam:HttpMessageHandler {public bool Valid;public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Calls++;if(request.RequestUri!.AbsoluteUri!=SteamLogin.Endpoint)throw new Exception("unexpected verification host");return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("ns:"+SteamLogin.Namespace+"\nis_valid:"+(Valid?"true":"false")+"\n")});}}
sealed class Peer:IAsyncDisposable {
 readonly RoomHub hub;readonly Socket socket=new();readonly Client client;readonly Task pump;readonly CancellationTokenSource stop=new();public Session Session;public JsonElement Lobby=>Last("lobby");
 public Peer(RoomHub hub,AccountIdentity identity){this.hub=hub;Session=hub.LoginAccount(identity);client=new Client(socket);pump=client.Pump(stop.Token);hub.Attach(new Command{Type="hello",Protocol=1,Token=Session.Token,Games=new[]{"stud","three","holdem","blackjack"}},client);}
 public JsonElement Last(string kind)=>socket.Frames.LastOrDefault(f=>f.GetProperty("type").GetString()==kind);
 public async Task Flush()=>await Task.Delay(100);
 public async Task<JsonElement> Command(string type,Action<Command>? configure=null){await Flush();var room=Last("state");var cmd=new Command{Type=type,Id=Guid.NewGuid().ToString("N")};if(room.ValueKind==JsonValueKind.Object&&room.GetProperty("room").ValueKind==JsonValueKind.Object)cmd.Revision=room.GetProperty("room").GetProperty("revision").GetInt64();configure?.Invoke(cmd);hub.Handle(Session,client,cmd);await Flush();return Last("ack");}
 public async ValueTask DisposeAsync(){hub.Detach(Session,client);client.Stop();stop.Cancel();await pump;stop.Dispose();}
}
sealed class Socket:WebSocket {
 public readonly System.Collections.Concurrent.ConcurrentQueue<JsonElement> Frames=new();WebSocketState state=WebSocketState.Open;
 public override WebSocketState State=>state;public override WebSocketCloseStatus? CloseStatus=>null;public override string? CloseStatusDescription=>null;public override string? SubProtocol=>null;
 public override void Abort()=>state=WebSocketState.Aborted;public override void Dispose()=>Abort();
 public override Task CloseAsync(WebSocketCloseStatus status,string? text,CancellationToken token){Abort();return Task.CompletedTask;}
 public override Task CloseOutputAsync(WebSocketCloseStatus status,string? text,CancellationToken token)=>CloseAsync(status,text,token);
 public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,CancellationToken token)=>throw new NotSupportedException();
 public override Task SendAsync(ArraySegment<byte> buffer,WebSocketMessageType type,bool end,CancellationToken token){using var doc=JsonDocument.Parse(buffer.AsMemory());Frames.Enqueue(doc.RootElement.Clone());return Task.CompletedTask;}
}
