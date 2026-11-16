using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NightStud.Server;

int checks=0;void Check(bool b,string s){if(!b)throw new Exception(s);checks++;}
void Reject(Action f,string code){try{f();throw new Exception("accepted forbidden operation: "+code);}catch(WireFault e){Check(e.Code==code,"correct refusal "+code+", got "+e.Code);}}
string root=Path.GetFullPath(args.FirstOrDefault()??"work/phone-market-tests");Directory.CreateDirectory(root);string data=Path.Combine(root,Guid.NewGuid().ToString("N"));
var accounts=new Accounts(data);var phones=new PlayerPhones(accounts);var hub=new RoomHub(90,null,accounts,new Moderation(data));
var a=await accounts.Register("phone_market_a","testing-password-4321","卖家");var b=await accounts.Register("phone_market_b","testing-password-4321","买家");var c=accounts.Steam("76561199000000001","Steam 玩家");
string an=phones.Number(a.Id),bn=phones.Number(b.Id),cn=phones.Number(c.Id);Check(new[]{an,bn,cn}.All(n=>n.Length==6&&n[0]!='0'&&n.All(char.IsAsciiDigit))&&new[]{an,bn,cn}.Distinct().Count()==3,"every password and Steam account immediately receives a distinct six-digit number");
Check(new PlayerPhones(new Accounts(data)).Number(a.Id)==an&&phones.Resolve(bn)?.Id==b.Id,"numbers and reverse lookup survive server/database restart");
Check(phones.Resolve("325")==null&&phones.Resolve("032500")==null&&phones.Resolve("12345x")==null,"reserved NPC numbers and malformed numbers never resolve as players");
// An actual pre-phone database is backfilled without changing its original account.
string legacy=Path.Combine(data,"legacy");Directory.CreateDirectory(legacy);
using(var db=new SqliteConnection("Data Source="+Path.Combine(legacy,"accounts.db"))){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="CREATE TABLE accounts(id TEXT PRIMARY KEY,username TEXT UNIQUE,display_name TEXT NOT NULL,salt BLOB,hash BLOB,iterations INTEGER,steam_id TEXT UNIQUE,allow_visits INTEGER NOT NULL DEFAULT 0,allow_invites INTEGER NOT NULL DEFAULT 1,created TEXT NOT NULL,banned_until TEXT); INSERT INTO accounts(id,display_name,created) VALUES('legacy-account','旧玩家','2026-01-01')";cmd.ExecuteNonQuery();}
var oldAccounts=new Accounts(legacy);Check(new PlayerPhones(oldAccounts).Number("legacy-account").Length==6,"legacy accounts are migrated before login without replacing their identity");
await using var one=new Peer(hub,hub.LoginAccount(a));await using var two=new Peer(hub,hub.LoginAccount(b));await using var outsider=new Peer(hub,hub.LoginAccount(c));
Check(one.Last("welcome").GetProperty("phone").GetString()==an,"authenticated welcome provides own phone number");
Check((await one.Do("phoneDial",new(){Number=bn})).GetProperty("ok").GetBoolean(),"direct number dial uses the existing authenticated call state machine");
var incoming=two.Last("phone");string call=incoming.GetProperty("id").GetString()!;
Check(incoming.GetProperty("incoming").GetBoolean()&&incoming.GetProperty("number").GetString()==an,"incoming notice identifies caller number");
Check(DateTimeOffset.Parse(incoming.GetProperty("expires").GetString()!)-DateTimeOffset.UtcNow<TimeSpan.FromSeconds(21),"ring timeout is twenty seconds");
Check(outsider.Count("phone")==0,"call states remain private from third parties");
await two.Do("phoneEnd",new(){Call=call});Check(phones.History(b.Id).Length==1,"declined/missed calls persist in receiver history");
await two.Do("phoneRead");var logs=two.Last("contacts").GetProperty("calls");Check(logs[0].GetProperty("read").GetBoolean(),"reading call history clears only this account's missed indicator");
await two.Disconnect();Check((await outsider.Do("phoneDial",new(){Number=bn})).GetProperty("ok").GetBoolean()==false,"offline number does not produce a connected call");Check(outsider.Last("phoneResolved").GetProperty("peer").GetString()==b.Id,"offline dial retains authenticated target for leaving a message");
Check((await outsider.Do("contactSend",new(){Target=b.Id,Text="上线后回个电话。"})).GetProperty("ok").GetBoolean(),"offline caller can leave a persistent message");await two.Reconnect();
string saveA=new string('1',32),saveB=new string('2',32),saveC=new string('3',32);var cash=new StakeLedger(accounts);var market=new MarketLedger(accounts);
string Id()=>Guid.NewGuid().ToString("N");
string payload="""{"label":"基础绷带","nodes":[{"identifier":"bandage_item","unitCount":1,"children":[],"slots":[],"tags":[],"shape":null,"modified":null}]}""";
cash.Transfer(b.Id,saveB,Id(),"in",1000);cash.Transfer(c.Id,saveC,Id(),"in",1000);string listing=Id();
Check(market.Offer(a.Id,saveA,listing,payload,150).State=="listed","physical item is stored in server escrow before it is publicly listed");
Check(market.Offer(a.Id,saveA,listing,payload,150).Id==listing&&market.Browse(a.Id,saveA,"sell",0).Length==1,"retrying an upload does not create a second listing");
Reject(()=>market.Buy(a.Id,saveA,listing,150),"market_self");Reject(()=>market.Buy(b.Id,saveB,listing,151),"market_gone");Reject(()=>market.Cancel(c.Id,saveC,listing),"market_scope");
Check(market.Buy(b.Id,saveB,listing,150).Buyer==b.Id&&cash.Balance(b.Id,saveB)==850&&cash.Balance(a.Id,saveA)==150,"one purchase atomically transfers item ownership and exactly one payment to the original seller save");
market.Buy(b.Id,saveB,listing,150);Check(cash.Balance(b.Id,saveB)==850&&cash.Balance(a.Id,saveA)==150,"retried purchase receipt never charges or credits twice");Reject(()=>market.Buy(c.Id,saveC,listing,150),"market_gone");
Reject(()=>market.Status(b.Id,saveA,listing),"market_scope");Reject(()=>market.Delivered(c.Id,saveC,listing),"market_scope");
Check(new MarketLedger(new Accounts(data)).Status(b.Id,saveB,listing)?.Payload==payload,"undelivered purchase survives a complete ledger restart with full item payload");
Check(market.Delivered(b.Id,saveB,listing).Delivered&&market.Status(b.Id,saveB,listing)!.Payload=="","only the buyer's durable delivery acknowledgement retires its payload");market.Delivered(b.Id,saveB,listing);Check(cash.Balance(a.Id,saveA)==150,"repeated delivery acknowledgements cannot repeat a payout");
string missing=Id();Check(market.Status(a.Id,saveA,missing,true)?.State=="cancelled","missing upload is durably tombstoned before a local refund");Check(market.Offer(a.Id,saveA,missing,payload,60).State=="cancelled","delayed original upload cannot relist a refunded physical object");
string cancelled=Id();market.Offer(a.Id,saveA,cancelled,payload,50);Check(market.Cancel(a.Id,saveA,cancelled).Payload==payload,"cancelling an unsold item preserves its original state for return");Reject(()=>market.Buy(b.Id,saveB,cancelled,50),"market_gone");market.Delivered(a.Id,saveA,cancelled);
string race=Id();market.Offer(a.Id,saveA,race,payload,100);int winners=0;
await Task.WhenAll(new[]{(b.Id,saveB),(c.Id,saveC)}.Select(pair=>Task.Run(()=>{try{new MarketLedger(accounts).Buy(pair.Item1,pair.Item2,race,100);Interlocked.Increment(ref winners);}catch(WireFault e)when(e.Code=="market_gone") {}})));
Check(winners==1&&cash.Balance(a.Id,saveA)==250,"two simultaneous buyers produce exactly one winner and one seller payment");
string tooExpensive=Id();market.Offer(a.Id,saveA,tooExpensive,payload,999999);Reject(()=>market.Buy(b.Id,saveB,tooExpensive,999999),"cash_balance");Check(market.Preview(tooExpensive)?.State=="listed","insufficient funds rolls back without consuming the item");
string expired=Id();market.Offer(a.Id,saveA,expired,payload,50);using(var db=accounts.Open()){using var cmd=db.CreateCommand();cmd.CommandText="UPDATE market_listings SET expires='2020-01-01' WHERE id=$i";cmd.Parameters.AddWithValue("$i",expired);cmd.ExecuteNonQuery();}Check(market.Status(a.Id,saveA,expired)!.State=="cancelled","expired listings become returnable rather than destroying the physical item");
string tech=payload.Replace("基础绷带","虹膜片").Replace("bandage_item","night_stud.gear.phaseLens");Check(market.Offer(a.Id,saveA,Id(),tech,80).Kind=="night_stud.gear.phaseLens","the real technology item survives typed marketplace validation");
string worn=payload.Replace("基础绷带","茶色镜 [3/8]").Replace("bandage_item","night_stud.gear.tintFilter").Replace("\"tags\":[]","\"tags\":[{\"key\":\"night_stud.gear.remaining.v1\",\"number\":3}]");
string wornId=Id();market.Offer(a.Id,saveA,wornId,worn,30);Check(market.Preview(wornId)!.Payload==worn,"worn equipment payload is retained byte for byte");
var wornSale=market.Buy(b.Id,saveB,wornId,30);Check(wornSale.Payload==worn&&new MarketLedger(new Accounts(data)).Status(b.Id,saveB,wornId)!.Payload==worn,"selling and restarting never refill equipment durability");
foreach(int invalid in new[]{0,-1,9,int.MaxValue})Reject(()=>GoodsLedger.Validate(worn.Replace("\"number\":3","\"number\":"+invalid)),"bad_lot");
Check(GoodsLedger.Validate(tech).Id=="night_stud.gear.phaseLens","legacy single-use supplies without durability remain compatible");
Reject(()=>market.Offer(a.Id,saveA,Id(),payload.Replace("bandage_item","lou_locker"),1),"protected_lot");
Check((await one.Do("marketBrowse",new(){SaveId=saveA,Direction="buy",Choice=0})).GetProperty("ok").GetBoolean(),"production market routing exposes summaries without item payloads");
Check(!one.Last("market").GetProperty("items")[0].TryGetProperty("payload",out _),"market browsing keeps full item trees out of every summary frame");
string summary=$"PASS {checks} phone/market assertions: stable account numbers, legacy migration, direct authenticated dialing, private history, offline messages, atomic offline sale, save scope, single delivery, tombstones, simultaneous buyers, expiration and physical device payloads.";File.WriteAllText(Path.Combine(root,"success.txt"),summary);Console.WriteLine(summary);

