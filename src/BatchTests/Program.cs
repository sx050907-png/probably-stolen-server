using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightStud.Server;
using NightStud.Shared;

int checks=0;void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
string payload=File.ReadAllText(args[1]);var batch=JsonNode.Parse(payload)!.AsObject();
string leaf=batch["items"]![0]!.GetValue<string>();
Check(ShopTradePayload.Parse(payload).Items.Length==2,"actual native batch payload accepted");
Check(ShopTradePayload.Parse(leaf).Items.Length==1,"ordinary legacy single lot remains accepted");
void Bad(string input,string code="bad_lot"){try{ShopTradePayload.Parse(input);throw new Exception("accepted invalid batch");}catch(WireFault ex){Check(ex.Code==code,ex.Code+" instead of "+code);}}
void Mutate(Action<JsonObject> edit,string code="bad_lot"){var copy=batch.DeepClone().AsObject();edit(copy);Bad(copy.ToJsonString(),code);}
Mutate(b=>b["tradeBatch"]=2);Mutate(b=>b["tradeBatch"]="1");Mutate(b=>b.Remove("label"));Mutate(b=>b["label"]=new string('a',129));Mutate(b=>b["unknown"]=1);
Mutate(b=>b["items"]=new JsonArray());Mutate(b=>b["items"]=new JsonArray(leaf));Mutate(b=>b["items"]=new JsonArray(Enumerable.Repeat(leaf,9).Select(p=>(JsonNode?)JsonValue.Create(p)).ToArray()));
Mutate(b=>b["items"]![0]=null);Mutate(b=>b["items"]![1]=payload);Bad(payload.Replace("\"tradeBatch\":1","\"tradeBatch\":1,\"tradeBatch\":1"));Bad(new string(' ',60001));
void Node(Action<JsonObject> edit,string code="bad_lot"){Mutate(b=>{var item=JsonNode.Parse(b["items"]![1]!.GetValue<string>())!.AsObject();edit(item["nodes"]![0]!.AsObject());b["items"]![1]=item.ToJsonString();},code);}
Node(n=>n["identifier"]="lou_etched_round","protected_lot");Node(n=>n["unitCount"]=0);Node(n=>n["unitCount"]=1001);Node(n=>n["name"]=new string('x',129));Node(n=>n["name"]=3);
Node(n=>{n["children"]=new JsonArray(0);n["slots"]=new JsonArray(0);});Node(n=>n["tags"]!.AsArray().Add(new JsonObject{{"key","quest_private"}}),"protected_lot");
var expanded=JsonNode.Parse(leaf)!.AsObject();var nodes=new JsonArray();
for(int i=0;i<9;i++){var n=expanded["nodes"]![0]!.DeepClone();n["children"]=i==0?new JsonArray(Enumerable.Range(1,8).Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()):new JsonArray();n["slots"]=i==0?new JsonArray(Enumerable.Range(0,8).Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray()):new JsonArray();nodes.Add(n);}
expanded["nodes"]=nodes;Mutate(b=>b["items"]=new JsonArray(Enumerable.Repeat(expanded.ToJsonString(),8).Select(p=>(JsonNode?)JsonValue.Create(p)).ToArray()));
Check(!RoomHub.ValidLot(payload),"batch envelope cannot enter the single-lot card stake path");

