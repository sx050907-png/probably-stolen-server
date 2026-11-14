using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NightStud.Server;
using NightStudOnline;

int checks=0;
void Check(bool condition,string name){checks++;if(!condition)throw new Exception(name);}
string Id()=>Guid.NewGuid().ToString("N");
string directory=Path.GetFullPath(args.ElementAtOrDefault(0)??Path.Combine("work","service-tests-"+Id()));Directory.CreateDirectory(directory);
string serverDll=Path.GetFullPath(args.ElementAtOrDefault(1)??"outputs/NightStudServer/Server/bin/Release/net10.0/NightStud.Server.dll");
string runtime=Environment.GetEnvironmentVariable("RUNNER_DOTNET")??"dotnet";
string webRoot=Path.Combine(AppContext.BaseDirectory,"web-test-ui");
var webFiles=new[]{"index.html","app.js","style.css"};
Check(webFiles.All(name=>File.Exists(Path.Combine(webRoot,name))),"actual browser UI files present for endpoint tests");
var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
string address="http://127.0.0.1:"+port;using var http=new HttpClient{BaseAddress=new Uri(address),Timeout=TimeSpan.FromSeconds(3)};
Process? server=null;
ProcessStartInfo Info(params string[] extra){var start=new ProcessStartInfo(runtime){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=directory};start.ArgumentList.Add(serverDll);foreach(string item in extra)start.ArgumentList.Add(item);if(extra.Length==0){start.ArgumentList.Add("--webroot");start.ArgumentList.Add(webRoot);}start.Environment["NIGHTSTUD_PORT"]=port.ToString();start.Environment.Remove("SERVER_PORT");start.Environment["NIGHTSTUD_SCHEME"]="http";start.Environment["NIGHTSTUD_DATA_DIR"]=directory;start.Environment["NIGHTSTUD_LOG_DIR"]=Path.Combine(directory,"logs");start.Environment["NIGHTSTUD_ALLOW_GUEST"]="1";start.Environment["NIGHTSTUD_ALLOW_LEGACY_LOGIN"]="0";start.Environment.Remove("NIGHTSTUD_WEB_TEST_UI");start.Environment.Remove("NIGHTSTUD_MAINTENANCE");return start;}
async Task Wait(Func<Task<bool>> predicate,string name){for(int i=0;i<100;i++){if(await predicate())return;await Task.Delay(50);}throw new Exception("timeout: "+name);}
async Task<JsonElement> Health(){using var response=await http.GetAsync("health");using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());return doc.RootElement.Clone();}
async Task Start(string? webUi=null){var start=Info();if(webUi!=null)start.Environment["NIGHTSTUD_WEB_TEST_UI"]=webUi;server=Process.Start(start)!;server.BeginOutputReadLine();server.BeginErrorReadLine();await Wait(async()=>{try{return (await Health()).GetProperty("version").GetString()!=null;}catch{return false;}},"server start");}
async Task Stop(){if(server!=null){if(!server.HasExited){server.Kill(true);await server.WaitForExitAsync();}server.Dispose();server=null;}}
async Task SetStatus(string status){using var process=Process.Start(Info("service",status,"测试维护提示"))!;await process.WaitForExitAsync();Check(process.ExitCode==0,"operator service command "+status);await Wait(async()=> (await Health()).GetProperty("service").GetProperty("status").GetString()==status,"service state "+status);}
async Task<JsonElement> Request(string route,object body){using var response=await http.PostAsJsonAsync(route,body);string data=await response.Content.ReadAsStringAsync();Check(response.IsSuccessStatusCode,"HTTP success "+route+" "+(int)response.StatusCode);using var json=JsonDocument.Parse(data);return json.RootElement.Clone();}
async Task Send(ClientWebSocket socket,object value)=>await socket.SendAsync(Encoding.UTF8.GetBytes(Wire.Encode(value)),WebSocketMessageType.Text,true,CancellationToken.None);
async Task<JsonElement> Receive(ClientWebSocket socket,string type){using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(5));var buffer=new byte[200000];for(int n=0;n<30;n++){using var stream=new MemoryStream();WebSocketReceiveResult part;do{part=await socket.ReceiveAsync(buffer,limit.Token);stream.Write(buffer,0,part.Count);}while(!part.EndOfMessage);using var doc=JsonDocument.Parse(stream.ToArray());if(doc.RootElement.GetProperty("type").GetString()==type)return doc.RootElement.Clone();}throw new Exception("missing frame "+type);}
try{
 var policy=new ReconnectPolicy();Check(new[]{policy.Failed(),policy.Failed(),policy.Failed(),policy.Failed(),policy.Failed()}.SequenceEqual(new[]{2,4,8,15,30}),"bounded exponential reconnect delays");Check(policy.Failed()==0&&policy.Paused,"six failures pause automatic retries");policy.Reset();Check(policy.Failures==0&&policy.Failed(60)==60,"manual retry reset and maintenance delay");
 await Start();Check((await Health()).GetProperty("service").GetProperty("status").GetString()=="online","default availability online");
 foreach(string route in new[]{"/","/index.html","/app.js","/style.css"}){using var response=await http.GetAsync(route);Check(response.StatusCode==HttpStatusCode.NotFound,"default blocks leftover browser UI "+route);}
 foreach(string route in new[]{"/auth/steam/start?state=invalid","/auth/steam/callback"}){using var response=await http.GetAsync(route);Check(response.StatusCode==HttpStatusCode.BadRequest,"Steam callback routes retained with UI disabled "+route);}
 await Request("api/auth/register",new{username="service_test",password="only-a-local-test-password",name="停服测试"});
 var identity=await Request("api/auth/login",new{username="service_test",password="only-a-local-test-password",remember=true});string account=identity.GetProperty("playerId").GetString()!,token=identity.GetProperty("token").GetString()!;
 using var socket=new ClientWebSocket();await socket.ConnectAsync(new Uri(address.Replace("http:","ws:")+"/ws"),CancellationToken.None);await Send(socket,new{type="hello",protocol=1,token,games=new[]{"stud"},capabilities=new[]{"storeStakes"}});await Receive(socket,"welcome");
 await Send(socket,new{type="create",id=Id(),ante=10,buyIn=1000,game="stud",economy="practice"});Check((await Receive(socket,"ack")).GetProperty("ok").GetBoolean(),"create before maintenance");var tableState=await Receive(socket,"state");long roomRevision=tableState.GetProperty("room").GetProperty("revision").GetInt64();
 using(var publish=Process.Start(Info("announce","维护预告","<b>只读文字</b> https://example.invalid/test.exe"))!){await publish.WaitForExitAsync();Check(publish.ExitCode==0,"operator publishes text-only announcement");}
 var noticeFrame=await Receive(socket,"announcement");Check(noticeFrame.GetProperty("announcement").GetProperty("body").GetString()!.StartsWith("<b>"),"notice transports literal text without interpreting markup");
 using(var response=await http.GetAsync("api/announcements")){Check(response.IsSuccessStatusCode,"announcement has public read-only GET endpoint");}
 using(var response=await http.PostAsJsonAsync("api/announcements",new{body="attempted remote edit"})){Check(response.StatusCode==HttpStatusCode.MethodNotAllowed,"network announcement publishing is not exposed");}
 Check(!NightStud.Shared.ServerNotice.TryCreate("bad","标题",new string('字',1201),DateTimeOffset.UtcNow.ToString("O"),out _),"oversized bulletin rejected by shared client/server schema");
 await SetStatus("maintenance");Check((await Receive(socket,"service")).GetProperty("status").GetString()=="maintenance","connected players receive maintenance notice");
 await Send(socket,new{type="create",id=Id(),ante=10,buyIn=1000,game="stud"});Check((await Receive(socket,"ack")).GetProperty("error").GetString()=="maintenance","maintenance blocks new rooms");
 await Send(socket,new{type="leave",id=Id(),revision=roomRevision});Check((await Receive(socket,"ack")).GetProperty("ok").GetBoolean(),"maintenance still permits leaving a room");
 using(var response=await http.PostAsJsonAsync("api/auth/login",new{username="service_test",password="only-a-local-test-password"})){Check(response.StatusCode==HttpStatusCode.ServiceUnavailable&&response.Headers.RetryAfter!=null,"maintenance login returns retryable 503");}
 var frames=new ConcurrentQueue<string>();using(var unavailable=new OnlineConnection(address,"测试",null,"guest")){unavailable.Frame=frames.Enqueue;unavailable.Run(CancellationToken.None);Check(frames.Any(s=>s.Contains("\"status\":\"maintenance\"")),"native transport receives explicit maintenance instead of rejecting credentials");}
 await SetStatus("online");await Receive(socket,"service");
 var cache=new ConcurrentQueue<string>();var received=new ConcurrentQueue<string>();int forgot=0;using var connection=new OnlineConnection(address,"测试",null,"password","service_test","only-a-local-test-password",true);connection.Frame=received.Enqueue;connection.RememberCredential=cache.Enqueue;connection.ForgetCredential=()=>Interlocked.Increment(ref forgot);
 using var lifetime=new CancellationTokenSource();var running=Task.Run(()=>connection.Run(lifetime.Token));await Wait(()=>Task.FromResult(received.Any(s=>s.Contains("\"type\":\"welcome\""))),"first native login");
 var accounts=new Accounts(directory);var ledger=new StakeLedger(accounts);string save=Id(),room=Id();ledger.Transfer(account,save,Id(),"in",2000);ledger.Reserve(account,save,room,0,1000);ledger.BeginHand(room,new[]{1000});
 var goods=new GoodsLedger(accounts);string lot=Id();string payload=Wire.Encode(new{label="测试物品",nodes=new[]{new{identifier="processed_juice",unitCount=1,children=Array.Empty<int>(),slots=Array.Empty<int>(),tags=Array.Empty<object>(),shape=(string?)null,modified=(string?)null}}});goods.Offer(account,save,lot,room,1,0,payload,12);
 await Stop();await running.WaitAsync(TimeSpan.FromSeconds(8));Check(forgot==0,"outage does not forget remembered login");
 await Start();Check(new StakeLedger(new Accounts(directory)).Balance(account,save)==2000,"abrupt restart returns unfinished cash to pre-hand wallet");Check(new GoodsLedger(new Accounts(directory)).Find(lot)!.State=="claimable","abrupt restart makes unconfirmed physical stake reclaimable");
 int before=received.Count;await Task.Run(()=>connection.Run(lifetime.Token)).WaitAsync(TimeSpan.FromSeconds(8));Check(received.Skip(before).Any(s=>s.Contains("恢复登录")),"stale session after restart requests refresh instead of looping rejection");
 before=received.Count;running=Task.Run(()=>connection.Run(lifetime.Token));await Wait(()=>Task.FromResult(received.Skip(before).Any(s=>s.Contains("\"type\":\"welcome\""))),"session refresh welcome");Check(cache.Count>=2&&forgot==0,"refresh rotates remembered credential without deleting it");lifetime.Cancel();await running.WaitAsync(TimeSpan.FromSeconds(5));
 await Stop();await Start();Check(new StakeLedger(new Accounts(directory)).Balance(account,save)==2000,"second restart does not duplicate cash recovery");
 await SetStatus("closed");frames.Clear();using(var unavailable=new OnlineConnection(address,"测试",null,"guest")){unavailable.Frame=frames.Enqueue;unavailable.Run(CancellationToken.None);Check(frames.Any(s=>s.Contains("\"status\":\"closed\"")),"operator closure status is distinct from network outage");}
 await Stop();await Start("1");
 foreach(string route in new[]{"/","/index.html","/app.js","/style.css"}){using var response=await http.GetAsync(route);string file=route=="/"?"index.html":route[1..];Check(response.IsSuccessStatusCode&&await response.Content.ReadAsStringAsync()==File.ReadAllText(Path.Combine(webRoot,file)),"explicit opt-in serves actual test UI "+route);}
 foreach(string setting in new[]{"0","true"}){await Stop();await Start(setting);foreach(string route in new[]{"/","/index.html","/app.js","/style.css"}){using var response=await http.GetAsync(route);Check(response.StatusCode==HttpStatusCode.NotFound,"non-opt-in setting blocks test UI "+setting+" "+route);}}
 string proof="PASS "+checks+" checks: actual browser UI default-off and explicit opt-in, native login/WebSocket and Steam routes retained, maintenance, closure, connected notice, room drain, bounded retries, restart session refresh, abrupt cash/goods recovery and duplicate-recovery prevention.";File.WriteAllText(Path.Combine(directory,"success.txt"),proof);Console.WriteLine(proof);
}finally{await Stop();}
