using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using NightStud.Server;
using NightStud.Shared;

int checks=0;void Check(bool condition,string message){checks++;if(!condition)throw new Exception(message);}
string output=args.FirstOrDefault()??Path.Combine("work","wager068-tests");Directory.CreateDirectory(output);
foreach(int tier in Enumerable.Range(0,3)){
 var amounts=WagerRules.Stakes(tier);Check(amounts.Distinct().Count()==amounts.Length&&amounts.All(a=>a>0&&a<=20000),"bounded unique local stake ladder");
 Check(amounts.Last()>WagerRules.Base(tier)*5,"higher stakes exceed the old five-times cap");
 Check(WagerRules.NextStake(amounts.Last(),tier)==amounts[0],"stake wraps to the lowest amount");
}
for(int dealer=0;dealer<4;dealer++)for(int own=0;own<4;own++){
 Check(WagerRules.Wind(dealer,dealer)==0,"dealer is east");
 Check(Enumerable.Range(0,4).Select(i=>WagerRules.Wind((own+i)%4,dealer)).Distinct().Count()==4,"all four winds under every camera rotation");
}
Check(!WagerRules.Room(int.MaxValue,int.MaxValue,true)&&!WagerRules.Room(20001,1000000,false),"huge ante rejected without overflow");
Check(!WagerRules.Room(20000,959999,true)&&WagerRules.Room(20000,960000,true),"Mahjong maximum-loss reserve boundary");
Check(!WagerRules.Room(20000,1000001,false)&&WagerRules.Room(20000,1000000,false),"buy-in boundary");
var hub=new RoomHub(3);await using var a=new Peer(hub,"大额甲");await using var b=new Peer(hub,"大额乙");await using var c=new Peer(hub,"大额丙");await using var d=new Peer(hub,"大额丁");
Room Current(Peer p)=>((Dictionary<string,Room>)typeof(RoomHub).GetField("rooms",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(hub)!)[p.Session.RoomCode!];
Check(RoomHub.Features.Contains("higherStakes"),"negotiated higher-stakes capability");
foreach(var invalid in new[]{new Command{Ante=20001,BuyIn=1000000},new Command{Ante=int.MaxValue,BuyIn=int.MaxValue},new Command{Ante=20000,BuyIn=1000001},new Command{Ante=20000,BuyIn=-1},new Command{Game="mahjong",Ante=20000,BuyIn=959999}}){
 var ack=await a.Command("create",x=>{x.Ante=invalid.Ante;x.BuyIn=invalid.BuyIn;x.Game=invalid.Game;});Check(!ack.GetProperty("ok").GetBoolean()&&ack.GetProperty("error").GetString()=="bad_rules","authoritative invalid high-stakes room rejected");
}
foreach(string mode in OnlineRound.Games){
 var peers=new[]{a,b,c,d}.Take(OnlineRound.Capacity(mode)).ToArray();
 await a.Ok("create",x=>{x.Game=mode;x.Ante=20000;x.BuyIn=1000000;});string code=a.Room.GetProperty("code").GetString()!;
 foreach(var peer in peers.Skip(1))await peer.Ok("join",x=>x.Code=code);foreach(var peer in peers)await peer.Ok("ready");
 var room=Current(a);Check(room.Game!.Ante==20000,"max ante accepted "+mode);int steps=0;
 while(!room.Game.Done){
  var game=room.Game;Check(++steps<1200&&game.Stack.Min()>=0&&game.Stack.Sum()+game.Pot==peers.Length*1000000,"high-stakes ledger "+mode);
  var command=mode is "landlord" or "mahjong"?game.Suggest():new Command{Move=game.Legal().FirstOrDefault(o=>o.Move=="allIn")?.Move??game.Legal().FirstOrDefault(o=>o.Move=="stand")?.Move??game.Legal().FirstOrDefault(o=>o.Move=="call")?.Move??game.Legal().FirstOrDefault(o=>o.Move=="check")?.Move??game.Legal()[0].Move};
  var actor=peers.Single(p=>Array.FindIndex(room.Seats,s=>s?.Session==p.Session)==game.Actor);
  await actor.Ok("act",x=>{x.Move=command.Move;x.Choice=command.Choice;x.Cards=command.Cards;});
 }
 Check(room.Game.Stack.Sum()==peers.Length*1000000&&room.Game.Stack.Min()>=0,"high-stakes conservation at settlement "+mode);
 var end=room.Game.Stack.ToArray();room.Game.Forfeit(0);Check(end.SequenceEqual(room.Game.Stack),"large settlement cannot pay twice "+mode);
 foreach(var peer in peers)await peer.Ok("leave");
 Console.WriteLine("PASS maximum-stake protocol round "+mode);
}
var accounts=new Accounts(Path.Combine(output,"ledger"));var ledger=new StakeLedger(accounts);string owner1=Guid.NewGuid().ToString("N"),owner2=Guid.NewGuid().ToString("N"),save=Guid.NewGuid().ToString("N"),table=Guid.NewGuid().ToString("N");
foreach(string owner in new[]{owner1,owner2})ledger.Transfer(owner,save,Guid.NewGuid().ToString("N"),"in",1000000);
var first=ledger.Reserve(owner1,save,table,0,1000000);var second=ledger.Reserve(owner2,save,table,1,1000000);ledger.BeginHand(table,new[]{1000000,1000000});ledger.FinishHand(table,new[]{2000000,0});
Check(ledger.Release(first.Id,owner1,save)==2000000&&ledger.Release(first.Id,owner1,save)==0,"million-credit cash holds settle and release once");
Check(ledger.Release(second.Id,owner2,save)==0&&ledger.Balance(owner1,save)==2000000&&ledger.Balance(owner2,save)==0,"cash wallet conservation after large payout");
try{ledger.Reserve(owner1,save,Guid.NewGuid().ToString("N"),0,1000001);throw new Exception("oversize hold accepted");}catch(WireFault e){Check(e.Code=="cash_amount","oversize cash hold rejected");}
string proof=$"PASS {checks} assertions: seven maximum-stake protocol games, room limit rejection, rotated seat winds, large cash-hold conservation and once-only payouts.";File.WriteAllText(Path.Combine(output,"success.txt"),proof);Console.WriteLine(proof);

sealed class Peer:IAsyncDisposable{
 readonly RoomHub hub;readonly Sink socket=new();readonly Client client;readonly CancellationTokenSource stop=new();readonly Task pump;
 public Session Session{get;}public JsonElement Room=>socket.Room;
 public Peer(RoomHub hub,string name){this.hub=hub;Session=hub.Login(name);client=new Client(socket);pump=client.Pump(stop.Token);hub.Attach(new Command{Type="hello",Protocol=1,Token=Session.Token,Games=OnlineRound.Games},client);}
 public async Task Flush(){Session.RateCount=0;int before=socket.Pongs;hub.Handle(Session,client,new(){Type="ping"});for(int n=0;n<3000&&socket.Pongs==before;n++)await Task.Delay(1);if(socket.Pongs==before)throw new Exception("Flush timeout");}
 public async Task<JsonElement> Command(string kind,Action<Command>? set=null){await Flush();var command=new Command{Type=kind,Id=Guid.NewGuid().ToString("N"),Revision=Room.ValueKind==JsonValueKind.Object?Room.GetProperty("revision").GetInt64():null};set?.Invoke(command);hub.Handle(Session,client,command);await Flush();return socket.Ack;}
 public async Task Ok(string kind,Action<Command>? set=null){var ack=await Command(kind,set);if(!ack.GetProperty("ok").GetBoolean())throw new Exception(kind+": "+ack);}
 public async ValueTask DisposeAsync(){hub.Detach(Session,client);client.Stop();stop.Cancel();await pump;stop.Dispose();}
}
sealed class Sink:WebSocket{
 public JsonElement Room,Ack;public int Pongs;WebSocketState state=WebSocketState.Open;
 public override WebSocketCloseStatus? CloseStatus=>null;public override string? CloseStatusDescription=>null;public override WebSocketState State=>state;public override string? SubProtocol=>null;
 public override void Abort()=>state=WebSocketState.Aborted;public override void Dispose()=>Abort();
 public override Task CloseAsync(WebSocketCloseStatus s,string? t,CancellationToken c){Abort();return Task.CompletedTask;}
 public override Task CloseOutputAsync(WebSocketCloseStatus s,string? t,CancellationToken c)=>CloseAsync(s,t,c);
 public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b,CancellationToken c)=>throw new NotSupportedException();
 public override Task SendAsync(ArraySegment<byte> bytes,WebSocketMessageType type,bool end,CancellationToken cancel){using var doc=JsonDocument.Parse(bytes.AsMemory());var v=doc.RootElement;switch(v.GetProperty("type").GetString()){case "state":Room=v.GetProperty("room").Clone();break;case "ack":Ack=v.Clone();break;case "pong":Interlocked.Increment(ref Pongs);break;}return Task.CompletedTask;}
}
