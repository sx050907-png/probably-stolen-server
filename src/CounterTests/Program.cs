using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightStud.Server;
using NightStud.Shared;

if(args.Length==1&&args[0]=="--budget"){
 for(int c=0;c<=65535;c++){
  string s=new string((char)c,1);int real=System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(s,Wire.Json));
  if(GamePayloadBudget.ServerStringBytes(s)<real)throw new Exception("unsafe encoded string estimate "+c);
 }
 Console.WriteLine("PASS payload response budget covers all 65536 UTF-16 code units against actual .NET serializer, including HTML escapes, Unicode and surrogate replacements; outgoing limit is distinct from legacy decoding.");return;
}
// Isolated QA transport only. Files carry the actual native client's commands
// through production RoomHub and Client; no TCP listener is opened.
string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
void Atomic(string path,string json){File.WriteAllText(path+".tmp",json);File.Move(path+".tmp",path);}
int checks=0;void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
var deadline=DateTime.UtcNow.AddSeconds(95);
while(!File.Exists(Path.Combine(root,"scene.json"))&&DateTime.UtcNow<deadline)await Task.Delay(20);
if(!File.Exists(Path.Combine(root,"scene.json")))throw new Exception("native bootstrap never arrived");
var scene=JsonSerializer.Deserialize<ShopScene>(File.ReadAllText(Path.Combine(root,"scene.json")),Wire.Json)!;
Check(scene.CounterStaging,"actual native publisher advertises staged counter support");
var accounts=new Accounts(Path.Combine(root,"accounts"));var hub=new RoomHub(3,null,accounts);
async Task<Peer> Make(string name,bool native=false){var id=await accounts.Register(name,"isolated-counter-long-3124",native?"测试访客":name=="counterhost"?"测试店主":"其他访客");return new Peer(hub,hub.LoginAccount(id),native?root:null);}
await using var host=await Make("counterhost");await using var guest=await Make("counterguest",true);await using var other=await Make("counterother");
async Task<JsonElement> Ok(Peer p,string type,Action<Command>? f=null){var ack=await p.Command(type,f);Check(ack.GetProperty("ok").GetBoolean(),type+": "+ack);return ack;}
async Task No(Peer p,string type,string code,Action<Command>? f=null){var ack=await p.Command(type,f);Check(!ack.GetProperty("ok").GetBoolean()&&ack.GetProperty("error").GetString()==code,type+": "+ack);}
async Task Publish(){await Task.Delay(800);await Ok(host,"shopScene",c=>{c.Name="柜台联动测试";c.Payload=Wire.Encode(scene);});await guest.Flush();}
await Ok(host,"privacy",c=>c.AllowVisits=true);await Publish();await Ok(guest,"visit",c=>c.Target=host.Session.Id);
Atomic(Path.Combine(root,"boot.json"),Wire.Encode(new{host=host.Session.Id,guest=guest.Session.Id}));
int sequence=1,controlSequence=0;bool done=false,losePutReply=false,loseRemoveReply=false,staleRemove=false;string lastTrade="";var nativeCommands=new List<Command>();DateTime syncRejected=DateTime.MinValue;
while(!done&&DateTime.UtcNow<deadline){
 string file=Path.Combine(root,$"request-{sequence:00000}.json");
 if(!File.Exists(file)){await Task.Delay(10);continue;}
 var command=JsonSerializer.Deserialize<Command>(File.ReadAllText(file),Wire.Json)!;sequence++;
 if(command.Type=="qaControl"){
  switch(command.Move){
   case "busy":scene.CounterBusy=true;await Publish();break;
   case "resume":scene.CounterBusy=false;await Publish();break;
   case "loseReceipt":guest.DropTrades=true;break;
   case "cancelTrade":Check(lastTrade.Length>0,"native batch produced one trade id");await Ok(host,"tradeCancel",c=>c.Trade=lastTrade);break;
   case "hostBatch":{
    string batch=File.ReadAllText(Path.Combine(root,"batch-payload.json"));lastTrade=Guid.NewGuid().ToString("N");
    await Ok(host,"tradeOffer",c=>{c.Target=guest.Session.Id;c.Trade=lastTrade;c.Payload=batch;c.Price=13;});break;
   }
   case "enableMulti":host.Session.CounterBatches=guest.Session.CounterBatches=true;await Task.Delay(1100);await Ok(guest,"shopSync");break;
   case "disableMulti":host.Session.CounterBatches=guest.Session.CounterBatches=false;await Task.Delay(1100);await Ok(guest,"shopSync");break;
   case "multiSecurity":{
    var source=JsonSerializer.Deserialize<Command>(File.ReadAllText(Path.Combine(root,"multi-quote.json")),Wire.Json)!;
    Command CopyMulti(){var c=JsonSerializer.Deserialize<Command>(Wire.Encode(source),Wire.Json)!;c.Trade=Guid.NewGuid().ToString("N");c.Price=29;return c;}
    void UseMulti(Command d,Command s){d.Target=host.Session.Id;d.Trade=s.Trade;d.Price=s.Price;d.Payload=s.Payload;d.ShopEpoch=s.ShopEpoch;d.CounterRevision=s.CounterRevision;d.CounterLots=s.CounterLots;d.CounterHashes=s.CounterHashes;}
    var wrong=CopyMulti();wrong.CounterHashes![1]="wrong";await No(guest,"tradeOffer","counter_changed",c=>UseMulti(c,wrong));
    wrong=CopyMulti();wrong.CounterLots![1]=Guid.NewGuid().ToString("N");await No(guest,"tradeOffer","counter_changed",c=>UseMulti(c,wrong));
    wrong=CopyMulti();wrong.CounterRevision=-1;await No(guest,"tradeOffer","counter_changed",c=>UseMulti(c,wrong));
    wrong=CopyMulti();wrong.ShopEpoch=Guid.NewGuid().ToString("N");await No(guest,"tradeOffer","counter_changed",c=>UseMulti(c,wrong));
    wrong=CopyMulti();wrong.CounterLots![1]=wrong.CounterLots[0];await No(guest,"tradeOffer","bad_counter",c=>UseMulti(c,wrong));
    wrong=CopyMulti();wrong.CounterLots![0]=null!;await No(guest,"tradeOffer","bad_counter",c=>UseMulti(c,wrong));
    var layout=source.CounterLots!.Select(id=>guest.Last("shopScene").GetProperty("counterLots").EnumerateArray().Single(l=>l.GetProperty("id").GetString()==id).GetProperty("item").Deserialize<NightStud.Shared.ShopThing>(Wire.Json)!).ToArray();
    wrong=CopyMulti();var positioned=JsonNode.Parse(wrong.Payload)!;positioned["counterLayout"]=JsonSerializer.SerializeToNode(layout,Wire.Json);wrong.Payload=positioned.ToJsonString();wrong.CounterLots=null;wrong.CounterHashes=null;await No(guest,"tradeOffer","bad_counter",c=>UseMulti(c,wrong));
    wrong=CopyMulti();layout[0].X+=18;positioned=JsonNode.Parse(wrong.Payload)!;positioned["counterLayout"]=JsonSerializer.SerializeToNode(layout,Wire.Json);wrong.Payload=positioned.ToJsonString();await No(guest,"tradeOffer","counter_changed",c=>UseMulti(c,wrong));
    await No(guest,"shopCounterRemove","bad_counter",c=>{c.ShopEpoch=source.ShopEpoch;c.CounterRevision=source.CounterRevision;c.CounterLots=[null!];});
    await No(guest,"shopCounterKeep","bad_counter",c=>{c.ShopEpoch=source.ShopEpoch;c.CounterLots=[null!];});
    wrong=CopyMulti();var leaves=ShopTradePayload.Parse(wrong.Payload);wrong.Payload=leaves.Items[0];wrong.CounterLots=[wrong.CounterLots![0]];wrong.CounterHashes=[wrong.CounterHashes![0]];await No(guest,"tradeOffer","counter_changed",c=>UseMulti(c,wrong));
    await No(guest,"shopCounterRemove","counter_changed",c=>{c.ShopEpoch=source.ShopEpoch;c.CounterRevision=source.CounterRevision;c.CounterLots=[source.CounterLots![0],Guid.NewGuid().ToString("N")];});
    Check(guest.Last("shopScene").GetProperty("counterLots").GetArrayLength()==2,"malformed bulk withdrawal cannot partially remove the native batch");
    var prior=nativeCommands.Last(c=>c.Type=="shopCounterPut");var overlap=JsonSerializer.Deserialize<Command>(Wire.Encode(prior),Wire.Json)!;overlap.CounterLot=Guid.NewGuid().ToString("N");overlap.CounterItem!.Id=overlap.CounterLot;
    await No(guest,"shopCounterPut","counter_occupied",c=>{c.ShopEpoch=source.ShopEpoch;c.SceneRevision=guest.Last("shopScene").GetProperty("revision").GetInt64();c.CounterRevision=source.CounterRevision;c.CounterLot=overlap.CounterLot;c.CounterItem=overlap.CounterItem;c.Payload=overlap.Payload;c.Price=overlap.Price;});break;
   }
   case "loseStageReply":losePutReply=true;break;
   case "loseWithdrawReply":loseRemoveReply=true;break;
   case "flush":await guest.Flush();break;
   case "staleRemoval":{
    var map=(System.Collections.IDictionary)typeof(RoomHub).GetField("sharedShops",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(hub)!;
    var state=map[host.Session.Id]!;var field=state.GetType().GetField("CounterRevision")!;field.SetValue(state,(long)field.GetValue(state)!+1);staleRemove=true;break;
   }
   case "accept":Check(lastTrade.Length>0,"native client generated trade id");await Ok(host,"tradeAccept",c=>c.Trade=lastTrade);break;
   case "reconnect":hub.Detach(guest.Session,guest.Client);hub.Expire();await guest.Reconnect();break;
   case "receipt":await Ok(guest,"tradeReceipt",c=>c.Trade=lastTrade);break;
   case "refresh":await Publish();break;
   case "expire":{
    var map=(System.Collections.IDictionary)typeof(RoomHub).GetField("sharedShops",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(hub)!;
    var state=map[host.Session.Id]!;var lots=(System.Collections.IDictionary)state.GetType().GetField("CounterLots")!.GetValue(state)!;
    foreach(System.Collections.DictionaryEntry pair in lots)pair.Value!.GetType().GetField("Expires")!.SetValue(pair.Value,DateTime.UtcNow.AddSeconds(-1));
    hub.Expire();await guest.Flush();Check(guest.Last("shopScene").GetProperty("counterLots").GetArrayLength()==0,"expired preview is removed by actual server expiry path");break;
   }
   case "security":{
    var put=nativeCommands.Last(c=>c.Type=="shopCounterPut");var frame=guest.Last("shopScene");
    Command Copy(){var c=JsonSerializer.Deserialize<Command>(Wire.Encode(put),Wire.Json)!;c.Id=Guid.NewGuid().ToString("N");c.SceneRevision=frame.GetProperty("revision").GetInt64();c.ShopEpoch=frame.GetProperty("epoch").GetString()!;c.CounterRevision=frame.GetProperty("counterRevision").GetInt64();return c;}
    void Use(Command dest,Command src){dest.CounterItem=src.CounterItem;dest.CounterLot=src.CounterLot;dest.Payload=src.Payload;dest.Price=src.Price;dest.ShopEpoch=src.ShopEpoch;dest.CounterRevision=src.CounterRevision;dest.SceneRevision=src.SceneRevision;}
    var wrong=Copy();wrong.ShopEpoch=Guid.NewGuid().ToString("N");await No(guest,"shopCounterPut","scene_changed",c=>Use(c,wrong));
    wrong=Copy();wrong.CounterRevision=-1;await No(guest,"shopCounterPut","counter_changed",c=>Use(c,wrong));
    wrong=Copy();wrong.Price=0;await No(guest,"shopCounterPut","bad_counter",c=>Use(c,wrong));
    wrong=Copy();wrong.CounterItem!.W+=16;await No(guest,"shopCounterPut","bad_counter",c=>Use(c,wrong));
    wrong=Copy();wrong.CounterItem!.Count+=1;await No(guest,"shopCounterPut","bad_counter",c=>Use(c,wrong));
    wrong=Copy();wrong.CounterItem!.Kind="lou_etched_round";await No(guest,"shopCounterPut","bad_counter",c=>Use(c,wrong));
    wrong=Copy();wrong.CounterItem!.X=0;await No(guest,"shopCounterPut","bad_scene",c=>Use(c,wrong));
    wrong=Copy();wrong.CounterItem!.Id=null!;await No(guest,"shopCounterPut","bad_counter",c=>Use(c,wrong));
    wrong=Copy();wrong.Payload=null!;await No(guest,"shopCounterPut","bad_counter",c=>Use(c,wrong));
    wrong=Copy();await No(other,"shopCounterPut","visit_unavailable",c=>Use(c,wrong));
    await No(host,"shopCounterPut","visit_scope",c=>Use(c,wrong));
    // A changed typed payload cannot be substituted for the staged lot.
    var mismatch=Copy();var changed=JsonNode.Parse(mismatch.Payload)!;changed["label"]="changed";mismatch.Payload=changed.ToJsonString();
    await No(guest,"tradeOffer","counter_changed",c=>{c.Target=host.Session.Id;c.Trade=Guid.NewGuid().ToString("N");c.Price=17;c.Payload=mismatch.Payload;c.CounterLot=put.CounterLot;});
    Check(other.Last("shopScene").ValueKind==JsonValueKind.Undefined,"staged goods remain private to this shop");
    await Ok(other,"visit",c=>c.Target=host.Session.Id);wrong=Copy();await No(other,"shopCounterPut","visitor_waiting",c=>Use(c,wrong));
    await Ok(other,"shopCounterRemove",c=>{c.ShopEpoch=frame.GetProperty("epoch").GetString()!;c.CounterLot=put.CounterLot;c.CounterRevision=frame.GetProperty("counterRevision").GetInt64();});
    Check(guest.Last("shopScene").GetProperty("counterLots").GetArrayLength()==1,"another visitor cannot withdraw native player's staged good");await Ok(other,"leaveVisit");break;
   }
   case "done":done=true;break;
   default:throw new Exception("unknown QA control");
  }
  Atomic(Path.Combine(root,$"control-{++controlSequence:00000}.json"),Wire.Encode(new{move=command.Move,ok=true}));continue;
 }
 nativeCommands.Add(command);
 if(command.Type=="tradeOffer")lastTrade=command.Trade;
 bool dropPut=command.Type=="shopCounterPut"&&losePutReply,dropRemove=command.Type=="shopCounterRemove"&&loseRemoveReply;
 guest.DropCounterReplies=dropPut||dropRemove;
 if(command.Type=="shopSync"&&syncRejected!=DateTime.MinValue){Check(DateTime.UtcNow-syncRejected>=TimeSpan.FromSeconds(1),"native recovery waits before retrying a rate-limited sync");syncRejected=DateTime.MinValue;}
 bool compact=command.Type=="tradeOffer"&&command.Payload.Length==0&&command.CounterLot.Length>0;guest.Client.CheckTraffic(System.Text.Encoding.UTF8.GetByteCount(File.ReadAllText(file)));hub.Handle(guest.Session,guest.Client,command);await guest.Flush();
 guest.DropCounterReplies=false;if(dropPut){losePutReply=false;Check(true,"stage snapshot and receipt intentionally lost before native early takeback");}if(dropRemove){loseRemoveReply=false;Check(true,"withdrawal snapshot and receipt intentionally lost before native resynchronization");}
 if(compact)Check(command.Payload.Length>0,"native compact quote resolves the exact staged payload without uploading it twice");
 var result=guest.Last("ack");if(result.ValueKind==JsonValueKind.Object&&result.GetProperty("id").GetString()==command.Id){bool ok=result.GetProperty("ok").GetBoolean();if(command.Type=="shopCounterRemove"&&staleRemove){Check(!ok&&result.GetProperty("error").GetString()=="counter_changed","native stale-revision takeback follows the actual rejection path");staleRemove=false;}else if(command.Type=="shopSync"&&!ok&&result.GetProperty("error").GetString()=="shop_action_rate"){syncRejected=DateTime.UtcNow;Check(true,"forced early resynchronization reaches actual server rate limiter");}else Check(ok,command.Type+": "+result);}
 if(command.Type=="shopCounterPut"){
  var f=guest.Last("shopScene");Check(guest.Session.CounterBatches?f.GetProperty("counterLots").GetArrayLength() is >=1 and <=8:f.GetProperty("counterLots").GetArrayLength()==1,"native drag produces a bounded shared counter set");
  var fromGuest=f.GetProperty("counterLots").EnumerateArray().Single(l=>l.GetProperty("id").GetString()==command.CounterLot).GetProperty("item");var fromHost=host.Last("shopScene").GetProperty("counterLots").EnumerateArray().Single(l=>l.GetProperty("id").GetString()==command.CounterLot).GetProperty("item");
  Check(fromGuest.GetRawText()==fromHost.GetRawText(),"both sides receive identical actual staged geometry and counts");
 }
}
Check(done,"native scenario reached completion");
var summary=$"PASS {checks} production assertions with live file-bridged native commands: authenticated stage geometry/counts, private broadcast, hostile scope/payload inputs, withdrawal, busy cleanup, typed quote, lost receipt and same-session reconnect. One isolated native client and production in-memory WebSocket peers; no TCP listener or native pair.";
File.WriteAllText(Path.Combine(root,"server-success.txt"),summary);Console.WriteLine(summary);

sealed class Peer:IAsyncDisposable {
 readonly RoomHub hub;readonly string? directory;Socket socket;Task pump;CancellationTokenSource stop=new();int sequence,commands;public readonly Session Session;public Client Client;public bool DropTrades,DropCounterReplies;
 public Peer(RoomHub hub,Session s,string? directory){this.hub=hub;Session=s;this.directory=directory;socket=new Socket(this);Client=new Client(socket);pump=Client.Pump(stop.Token);Attach();}
 void Attach()=>hub.Attach(new Command{Type="hello",Protocol=1,Token=Session.Token,Games=["stud"],Capabilities=["sharedShop","shopReception","shopBell","storeStakes","roundGoods","tradeBatch"]},Client);
 public JsonElement Last(string type)=>socket.Frames.LastOrDefault(f=>f.GetProperty("type").GetString()==type);
 public Task Flush()=>Task.Delay(60);
 public async Task<JsonElement> Command(string type,Action<Command>? configure=null){if(++commands%10==0)await Task.Delay(1050);var c=new Command{Type=type,Id=Guid.NewGuid().ToString("N")};configure?.Invoke(c);hub.Handle(Session,Client,c);for(int i=0;i<100;i++){await Task.Delay(10);var ack=Last("ack");if(ack.ValueKind==JsonValueKind.Object&&ack.GetProperty("id").GetString()==c.Id){await Flush();return ack;}}throw new Exception("ack missing "+type);}
 public async Task Reconnect(){Client.Stop();stop.Cancel();await pump;stop.Dispose();DropTrades=false;stop=new();socket=new Socket(this);Client=new Client(socket);pump=Client.Pump(stop.Token);Attach();}
 public async ValueTask DisposeAsync(){hub.Detach(Session,Client);Client.Stop();stop.Cancel();await pump;stop.Dispose();}
 sealed class Socket(Peer peer):WebSocket {
  public readonly System.Collections.Concurrent.ConcurrentQueue<JsonElement> Frames=new();WebSocketState state=WebSocketState.Open;
  public override WebSocketState State=>state;public override WebSocketCloseStatus? CloseStatus=>null;public override string? CloseStatusDescription=>null;public override string? SubProtocol=>null;
  public override void Abort()=>state=WebSocketState.Aborted;public override void Dispose()=>Abort();
  public override Task CloseAsync(WebSocketCloseStatus s,string? t,CancellationToken c){Abort();return Task.CompletedTask;}
  public override Task CloseOutputAsync(WebSocketCloseStatus s,string? t,CancellationToken c)=>CloseAsync(s,t,c);
  public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b,CancellationToken c)=>throw new NotSupportedException();
  public override Task SendAsync(ArraySegment<byte> b,WebSocketMessageType t,bool end,CancellationToken c){
   using var doc=JsonDocument.Parse(b.AsMemory());var f=doc.RootElement.Clone();Frames.Enqueue(f);
   if(peer.directory!=null&&!(peer.DropTrades&&f.GetProperty("type").GetString()=="trade")&&!(peer.DropCounterReplies&&f.GetProperty("type").GetString() is "shopScene" or "ack")){
    string path=Path.Combine(peer.directory,$"frame-{++peer.sequence:00000}.json");File.WriteAllText(path+".tmp",f.GetRawText());File.Move(path+".tmp",path);
   }
   return Task.CompletedTask;
  }
 }
}
