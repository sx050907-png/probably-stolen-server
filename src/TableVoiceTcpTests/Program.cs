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
var peers=new List<Peer>();
async Task<JsonElement> Table(Peer peer,string kind,Dictionary<string,object?>? payload=null){
 payload??=new();
 for(int attempt=0;attempt<8;attempt++){
  var room=peer.Last("state").GetProperty("room");
  payload["code"]=room.GetProperty("code").GetString();payload["revision"]=room.GetProperty("revision").GetInt64();
  var ack=await peer.Do(kind,payload);
  if(ack.GetProperty("ok").GetBoolean()||ack.GetProperty("error").GetString()!="stale")return ack;
  await Task.Delay(60);
 }
 throw new Exception("No current table snapshot for "+kind);
}
try{
 await Start();foreach(string user in new[]{"voice_tcp_a","voice_tcp_b","voice_tcp_c","voice_tcp_d","voice_tcp_e"}){
  await Request("api/auth/register",new{username=user,password="contact-test-password",name=user});peers.Add(await Login(user));
 }
 Check(peers[0].Last("welcome").GetProperty("features").EnumerateArray().Any(f=>f.GetString()=="tableVoiceAllModes"),"TCP server advertises contextual voice and 3-second cooldown");
 foreach(string mode in new[]{"landlord","stud","three","holdem","blackjack","mahjong","dice"}){
  int capacity=mode=="landlord"?3:mode=="mahjong"?4:2;var seated=peers.Take(capacity).ToArray();var outside=peers[4];
  Check((await seated[0].Do("create",new(){["game"]=mode,["public"]=true})).GetProperty("ok").GetBoolean(),"create "+mode);
  var created=await seated[0].Wait(f=>f.GetProperty("type").GetString()=="state"&&f.GetProperty("room").ValueKind==JsonValueKind.Object&&f.GetProperty("room").GetProperty("game").GetString()==mode);
  string code=created.GetProperty("room").GetProperty("code").GetString()!;
  foreach(var p in seated.Skip(1)){
   Check((await p.Do("join",new(){["code"]=code})).GetProperty("ok").GetBoolean(),"join "+mode);
   await p.Wait(f=>f.GetProperty("type").GetString()=="state"&&f.GetProperty("room").ValueKind==JsonValueKind.Object&&f.GetProperty("room").GetProperty("code").GetString()==code);
  }
  foreach(var p in seated)await p.Wait(f=>f.GetProperty("type").GetString()=="state"&&f.GetProperty("room").ValueKind==JsonValueKind.Object&&f.GetProperty("room").GetProperty("code").GetString()==code&&f.GetProperty("room").GetProperty("players").EnumerateArray().All(s=>s.ValueKind==JsonValueKind.Object));
  foreach(var p in seated)Check((await Table(p,"ready")).GetProperty("ok").GetBoolean(),"ready "+mode);
  foreach(var p in seated)await p.Wait(f=>f.GetProperty("type").GetString()=="state"&&f.GetProperty("room").ValueKind==JsonValueKind.Object&&f.GetProperty("room").GetProperty("code").GetString()==code&&f.GetProperty("room").GetProperty("phase").GetString()=="playing");
  var room=seated[0].Last("state").GetProperty("room");Check(room.GetProperty("phase").GetString()=="playing","real WebSocket peers start "+mode);
  File.WriteAllText(Path.Combine(directory,mode+"-playing.json"),seated[0].Last("state").GetRawText());
  int actor=room.GetProperty("actor").GetInt32(),speaker=(actor+1)%capacity;long revision=room.GetProperty("revision").GetInt64();string phrase=mode=="landlord"?"coffee":"show";
  Check((await Table(seated[speaker],"tablePhrase",new(){["move"]="say:"+phrase})).GetProperty("ok").GetBoolean(),"off-turn manual voice accepted "+mode);
  foreach(var p in seated){
   var received=await p.Wait(f=>f.GetProperty("type").GetString()=="tablePhrase"&&f.GetProperty("code").GetString()==code);
   Check(received.GetProperty("speaker").GetInt32()==speaker&&received.GetProperty("phrase").GetString()==phrase&&received.GetProperty("manual").GetBoolean(),"all table peers agree on speaker and phrase "+mode);
  }
  Check(outside.Count("tablePhrase")==0,"outside socket receives no table voice "+mode);
  var rate=await Table(seated[actor],"tablePhrase",new(){["move"]="say:wait"});Check(!rate.GetProperty("ok").GetBoolean()&&rate.GetProperty("error").GetString()=="phrase_rate","sub-three-second spam blocked "+mode);
  string invalid=mode=="landlord"?"../../external.ogg":mode=="dice"?"ready":"seventeen";
  var bad=await Table(seated[speaker],"tablePhrase",new(){["move"]="say:"+invalid});Check(!bad.GetProperty("ok").GetBoolean()&&bad.GetProperty("error").GetString()=="bad_phrase","inapplicable phrase or external path rejected "+mode);
  await Task.Delay(3100);
  Check((await Table(seated[speaker],"tablePhrase",new(){["move"]="say:wait"})).GetProperty("ok").GetBoolean(),"voice available after three seconds "+mode);
  Check(seated[0].Last("state").GetProperty("room").GetProperty("revision").GetInt64()==revision,"quick voice never changes gameplay revision "+mode);
  foreach(var p in seated)await Table(p,"leave");
 }
 string proof="PASS "+checks+" real TCP/WebSocket checks across all 7 modes: contextual phrases, authenticated speaker, table-only delivery and 3-second cooldown. Local test accounts only.";
 File.WriteAllText(Path.Combine(directory,"success.txt"),proof);Console.WriteLine(proof);
}finally{foreach(var p in peers)await p.DisposeAsync();await Stop();File.WriteAllLines(Path.Combine(directory,"server-console.log"),log);}

