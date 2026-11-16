using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

int checks=0;void Check(bool value,string label){checks++;if(!value)throw new Exception(label);}
string directory=Path.GetFullPath(args.ElementAtOrDefault(0)??Path.Combine("work","contact-tcp-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(directory);
string dll=Path.GetFullPath(args.ElementAtOrDefault(1)??"outputs/NightStudServer/Server/bin/Release/net10.0/NightStud.Server.dll");
string dotnet=Environment.GetEnvironmentVariable("RUNNER_DOTNET")??Path.GetFullPath("work/server-tools/dotnet/dotnet.exe");
var portProbe=new TcpListener(IPAddress.Loopback,0);portProbe.Start();int port=((IPEndPoint)portProbe.LocalEndpoint).Port;portProbe.Stop();string address="http://127.0.0.1:"+port;
using var http=new HttpClient{BaseAddress=new(address),Timeout=TimeSpan.FromSeconds(5)};Process? server=null;var log=new ConcurrentQueue<string>();
async Task Start(){
 var info=new ProcessStartInfo(dotnet){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=directory,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(dll);
 foreach(string key in info.Environment.Keys.Where(k=>k.StartsWith("NIGHTSTUD_",StringComparison.Ordinal)||k=="SERVER_PORT"||k=="ASPNETCORE_URLS").ToArray())info.Environment.Remove(key);
 info.Environment["ASPNETCORE_URLS"]=address;info.Environment["NIGHTSTUD_DATA_DIR"]=directory;info.Environment["NIGHTSTUD_LOG_DIR"]=Path.Combine(directory,"logs");info.Environment["NIGHTSTUD_SCHEME"]="http";
 server=Process.Start(info)!;server.OutputDataReceived+=(_,e)=>{if(e.Data!=null)log.Enqueue(e.Data);};server.ErrorDataReceived+=(_,e)=>{if(e.Data!=null)log.Enqueue(e.Data);};server.BeginOutputReadLine();server.BeginErrorReadLine();
 for(int n=0;n<100;n++){try{if((await http.GetAsync("health")).IsSuccessStatusCode)return;}catch(HttpRequestException){}await Task.Delay(50);}throw new Exception("server start: "+string.Join("\n",log));
}
async Task Stop(){if(server!=null){if(!server.HasExited){server.Kill(true);await server.WaitForExitAsync();}server.Dispose();server=null;}}
async Task<JsonElement> Request(string path,object body){using var response=await http.PostAsJsonAsync(path,body);string text=await response.Content.ReadAsStringAsync();Check(response.IsSuccessStatusCode,path+": "+text);using var json=JsonDocument.Parse(text);return json.RootElement.Clone();}
async Task<Peer> Login(string user){var result=await Request("api/auth/login",new{username=user,password="contact-test-password"});return await Peer.Connect(address,result.GetProperty("token").GetString()!,result.GetProperty("playerId").GetString()!);}
Peer? a=null,b=null,c=null;
try{
 await Start();foreach(string user in new[]{"tcp_contact_a","tcp_contact_b","tcp_contact_c"})await Request("api/auth/register",new{username=user,password="contact-test-password",name=user});
 a=await Login("tcp_contact_a");b=await Login("tcp_contact_b");c=await Login("tcp_contact_c");
 var sent=await a.Do("contactSend",new(){["target"]=b.Id,["text"]="真实连接的私信"});Check(sent.GetProperty("ok").GetBoolean(),"real TCP private send");await b.Wait(f=>f.GetProperty("type").GetString()=="contactMessage");Check(c.Count("contactMessage")==0,"real third socket receives no private data");
 await b.DisposeAsync();b=null;var offline=await a.Do("contactSend",new(){["target"]=a.Last("contactMessage").GetProperty("message").GetProperty("recipient").GetString(),["text"]="重启后也应保留的留言"});Check(offline.GetProperty("ok").GetBoolean(),"offline message acknowledged");

 string sellerId=a.Id,saveA=new string('1',32),saveB=new string('2',32),saveC=new string('3',32),marketId=Guid.NewGuid().ToString("N");
 string parcel="""{"label":"茶色镜 [3/8]","nodes":[{"identifier":"night_stud.gear.tintFilter","unitCount":1,"children":[],"slots":[],"tags":[{"key":"night_stud.gear.remaining.v1","number":3}],"shape":null,"modified":null}]}""";
 Check((await a.Do("marketOffer",new(){["saveId"]=saveA,["trade"]=marketId,["payload"]=parcel,["price"]=130})).GetProperty("ok").GetBoolean(),"actual TCP uploads a real device into market escrow");
 string originalPhone=a.Last("welcome").GetProperty("phone").GetString()!;
 await a.DisposeAsync();a=null;await c.DisposeAsync();c=null;await Stop();await Start();
 b=await Login("tcp_contact_b");a=await Login("tcp_contact_a");c=await Login("tcp_contact_c");await b.Wait(f=>f.GetProperty("type").GetString()=="contacts");Check(b.Last("contacts").GetProperty("unread").GetInt32()==2,"unread survives actual server process restart");
 await b.Do("contactHistory",new(){["target"]=a.Id});Check(b.Last("contactHistory").GetProperty("messages").GetArrayLength()==2,"both messages restored from actual database");
 long sequence=b.Last("contactHistory").GetProperty("messages")[1].GetProperty("sequence").GetInt64();await b.Do("contactRead",new(){["target"]=a.Id,["sequence"]=sequence});Check(b.Last("contacts").GetProperty("unread").GetInt32()==0,"real read receipt");

 Check(a.Last("welcome").GetProperty("phone").GetString()==originalPhone,"account number remains identical across actual process restart");
 await a.DisposeAsync();a=null;
 Check((await b.Do("marketBrowse",new(){["saveId"]=saveB,["direction"]="buy",["choice"]=0})).GetProperty("ok").GetBoolean(),"buyer browses while original seller is offline");
 Check(b.Last("market").GetProperty("items").EnumerateArray().Any(i=>i.GetProperty("id").GetString()==marketId),"seller's item survives a real server restart");
 Check((await b.Do("cashTransfer",new(){["saveId"]=saveB,["transfer"]=Guid.NewGuid().ToString("N"),["direction"]="in",["amount"]=500})).GetProperty("ok").GetBoolean(),"buyer funds the same production wallet used by card games");
 Check((await b.Do("marketBuy",new(){["saveId"]=saveB,["trade"]=marketId,["price"]=130})).GetProperty("ok").GetBoolean(),"offline seller's listing can be bought through production HTTP/WebSocket server");
 Check(b.Last("market").GetProperty("available").GetInt32()==370&&b.Last("market").GetProperty("receipt").GetProperty("payload").GetString()==parcel,"wire receipt contains exact device state and one debit");
 await b.Do("marketBuy",new(){["saveId"]=saveB,["trade"]=marketId,["price"]=130});Check(b.Last("market").GetProperty("available").GetInt32()==370,"actual network retry cannot pay twice");
 Check(!(await c.Do("marketStatus",new(){["saveId"]=saveC,["trade"]=marketId})).GetProperty("ok").GetBoolean(),"third socket cannot read private purchase payload");
 a=await Login("tcp_contact_a");await a.Do("cashWallet",new(){["saveId"]=saveA});Check(a.Last("cash").GetProperty("available").GetInt32()==130,"offline proceeds reach original seller save after reconnect");
 await b.Do("marketDelivered",new(){["saveId"]=saveB,["trade"]=marketId});await b.Do("marketStatus",new(){["saveId"]=saveB,["trade"]=marketId});Check(b.Last("market").GetProperty("receipt").GetProperty("delivered").GetBoolean()&&b.Last("market").GetProperty("receipt").GetProperty("payload").GetString()=="","real acknowledgement clears only an already delivered item");
 await a.Do("phoneDial",new(){["number"]=b.Last("welcome").GetProperty("phone").GetString()});await b.Wait(f=>f.GetProperty("type").GetString()=="phone");string call=b.Last("phone").GetProperty("id").GetString()!;Check(b.Last("phone").GetProperty("incoming").GetBoolean(),"real incoming call");
 var bad=await c.Do("phoneAccept",new(){["call"]=call});Check(!bad.GetProperty("ok").GetBoolean(),"real third party cannot accept");
 await b.Do("phoneAccept",new(){["call"]=call});Check(b.Last("phone").GetProperty("state").GetString()=="connected","real call accepted");
 await a.Do("contactSend",new(){["target"]=b.Id,["call"]=call,["text"]="接通后的文字"});await b.Wait(f=>f.GetProperty("type").GetString()=="contactMessage");Check(c.Count("contactMessage")==0&&c.Count("phone")==0,"real call remains private");
 string receipt=a.Last("contactMessage").GetProperty("message").GetProperty("id").GetString()!;int priorMessages=a.Count("contactMessage");await a.Do("contactReceipt",new(){["receipt"]=receipt});Check(a.Count("contactMessage")==priorMessages+1,"sender can recover the saved message receipt");
 await c.Do("contactReceipt",new(){["receipt"]=receipt});Check(c.Count("contactMessage")==0,"third party cannot recover sender receipt");
 await b.Do("contactSettings",new(){["allowCalls"]=false,["allowMessages"]=false});Check(b.Last("phone").GetProperty("state").GetString()=="ended","do not disturb hangs up active call");
 Check(!(await a.Do("contactSend",new(){["target"]=b.Id,["text"]="不该收到"})).GetProperty("ok").GetBoolean(),"private message opt-out enforced on TCP");
 foreach(string key in new[]{"target","call","receipt","text"}){var payload=new Dictionary<string,object?>{["target"]=b.Id,["text"]="null probe",[key]=null};Check(!(await a.Do("contactSend",payload)).GetProperty("ok").GetBoolean(),"malformed null field rejected: "+key);}
 Check((await http.GetAsync("health")).IsSuccessStatusCode,"server remains healthy after hostile payloads");
 await b.Do("contactSettings",new(){["allowCalls"]=true,["allowMessages"]=true});
 await b.Do("phoneStart",new(){["target"]=c.Id});await c.Wait(f=>f.GetProperty("type").GetString()=="phone");string secondCall=b.Last("phone").GetProperty("id").GetString()!;await c.DisposeAsync();c=null;await b.Wait(f=>f.GetProperty("type").GetString()=="phone"&&f.GetProperty("state").GetString()=="ended"&&f.GetProperty("id").GetString()==secondCall);Check(b.Last("phone").GetProperty("reason").GetString()=="disconnected","actual socket disconnect ends ringing call");
 File.WriteAllText(Path.Combine(directory,"success.txt"),$"PASS {checks} real HTTP/WebSocket assertions across a server process restart: stable player numbers, direct dialing, offline physical-device market, exact wallet transfer, delivery ownership and retries, private messages and contact privacy. Native UI interaction is tested separately.");Console.WriteLine(File.ReadAllText(Path.Combine(directory,"success.txt")));
}finally{if(a!=null)await a.DisposeAsync();if(b!=null)await b.DisposeAsync();if(c!=null)await c.DisposeAsync();await Stop();File.WriteAllLines(Path.Combine(directory,"server-console.log"),log);}

sealed class Peer:IAsyncDisposable {
 readonly ClientWebSocket socket=new();readonly CancellationTokenSource stop=new();Task pump=Task.CompletedTask;readonly ConcurrentQueue<JsonElement> frames=new();public string Id="";
 public static async Task<Peer> Connect(string address,string token,string id){var p=new Peer{Id=id};await p.socket.ConnectAsync(new Uri(address.Replace("http:","ws:")+"/ws"),p.stop.Token);p.pump=p.Pump();await p.Send(new{type="hello",protocol=1,token,capabilities=new[]{"contacts","storeStakes"},games=new[]{"stud","landlord","mahjong"}});await p.Wait(f=>f.GetProperty("type").GetString()=="welcome");return p;}
 async Task Pump(){try{var buffer=new byte[4096];while(!stop.IsCancellationRequested){using var body=new MemoryStream();WebSocketReceiveResult frame;do{frame=await socket.ReceiveAsync(buffer,stop.Token);if(frame.MessageType==WebSocketMessageType.Close)return;body.Write(buffer,0,frame.Count);if(body.Length>131072)throw new Exception("oversize frame");}while(!frame.EndOfMessage);using var json=JsonDocument.Parse(body.ToArray());frames.Enqueue(json.RootElement.Clone());}}catch(Exception e)when(e is OperationCanceledException or WebSocketException or ObjectDisposedException){}}
 Task Send(object value)=>socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)),WebSocketMessageType.Text,true,stop.Token);
 public JsonElement Last(string type)=>frames.LastOrDefault(f=>f.GetProperty("type").GetString()==type);
 public int Count(string type)=>frames.Count(f=>f.GetProperty("type").GetString()==type);
 public async Task<JsonElement> Wait(Func<JsonElement,bool> predicate){for(int n=0;n<500;n++){var frame=frames.LastOrDefault(predicate);if(frame.ValueKind==JsonValueKind.Object)return frame;await Task.Delay(10);}throw new Exception("wire reply timeout");}
 public async Task<JsonElement> Do(string type,Dictionary<string,object?>? payload=null){payload??=new();string id=Guid.NewGuid().ToString("N");payload["type"]=type;payload["id"]=id;await Send(payload);var ack=await Wait(f=>f.GetProperty("type").GetString()=="ack"&&f.GetProperty("id").GetString()==id);await Task.Delay(15);return ack;}
 public async ValueTask DisposeAsync(){if(stop.IsCancellationRequested)return;socket.Abort();stop.Cancel();await pump;socket.Dispose();stop.Dispose();}
}
