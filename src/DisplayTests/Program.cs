using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightStud.Server;
using NightStud.Shared;

int checks=0;
void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
void Bad(Action act){try{act();throw new Exception("accepted invalid display");}catch(WireFault e){Check(e.Code=="bad_scene",e.Code);}}
string root=Path.GetFullPath(args.ElementAtOrDefault(0)??"work/display065-tests");Directory.CreateDirectory(root);
using var data=JsonDocument.Parse(File.ReadAllText(args.ElementAtOrDefault(1)??"work/native-showcase-steps.json"));
var steps=data.RootElement.GetProperty("steps").EnumerateArray().ToDictionary(s=>s.GetProperty("name").GetString()!,s=>JsonSerializer.Deserialize<ShopScene>(s.GetProperty("scene").GetRawText(),Wire.Json)!);
ShopScene Copy(ShopScene s)=>JsonSerializer.Deserialize<ShopScene>(Wire.Encode(s),Wire.Json)!;
foreach(var s in steps.Values)Check(RoomHub.ValidateShopScene(Wire.Encode(s)).Items.Length==s.Items.Length,"actual native scene accepted");
var display=steps["display-only"];var protectedItem=display.Items.Single(i=>i.Kind=="lou_etched_round");
Check(!protectedItem.CanTrade&&ShopCatalogue.All.ContainsKey(protectedItem.Kind)&&!GoodsLedger.Catalogue.ContainsKey(protectedItem.Kind),"protected display metadata never joins trade whitelist");
var invalid=Copy(display);invalid.Items.Single(i=>i.Id==protectedItem.Id).CanTrade=true;Bad(()=>RoomHub.ValidateShopScene(Wire.Encode(invalid)));
invalid=Copy(display);invalid.Items[0].Kind="remote_untrusted_mod";Bad(()=>RoomHub.ValidateShopScene(Wire.Encode(invalid)));
invalid=Copy(display);invalid.Items[0].Orientation=4;Bad(()=>RoomHub.ValidateShopScene(Wire.Encode(invalid)));
invalid=Copy(display);invalid.Items[0].Orientation=-1;Bad(()=>RoomHub.ValidateShopScene(Wire.Encode(invalid)));
invalid=Copy(display);invalid.Items.Single(i=>i.Kind=="skincare_cream").CanTrade=true;Bad(()=>RoomHub.ValidateShopScene(Wire.Encode(invalid)));
invalid=Copy(display);invalid.Items[0].Quote=-1;Bad(()=>RoomHub.ValidateShopScene(Wire.Encode(invalid)));
Bad(()=>RoomHub.ValidateShopScene(new string(' ',96001)));
invalid=Copy(display);invalid.Zones=[];invalid.Items=Enumerable.Range(0,193).Select(_=>new ShopThing{Id=Guid.NewGuid().ToString("N"),Kind=protectedItem.Kind,Zone="showcase",CanTrade=false,Count=1,W=16,H=16}).ToArray();Bad(()=>RoomHub.ValidateShopScene(Wire.Encode(invalid)));
invalid.Items=invalid.Items.Take(192).ToArray();Check(RoomHub.ValidateShopScene(Wire.Encode(invalid)).Items.Length==192,"bounded display supports full public grids beyond legacy48");
var legacy=JsonNode.Parse(Wire.Encode(steps["legacy-server"]))!;foreach(var i in legacy["items"]!.AsArray()){i!.AsObject().Remove("canTrade");i.AsObject().Remove("orientation");i.AsObject().Remove("flipped");}
Check(RoomHub.ValidateShopScene(legacy.ToJsonString()).Items.All(i=>i.CanTrade&&i.Orientation==0&&!i.Flipped),"legacy positive-price items retain old semantics");
var accounts=new Accounts(Path.Combine(root,Guid.NewGuid().ToString("N")));var hub=new RoomHub(3,null,accounts);
async Task<Peer> Make(string name){var id=await accounts.Register(name,"display-native-qa-long-3810",name);return new Peer(hub,hub.LoginAccount(id));}
await using var host=await Make("displayhost");await using var guest=await Make("displayguest");await using var stranger=await Make("displaystranger");
async Task Ok(Peer p,string type,Action<Command>? c=null){var ack=await p.Command(type,c);Check(ack.GetProperty("ok").GetBoolean(),type+": "+ack);}
async Task No(Peer p,string type,string error,Action<Command>? c=null){var ack=await p.Command(type,c);Check(!ack.GetProperty("ok").GetBoolean()&&ack.GetProperty("error").GetString()==error,type+": "+ack);}
long Rev()=>guest.Last("shopScene").GetProperty("revision").GetInt64();
var frames=new JsonArray();long last=0;
await Ok(host,"privacy",c=>c.AllowVisits=true);
foreach(string name in new[]{"filled","panel-hidden","moved-stacked-rotated","display-only","removed","empty"}){
 await Task.Delay(800);var source=steps[name];await Ok(host,"shopScene",c=>{c.Name="展示柜同步测试";c.Payload=Wire.Encode(source);});
 if(name=="filled")await Ok(guest,"visit",c=>c.Target=host.Session.Id);await guest.Flush();
 var frame=guest.Last("shopScene");var received=frame.GetProperty("scene");
 Check(JsonSerializer.Serialize(received,Wire.Json)==Wire.Encode(source),"authoritative broadcast exactly matches native "+name);
 Check(Rev()>=last,"snapshot revisions ordered");last=Rev();
 Check(!frame.ToString().Contains("energy_credit_hyp"),"private stock absent "+name);
 Check(stranger.Last("shopScene").ValueKind==JsonValueKind.Undefined,"scene not broadcast to unrelated player "+name);
 frames.Add(new JsonObject{{"name",name},{"frame",JsonNode.Parse(frame.GetRawText())}});
 if(name=="filled"){
  var item=source.Items.Single(i=>i.Kind=="processed_juice");await Ok(guest,"shopAsk",c=>{c.Item=item.Id;c.SceneRevision=Rev();});
 }
 if(name=="moved-stacked-rotated")Check(guest.Last("shopAnswer").GetProperty("status").GetString()=="changed","changed stack invalidates old price inquiry");
 if(name=="display-only"){
  await Ok(guest,"shopInspect",c=>{c.Item=protectedItem.Id;c.SceneRevision=Rev();});
  Check(guest.Last("shopFocus").GetProperty("kind").GetProperty("id").GetString()==protectedItem.Kind,"read-only protected item can be inspected");
  await No(guest,"shopAsk","display_only",c=>{c.Item=protectedItem.Id;c.SceneRevision=Rev();});
  var zero=source.Items.Single(i=>i.Kind=="skincare_cream");await No(guest,"shopAsk","display_only",c=>{c.Item=zero.Id;c.SceneRevision=Rev();});
 }
 if(name=="empty")await No(guest,"shopInspect","item_changed",c=>{c.Item=protectedItem.Id;c.SceneRevision=Rev();});
}
using var offer=JsonDocument.Parse(File.ReadAllText(args.ElementAtOrDefault(2)??"work/native-inner-counter-offer.json"));string tradeId=Guid.NewGuid().ToString("N");
await Ok(host,"tradeOffer",c=>{c.Target=guest.Session.Id;c.Trade=tradeId;c.Payload=offer.RootElement.GetProperty("payload").GetString()!;c.Price=17;});await guest.Flush();var proposed=guest.Last("trade").Clone();Check(proposed.GetProperty("state").GetString()=="proposed","real native inner-counter payload enters pending trade");
await Ok(guest,"tradeAccept",c=>c.Trade=tradeId);await host.Flush();var committed=guest.Last("trade").Clone();Check(committed.GetProperty("state").GetString()=="committed"&&host.Last("trade").GetProperty("state").GetString()=="committed","counter purchase committed to both parties");
File.WriteAllText(Path.Combine(root,"frames.json"),new JsonObject{{"host",host.Session.Id},{"guest",guest.Session.Id},{"frames",frames},{"purchase",new JsonObject{{"proposed",JsonNode.Parse(proposed.GetRawText())},{"committed",JsonNode.Parse(committed.GetRawText())}}}}.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
File.WriteAllText(Path.Combine(root,"success.txt"),$"PASS {checks} production backend assertions: captured native inventory steps validated and privately broadcast in order; all metadata, quantities, positions, rotation, read-only protection, old-client defaults, stale inquiry invalidation and empty cabinet. In-memory WebSocket, no TCP listener or campaigns.");Console.WriteLine(File.ReadAllText(Path.Combine(root,"success.txt")));

sealed class Peer:IAsyncDisposable {
 readonly RoomHub hub;readonly Socket socket=new();readonly Client client;readonly Task pump;readonly CancellationTokenSource stop=new();int commands;public readonly Session Session;
 public Peer(RoomHub hub,Session session){this.hub=hub;Session=session;client=new Client(socket);pump=client.Pump(stop.Token);hub.Attach(new Command{Type="hello",Protocol=1,Token=session.Token,Games=["stud"],Capabilities=["sharedShop"]},client);}
 public JsonElement Last(string kind)=>socket.Frames.LastOrDefault(f=>f.GetProperty("type").GetString()==kind);
 public Task Flush()=>Task.Delay(60);
 public async Task<JsonElement> Command(string type,Action<Command>? configure=null){if(++commands%12==0)await Task.Delay(1050);var c=new Command{Type=type,Id=Guid.NewGuid().ToString("N")};configure?.Invoke(c);hub.Handle(Session,client,c);for(int i=0;i<100;i++){await Task.Delay(10);var ack=Last("ack");if(ack.ValueKind==JsonValueKind.Object&&ack.GetProperty("id").GetString()==c.Id){await Flush();return ack;}}throw new Exception("missing ack "+type);}
 public async ValueTask DisposeAsync(){hub.Detach(Session,client);client.Stop();stop.Cancel();await pump;stop.Dispose();}
}
sealed class Socket:WebSocket {
 public readonly System.Collections.Concurrent.ConcurrentQueue<JsonElement> Frames=new();WebSocketState state=WebSocketState.Open;
 public override WebSocketState State=>state;public override WebSocketCloseStatus? CloseStatus=>null;public override string? CloseStatusDescription=>null;public override string? SubProtocol=>null;
 public override void Abort()=>state=WebSocketState.Aborted;public override void Dispose()=>Abort();
 public override Task CloseAsync(WebSocketCloseStatus s,string? t,CancellationToken c){Abort();return Task.CompletedTask;}
 public override Task CloseOutputAsync(WebSocketCloseStatus s,string? t,CancellationToken c)=>CloseAsync(s,t,c);
 public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b,CancellationToken c)=>throw new NotSupportedException();
 public override Task SendAsync(ArraySegment<byte> b,WebSocketMessageType t,bool end,CancellationToken c){using var d=JsonDocument.Parse(b.AsMemory());Frames.Enqueue(d.RootElement.Clone());return Task.CompletedTask;}
}