sealed class Peer:IAsyncDisposable {
 readonly ClientWebSocket socket=new();readonly CancellationTokenSource stop=new();Task pump=Task.CompletedTask;readonly ConcurrentQueue<JsonElement> frames=new();public string Id="";
 public static async Task<Peer> Connect(string address,string token,string id){var p=new Peer{Id=id};await p.socket.ConnectAsync(new Uri(address.Replace("http:","ws:")+"/ws"),p.stop.Token);p.pump=p.Pump();await p.Send(new{type="hello",protocol=1,token,capabilities=new[]{"contacts"},games=new[]{"stud","three","holdem","blackjack","landlord","mahjong","dice"}});await p.Wait(f=>f.GetProperty("type").GetString()=="welcome");return p;}
 async Task Pump(){try{var buffer=new byte[4096];while(!stop.IsCancellationRequested){using var body=new MemoryStream();WebSocketReceiveResult frame;do{frame=await socket.ReceiveAsync(buffer,stop.Token);if(frame.MessageType==WebSocketMessageType.Close)return;body.Write(buffer,0,frame.Count);if(body.Length>131072)throw new Exception("oversize frame");}while(!frame.EndOfMessage);using var json=JsonDocument.Parse(body.ToArray());frames.Enqueue(json.RootElement.Clone());}}catch(Exception e)when(e is OperationCanceledException or WebSocketException or ObjectDisposedException){}}
 Task Send(object value)=>socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)),WebSocketMessageType.Text,true,stop.Token);
 public JsonElement Last(string type)=>frames.LastOrDefault(f=>f.GetProperty("type").GetString()==type);
 public int Count(string type)=>frames.Count(f=>f.GetProperty("type").GetString()==type);
 public async Task<JsonElement> Wait(Func<JsonElement,bool> predicate){for(int n=0;n<500;n++){var frame=frames.LastOrDefault(predicate);if(frame.ValueKind==JsonValueKind.Object)return frame;await Task.Delay(10);}throw new Exception("wire reply timeout");}
 public async Task<JsonElement> Do(string type,Dictionary<string,object?>? payload=null){payload??=new();string id=Guid.NewGuid().ToString("N");payload["type"]=type;payload["id"]=id;await Send(payload);var ack=await Wait(f=>f.GetProperty("type").GetString()=="ack"&&f.GetProperty("id").GetString()==id);await Task.Delay(15);return ack;}
 public async ValueTask DisposeAsync(){if(stop.IsCancellationRequested)return;socket.Abort();stop.Cancel();await pump;socket.Dispose();stop.Dispose();}
}
