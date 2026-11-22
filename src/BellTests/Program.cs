using System.Net.WebSockets;
using System.Text.Json;
using NightStud.Server;
using NightStud.Shared;
int checks=0;
void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
var root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
var scene=JsonSerializer.Deserialize<ShopScene>(File.ReadAllText(args[1]),Wire.Json)!;scene.Generation=Guid.NewGuid().ToString("N");scene.DoorOpen=false;scene.CounterBusy=false;
var accounts=new Accounts(Path.Combine(root,"accounts"));var hub=new RoomHub(3,null,accounts);
Check(Version.TryParse(ServerBuild.Version,out _)&&JsonSerializer.SerializeToElement(hub.Status(),Wire.Json).GetProperty("version").GetString()==ServerBuild.Version,"health uses current assembly version shared with auth options and startup log");
async Task<Peer> Make(string name,bool bell=true){var id=await accounts.Register(name,"test-password-long-6499",name);return new Peer(hub,hub.LoginAccount(id),true,false,bell);}
await using var host=await Make("host");await using var guest=await Make("guest");await using var second=await Make("second");await using var outsider=await Make("outsider");await using var old=await Make("oldhost",false);
async Task Ok(Peer p,string type,Action<Command>? change=null){var a=await p.Command(type,change);Check(a.GetProperty("ok").GetBoolean(),type+": "+a);}
async Task No(Peer p,string type,string error,Action<Command>? change=null){var a=await p.Command(type,change);Check(!a.GetProperty("ok").GetBoolean()&&a.GetProperty("error").GetString()==error,type+" expects "+error+": "+a);}
void Bell(Command c,Peer viewer){var s=viewer.Last("shopScene");c.SceneRevision=s.GetProperty("revision").GetInt64();c.ShopEpoch=s.GetProperty("epoch").GetString()!;}
async Task Publish(Peer owner){await Ok(owner,"shopScene",c=>{c.Name="测试当铺";c.Payload=Wire.Encode(scene);});}
await Ok(host,"privacy",c=>{c.AllowVisits=true;c.AllowInvites=true;});await Publish(host);
await No(outsider,"shopBell","visit_unavailable");
await No(host,"shopBell","visit_scope",c=>Bell(c,host));
await Ok(guest,"visit",c=>c.Target=host.Session.Id);await Ok(second,"visit",c=>c.Target=host.Session.Id);
Check(guest.Last("welcome").GetProperty("features").EnumerateArray().Any(f=>f.GetString()=="shopBell"),"new component receives doorbell feature");
Check(!old.Last("welcome").GetProperty("features").EnumerateArray().Any(f=>f.GetString()=="shopBell"),"old component welcome omits unusable feature");
Check(!old.Last("lobby").GetProperty("features").EnumerateArray().Any(f=>f.GetString()=="shopBell"),"directory does not re-enable unsupported feature");
await No(guest,"shopBell","scene_changed",c=>{Bell(c,guest);c.ShopEpoch=Guid.NewGuid().ToString("N");});
await No(guest,"shopBell","scene_changed",c=>{Bell(c,guest);c.SceneRevision--;});
await Ok(guest,"shopBell",c=>{Bell(c,guest);c.Target=outsider.Session.Id;});
var notice=host.Last("shopBell");var receipt=guest.Last("shopBellSent");var closed=guest.Last("shopScene");
Check(notice.GetProperty("visitor").GetString()==guest.Session.Id&&notice.GetProperty("host").GetString()==host.Session.Id,"server uses actual visiting relationship, ignores forged target");
Check(notice.GetProperty("id").GetString()==receipt.GetProperty("id").GetString(),"owner notification and visitor receipt identify one request");
Check(outsider.Last("shopBell").ValueKind==JsonValueKind.Undefined&&second.Last("shopBell").ValueKind==JsonValueKind.Undefined,"bell notification stays between visitor and host");
Check(!closed.GetProperty("scene").GetProperty("doorOpen").GetBoolean()&&closed.GetProperty("people").EnumerateArray().Single(p=>p.GetProperty("id").GetString()==guest.Session.Id).GetProperty("place").GetString()=="counter","request does not open door or admit guest");
guest.Replay();await guest.Flush();Check(host.Last("shopBell").GetProperty("id").GetString()==notice.GetProperty("id").GetString(),"repeated command never emits a second bell");
await No(guest,"shopBell","shop_bell_wait",c=>Bell(c,guest));await No(second,"shopBell","shop_bell_wait",c=>Bell(c,second));
await Task.Delay(8200);
await Ok(guest,"leaveVisit");await Ok(guest,"visit",c=>c.Target=host.Session.Id);
await No(guest,"shopBell","shop_action_rate",c=>Bell(c,guest));await Ok(second,"shopBell",c=>Bell(c,second));
Check(host.Last("shopBell").GetProperty("visitor").GetString()==second.Session.Id,"another visitor may request after shared cooldown");
await Task.Delay(800);scene.DoorOpen=true;await Publish(host);var opened=guest.Last("shopScene");
await No(guest,"shopBell","door_open",c=>Bell(c,guest));await Ok(guest,"shopEnter",c=>c.SceneRevision=guest.Last("shopScene").GetProperty("revision").GetInt64());await No(guest,"shopBell","visit_scope",c=>Bell(c,guest));
await Ok(host,"privacy",c=>c.AllowVisits=false);await No(second,"shopBell","visit_unavailable");
scene.DoorOpen=false;await Ok(old,"privacy",c=>c.AllowVisits=true);await Publish(old);await Ok(guest,"visit",c=>c.Target=old.Session.Id);
Check(!guest.Last("shopScene").GetProperty("doorbell").GetBoolean(),"old host advertises no bell handler");await No(guest,"shopBell","client_update",c=>Bell(c,guest));
await Ok(host,"privacy",c=>c.AllowVisits=true);await Publish(host);await Ok(old,"visit",c=>c.Target=host.Session.Id);await No(old,"shopBell","client_update",c=>Bell(c,old));
await Ok(guest,"visit",c=>c.Target=host.Session.Id);host.Disconnect();await No(guest,"shopBell","visit_unavailable");
File.WriteAllText(Path.Combine(root,"frames.json"),Wire.Encode(new{closed,notice,receipt,opened}));
string success=$"PASS {checks} assertions: authenticated visitor/owner/capability/privacy/epoch/revision scope; forged target ignored, private request and matched receipt; door and position unchanged; duplicate command; per-shop and per-visitor cooldown; open/inside/unavailable paths; legacy welcome, directory and host compatibility. In-memory production WebSocket peers only, no TCP listener or campaigns.";
File.WriteAllText(Path.Combine(root,"success.txt"),success);Console.WriteLine(success);
sealed class Peer:IAsyncDisposable {
 readonly RoomHub hub;readonly Socket socket=new();readonly Client client;readonly Task pump;readonly CancellationTokenSource stop=new();int commands;public Session Session;
 public Peer(RoomHub hub,Session session,bool shared,bool reception=false,bool bell=true){this.hub=hub;Session=session;client=new Client(socket);pump=client.Pump(stop.Token);hub.Attach(new Command{Type="hello",Protocol=1,Token=session.Token,Games=["stud","three","holdem","blackjack","landlord","mahjong","dice"],Capabilities=shared?bell?["sharedShop","shopBell"]:["sharedShop"]:[]},client);}
 public readonly List<Command> Commands=[];
 public void Replay()=>hub.Handle(Session,client,Commands.Last());
 public void Disconnect()=>hub.Detach(Session,client);
 public JsonElement Last(string kind)=>socket.Frames.LastOrDefault(f=>f.GetProperty("type").GetString()==kind);
 public JsonElement Trade(string id)=>socket.Frames.Last(f=>f.GetProperty("type").GetString()=="trade"&&f.GetProperty("id").GetString()==id);
 public int Count(string kind,string id)=>socket.Frames.Count(f=>f.GetProperty("type").GetString()==kind&&f.TryGetProperty("id",out var value)&&value.GetString()==id);
 public Task Flush()=>Task.Delay(60);
 public async Task<JsonElement> Command(string type,Action<Command>? configure=null){if(++commands%12==0)await Task.Delay(1050);var cmd=new Command{Type=type,Id=Guid.NewGuid().ToString("N")};var state=Last("state");if(state.ValueKind==JsonValueKind.Object&&state.GetProperty("room").ValueKind==JsonValueKind.Object)cmd.Revision=state.GetProperty("room").GetProperty("revision").GetInt64();configure?.Invoke(cmd);Commands.Add(cmd);hub.Handle(Session,client,cmd);for(int i=0;i<100;i++){await Task.Delay(10);var ack=Last("ack");if(ack.ValueKind==JsonValueKind.Object&&ack.GetProperty("id").GetString()==cmd.Id){await Flush();return ack;}}throw new Exception("missing ack "+type);}
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