sealed class Peer:IAsyncDisposable {
 readonly RoomHub hub;readonly Session session;readonly Socket socket=new();Client client;CancellationTokenSource stop=new();Task pump;
 public Peer(RoomHub hub,Session session){this.hub=hub;this.session=session;client=new(socket);pump=client.Pump(stop.Token);Attach();}
 void Attach()=>hub.Attach(new(){Type="hello",Protocol=1,Token=session.Token,Capabilities=["contacts","storeStakes"]},client);
 public JsonElement Last(string type)=>socket.Frames.LastOrDefault(f=>f.GetProperty("type").GetString()==type);
 public int Count(string type)=>socket.Frames.Count(f=>f.GetProperty("type").GetString()==type);
 public async Task<JsonElement> Do(string type,Command? command=null){command??=new();command.Type=type;command.Id=Guid.NewGuid().ToString("N");session.RateSince=DateTime.UtcNow.AddSeconds(-2);hub.Handle(session,client,command);for(int i=0;i<100;i++){await Task.Delay(5);var ack=Last("ack");if(ack.ValueKind==JsonValueKind.Object&&ack.GetProperty("id").GetString()==command.Id){await Task.Delay(5);return ack;}}throw new Exception("missing ack: "+type);}
 public async Task Disconnect(){hub.Detach(session,client);client.Stop();stop.Cancel();await pump;}
 public Task Reconnect(){stop.Dispose();stop=new();client=new(socket);pump=client.Pump(stop.Token);Attach();return Task.Delay(30);}
 public async ValueTask DisposeAsync(){await Disconnect();stop.Dispose();}
 sealed class Socket:WebSocket {
  public readonly ConcurrentQueue<JsonElement> Frames=new();public override WebSocketState State=>WebSocketState.Open;public override WebSocketCloseStatus? CloseStatus=>null;public override string? CloseStatusDescription=>null;public override string? SubProtocol=>null;public override void Abort(){}public override void Dispose(){}
  public override Task CloseAsync(WebSocketCloseStatus s,string? t,CancellationToken c)=>Task.CompletedTask;public override Task CloseOutputAsync(WebSocketCloseStatus s,string? t,CancellationToken c)=>Task.CompletedTask;public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte>b,CancellationToken c)=>throw new NotSupportedException();
  public override Task SendAsync(ArraySegment<byte>b,WebSocketMessageType type,bool end,CancellationToken cancel){using var doc=JsonDocument.Parse(b.AsMemory());Frames.Enqueue(doc.RootElement.Clone());return Task.CompletedTask;}
 }
}