string root=Path.Combine(Path.GetFullPath(args[0]),Guid.NewGuid().ToString("N"));var accounts=new Accounts(root);
var seller=await accounts.Register("batchseller","batch-test-long-password","卖家");var buyer=await accounts.Register("batchbuyer","batch-test-long-password","买家");var old=await accounts.Register("batchlegacy","batch-test-long-password","旧版");
var hub=new RoomHub(3,null,accounts);string trade=Guid.NewGuid().ToString("N"),cancel=Guid.NewGuid().ToString("N");
async Task Yes(Peer peer,string type,Action<Command>? edit=null){var ack=await peer.Command(type,edit);Check(ack.GetProperty("ok").GetBoolean(),ack.ToString());}
async Task No(Peer peer,string type,string error,Action<Command>? edit=null){var ack=await peer.Command(type,edit);Check(!ack.GetProperty("ok").GetBoolean()&&ack.GetProperty("error").GetString()==error,ack.ToString());}
void Offer(Command c,string id,string target){c.Trade=id;c.Target=target;c.Payload=payload;c.Price=29;}
(int Count,string State,string Payload) Row(string id){using var db=accounts.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT state,payload FROM trades WHERE id=$i";cmd.Parameters.AddWithValue("$i",id);using var r=cmd.ExecuteReader();if(!r.Read())return(0,"","");string state=r.GetString(0),p=r.GetString(1);return(r.Read()?2:1,state,p);}
await using(var s=new Peer(hub,hub.LoginAccount(seller),true))await using(var b=new Peer(hub,hub.LoginAccount(buyer),true))await using(var legacy=new Peer(hub,hub.LoginAccount(old),false)){
 await Yes(s,"privacy",c=>c.AllowVisits=true);
 await Yes(s,"shopScene",c=>{c.Name="批量测试当铺";c.Payload=File.ReadAllText(args[2]);});
 await Yes(b,"visit",c=>c.Target=s.Session.Id);await Yes(legacy,"visit",c=>c.Target=s.Session.Id);
 string denied=Guid.NewGuid().ToString("N");await No(s,"tradeOffer","client_update",c=>Offer(c,denied,legacy.Session.Id));Check(Row(denied).Count==0,"old receiver rejection creates no partial trade");
 await No(legacy,"tradeOffer","client_update",c=>Offer(c,Guid.NewGuid().ToString("N"),s.Session.Id));
 await Yes(s,"tradeOffer",c=>Offer(c,trade,b.Session.Id));Check(Row(trade)==(1,"proposed",payload),"one persistent trade row stores the complete batch");
 await Yes(s,"tradeOffer",c=>Offer(c,trade,b.Session.Id));Check(Row(trade).Count==1,"quote retry preserves one whole batch row");
 await No(legacy,"tradeAccept","trade_unavailable",c=>c.Trade=trade);
 await Yes(b,"tradeAccept",c=>c.Trade=trade);await Yes(b,"tradeAccept",c=>c.Trade=trade);Check(Row(trade)==(1,"committed",""),"duplicate acceptance produces one committed receipt and clears payload");
 await Yes(s,"tradeCancel",c=>c.Trade=trade);Check(Row(trade).State=="committed","cancellation cannot undo a committed batch");
 await Yes(s,"tradeOffer",c=>Offer(c,cancel,b.Session.Id));await Yes(b,"tradeCancel",c=>c.Trade=cancel);await Yes(s,"tradeOffer",c=>Offer(c,cancel,b.Session.Id));Check(Row(cancel)==(1,"cancelled",""),"cancelled batch tombstone prevents a late quote from reviving any part");
}
var reloaded=new Accounts(root);var hub2=new RoomHub(3,null,reloaded);
await using(var b=new Peer(hub2,hub2.LoginAccount(buyer),true)){
 await Yes(b,"tradeReceipt",c=>c.Trade=trade);Check(b.Last("trade").GetProperty("state").GetString()=="committed","fresh server instance recovers the complete committed batch receipt without a live visit");
 await Yes(b,"tradeAccept",c=>c.Trade=trade);await Yes(b,"tradeReceipt",c=>c.Trade=cancel);Check(b.Last("trade").GetProperty("state").GetString()=="cancelled"&&Row(trade).Count==1,"server reload preserves committed and cancelled once-only outcomes");
}
Console.WriteLine($"PASS {checks} batch assertions: actual native multi-root schema, legacy single compatibility, protected and malformed later parts, bounds and total-node cap, both client capability gates, one-row quote/cancellation/commit, duplicate commands, and durable receipt recovery after a fresh Accounts and RoomHub instance. In-memory WebSockets; no TCP listener or native pair.");

sealed class Peer:IAsyncDisposable {
 readonly RoomHub hub;readonly Socket socket=new();readonly CancellationTokenSource stop=new();readonly Task pump;public readonly Session Session;public readonly Client Client;int count;
 public Peer(RoomHub hub,Session session,bool batch){this.hub=hub;Session=session;Client=new Client(socket);pump=Client.Pump(stop.Token);hub.Attach(new Command{Type="hello",Protocol=1,Token=session.Token,Capabilities=batch?["sharedShop","tradeBatch"]:["sharedShop"]},Client);}
 public JsonElement Last(string type)=>socket.Frames.LastOrDefault(f=>f.GetProperty("type").GetString()==type);
 public async Task<JsonElement> Command(string type,Action<Command>? configure=null){if(++count%10==0)await Task.Delay(1050);var c=new Command{Type=type,Id=Guid.NewGuid().ToString("N")};configure?.Invoke(c);hub.Handle(Session,Client,c);for(int i=0;i<100;i++){await Task.Delay(10);var ack=Last("ack");if(ack.ValueKind==JsonValueKind.Object&&ack.GetProperty("id").GetString()==c.Id){await Task.Delay(30);return ack;}}throw new Exception("ack missing "+type);}
 public async ValueTask DisposeAsync(){hub.Detach(Session,Client);Client.Stop();stop.Cancel();await pump;stop.Dispose();}
 sealed class Socket:WebSocket {
  public readonly System.Collections.Concurrent.ConcurrentQueue<JsonElement> Frames=new();WebSocketState state=WebSocketState.Open;
  public override WebSocketState State=>state;public override WebSocketCloseStatus? CloseStatus=>null;public override string? CloseStatusDescription=>null;public override string? SubProtocol=>null;
  public override void Abort()=>state=WebSocketState.Aborted;public override void Dispose()=>Abort();
  public override Task CloseAsync(WebSocketCloseStatus s,string? t,CancellationToken c){Abort();return Task.CompletedTask;}
  public override Task CloseOutputAsync(WebSocketCloseStatus s,string? t,CancellationToken c)=>CloseAsync(s,t,c);
  public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b,CancellationToken c)=>throw new NotSupportedException();
  public override Task SendAsync(ArraySegment<byte> b,WebSocketMessageType t,bool end,CancellationToken c){using var doc=JsonDocument.Parse(b.AsMemory());Frames.Enqueue(doc.RootElement.Clone());return Task.CompletedTask;}
 }
}
