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
Peer? a=null,b=null,c=null,d=null;
async Task<JsonElement> Table(Peer peer,string kind,Dictionary<string,object?>? payload=null){payload??=new();var room=peer.Last("state").GetProperty("room");payload["code"]=room.GetProperty("code").GetString();payload["revision"]=room.GetProperty("revision").GetInt64();return await peer.Do(kind,payload);}
try{
 await Start();foreach(string user in new[]{"voice_tcp_a","voice_tcp_b","voice_tcp_c","voice_tcp_d"})await Request("api/auth/register",new{username=user,password="contact-test-password",name=user});
 a=await Login("voice_tcp_a");b=await Login("voice_tcp_b");c=await Login("voice_tcp_c");d=await Login("voice_tcp_d");
 Check(a.Last("welcome").GetProperty("features").EnumerateArray().Any(f=>f.GetString()=="tableVoice"),"packaged TCP server advertises tableVoice");
 await a.Do("create",new(){["game"]="landlord",["public"]=true});string code=a.Last("state").GetProperty("room").GetProperty("code").GetString()!;
 await b.Do("join",new(){["code"]=code});await c.Do("join",new(){["code"]=code});await Table(a,"ready");await Table(b,"ready");await Table(c,"ready");
 var room=a.Last("state").GetProperty("room");Check(room.GetProperty("phase").GetString()=="playing","three actual WebSocket clients start landlord");
 var peers=new[]{a,b,c};int actor=room.GetProperty("actor").GetInt32();int speaker=(actor+1)%3;long revision=room.GetProperty("revision").GetInt64();
 var accepted=await Table(peers[speaker],"tablePhrase",new(){["move"]="say:coffee"});Check(accepted.GetProperty("ok").GetBoolean(),"off-turn player can manually send coffee over actual TCP");
 foreach(var p in peers){var phrase=await p.Wait(f=>f.GetProperty("type").GetString()=="tablePhrase");Check(phrase.GetProperty("speaker").GetInt32()==speaker&&phrase.GetProperty("phrase").GetString()=="coffee"&&phrase.GetProperty("manual").GetBoolean(),"every peer receives the same authoritative speaker and phrase");}
 Check(d.Count("tablePhrase")==0,"outside socket receives no table phrase");
 var rate=await Table(peers[actor],"tablePhrase",new(){["move"]="say:wait"});Check(!rate.GetProperty("ok").GetBoolean()&&rate.GetProperty("error").GetString()=="phrase_rate","real network room cooldown rejects cross-player spam");
 var path=await Table(peers[speaker],"tablePhrase",new(){["move"]="say:../../external.ogg"});Check(!path.GetProperty("ok").GetBoolean()&&path.GetProperty("error").GetString()=="bad_phrase","actual socket cannot inject arbitrary audio paths");
 var wrong=await peers[speaker].Do("tablePhrase",new(){["code"]="OTHER",["revision"]=revision,["move"]="say:coffee"});Check(!wrong.GetProperty("ok").GetBoolean()&&wrong.GetProperty("error").GetString()=="stale","actual socket cannot address another room");
 Check(a.Last("state").GetProperty("room").GetProperty("revision").GetInt64()==revision,"voice exchange leaves gameplay revision unchanged");
 var text="PASS "+checks+" actual TCP/WebSocket checks against packaged server 0.7.1; authenticated local test accounts; no official server or Steam friend used.";
 File.WriteAllText(Path.Combine(directory,"success.txt"),text);Console.WriteLine(text);
}finally{if(a!=null)await a.DisposeAsync();if(b!=null)await b.DisposeAsync();if(c!=null)await c.DisposeAsync();if(d!=null)await d.DisposeAsync();await Stop();File.WriteAllLines(Path.Combine(directory,"server-console.log"),log);}

sealed class Peer:IAsyncDisposable {
 readonly ClientWebSocket socket=new();readonly CancellationTokenSource stop=new();Task pump=Task.CompletedTask;readonly ConcurrentQueue<JsonElement> frames=new();public string Id="";
 public static async Task<Peer> Connect(string address,string token,string id){var p=new Peer{Id=id};await p.socket.ConnectAsync(new Uri(address.Replace("http:","ws:")+"/ws"),p.stop.Token);p.pump=p.Pump();await p.Send(new{type="hello",protocol=1,token,capabilities=new[]{"contacts"},games=new[]{"stud","landlord","mahjong"}});await p.Wait(f=>f.GetProperty("type").GetString()=="welcome");return p;}
 async Task Pump(){try{var buffer=new byte[4096];while(!stop.IsCancellationRequested){using var body=new MemoryStream();WebSocketReceiveResult frame;do{frame=await socket.ReceiveAsync(buffer,stop.Token);if(frame.MessageType==WebSocketMessageType.Close)return;body.Write(buffer,0,frame.Count);if(body.Length>131072)throw new Exception("oversize frame");}while(!frame.EndOfMessage);using var json=JsonDocument.Parse(body.ToArray());frames.Enqueue(json.RootElement.Clone());}}catch(Exception e)when(e is OperationCanceledException or WebSocketException or ObjectDisposedException){}}
 Task Send(object value)=>socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)),WebSocketMessageType.Text,true,stop.Token);
 public JsonElement Last(string type)=>frames.LastOrDefault(f=>f.GetProperty("type").GetString()==type);
 public int Count(string type)=>frames.Count(f=>f.GetProperty("type").GetString()==type);
 public async Task<JsonElement> Wait(Func<JsonElement,bool> predicate){for(int n=0;n<500;n++){var frame=frames.LastOrDefault(predicate);if(frame.ValueKind==JsonValueKind.Object)return frame;await Task.Delay(10);}throw new Exception("wire reply timeout");}
 public async Task<JsonElement> Do(string type,Dictionary<string,object?>? payload=null){payload??=new();string id=Guid.NewGuid().ToString("N");payload["type"]=type;payload["id"]=id;await Send(payload);var ack=await Wait(f=>f.GetProperty("type").GetString()=="ack"&&f.GetProperty("id").GetString()==id);await Task.Delay(15);return ack;}
 public async ValueTask DisposeAsync(){if(stop.IsCancellationRequested)return;socket.Abort();stop.Cancel();await pump;socket.Dispose();stop.Dispose();}
}
