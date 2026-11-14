using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using NightStud;
using NightStud.Server;

int checks=0;
void Check(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
string output=args.FirstOrDefault()??Path.Combine(Directory.GetCurrentDirectory(),"work/group040-tests");Directory.CreateDirectory(output);
void Fault(Action action,string code){try{action();throw new Exception("Expected "+code);}catch(WireFault e){Check(e.Code==code,"rejection "+code);}}
foreach(string mode in new[]{"landlord","mahjong","dice"}){
    var moves=new HashSet<string>();int wins=0;
    for(int seed=1;seed<=80;seed++){
        int cap=OnlineRound.Capacity(mode);var initial=Enumerable.Repeat(10000,cap).ToArray();var round=new OnlineRound(mode,10,seed,initial,new Random(seed));int step=0;
        while(!round.Done){
            Check(++step<1200,"finite "+mode);Check(round.Stack.Sum()+round.Pot==initial.Sum()&&round.Stack.Min()>=0,"ledger "+mode);
            for(int viewer=0;viewer<cap;viewer++)for(int seat=0;seat<cap;seat++)if(viewer!=seat)
                Check(round.VisibleCards(viewer,seat).All(c=>c==null),"private hand "+mode);
            Command command=mode=="dice"?new(){Move=step==1?(seed%2==0?"odd":"even"):"reveal"}:round.Suggest();
            moves.Add(command.Move);round.Play(command);
        }
        Check(round.Stack.Sum()==initial.Sum()&&round.Stack.Min()>=0,"settlement "+mode);
        Check(round.Done,"finished "+mode);if(round.Winner>=0)wins++;
        var after=round.Stack.ToArray();round.Forfeit(0);round.VoidHand();Check(after.SequenceEqual(round.Stack),"settlement idempotent "+mode);
    }
    Console.WriteLine($"PASS 80 seeded {mode} rounds; wins {wins}; actions {string.Join(',',moves.Order())}");
}

Mahjong Fixture(bool multi=false){
    var m=new Mahjong(new Random(17),manualPlayers:true);foreach(var h in m.Hands)h.Clear();
    m.Turn=0;m.From=-1;m.Last=-1;m.Drawn=5;m.Reply=false;m.Done=false;
    m.Hands[0].AddRange(new[]{5,6,6,7,8,10,12,15,16,19,20,24,26,31});
    m.Hands[1].AddRange(new[]{0,1,2,9,10,11,18,19,20,27,27,3,4});
    m.Hands[2].AddRange(multi?m.Hands[1]:new[]{5,5,7,7,8,10,12,15,16,19,20,24,26});
    m.Hands[3].AddRange(new[]{1,3,6,8,9,12,14,17,18,21,23,25,30});m.Visuals.Clear();return m;
}
var claim=Fixture();Check(claim.Discard(5),"discard opens response window");
Check(claim.Reply&&claim.OnlineActor==1&&claim.OnlineRon(1),"human ron offered");
Check(!claim.OnlineRespond(2,Mahjong.Pung),"off-turn reply rejected");
Check(claim.OnlineRespond(1,Mahjong.Win),"first ron chosen");
Check(claim.OnlineActor==2&&claim.OnlinePong(2),"second player may reply");
Check(claim.OnlineRespond(2,Mahjong.Pung),"pong response recorded");
Check(claim.Done&&claim.Winners.SequenceEqual(new[]{1})&&claim.Melds[2].Count==0,"ron takes priority over pong");
var multiple=Fixture(true);multiple.Discard(5);multiple.OnlineRespond(1,Mahjong.Win);multiple.OnlineRespond(2,Mahjong.Win);
Check(multiple.Done&&multiple.Winners.SequenceEqual(new[]{1,2}),"multiple ron winners");
var passed=Fixture();passed.Discard(5);passed.OnlineRespond(1,Mahjong.Pass);passed.OnlineRespond(2,Mahjong.Pung);
Check(!passed.Done&&passed.Turn==2&&!passed.Reply&&passed.Melds[2].SequenceEqual(new[]{5,5,5}),"pass win then human pong");
Check(Mahjong.Winning(new[]{0,1,2,3,4,5,9,10,11,18,19,20,27,27},0),"plain winning hand");
Check(Mahjong.Winning(new[]{0,0,1,1,9,9,10,10,18,18,27,27,33,33},0),"seven pairs");
Check(Mahjong.Winning(new[]{0,8,9,17,18,26,27,28,29,30,31,32,33,33},0),"thirteen orphans");

var fake=new OnlineRound("landlord",10,1,new[]{1000,1000,1000},new Random(17));fake.Play(new Command{Move="bid"});
string before=Wire.Encode(fake.Detail(fake.Actor)!);var cardsBefore=fake.Hand.Select(h=>h.ToArray()).ToArray();
Fault(()=>fake.Play(new Command{Move="play",Cards=new[]{54}}),"bad_cards");
Fault(()=>fake.Play(new Command{Move="play",Cards=new[]{fake.Hand[fake.Actor][0],fake.Hand[fake.Actor][0]}}),"bad_cards");
Check(before==Wire.Encode(fake.Detail(fake.Actor)!)&&cardsBefore.Select((h,s)=>h.SequenceEqual(fake.Hand[s])).All(v=>v),"invalid cards leave state intact");

var hub=new RoomHub(3);await using var a=new Peer(hub,"测试店主");await using var b=new Peer(hub,"测试桌友");await using var c=new Peer(hub,"测试第三位");await using var d=new Peer(hub,"测试第四位");await using var old=new Peer(hub,"旧客户端",false);
Room Current(Peer p)=>((Dictionary<string,Room>)typeof(RoomHub).GetField("rooms",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(hub)!)[p.Session.RoomCode!];
foreach(string mode in new[]{"landlord","mahjong","dice"}){
    int cap=OnlineRound.Capacity(mode);var peers=new[]{a,b,c,d}.Take(cap).ToArray();
    await a.Ok("create",x=>{x.Game=mode;x.Public=true;});string code=a.Room.GetProperty("code").GetString()!;
    await old.Flush();Check((await old.Command("join",x=>x.Code=code)).GetProperty("error").GetString()=="client_update_required","old client blocked "+mode);
    foreach(var peer in peers.Skip(1))await peer.Ok("join",x=>x.Code=code);
    Check(a.Room.GetProperty("capacity").GetInt32()==cap,"capacity "+mode);
    foreach(var peer in peers)await peer.Ok("ready");await a.Flush();
    File.WriteAllText(Path.Combine(output,mode+"-playing.json"),Wire.Encode(new{type="state",room=a.Room}));
    {var active=peers[Current(a).Game!.Actor];await active.Flush();File.WriteAllText(Path.Combine(output,mode+"-acting.json"),Wire.Encode(new{type="state",room=active.Room}));}
    int steps=0;var room=Current(a);
    while(room.Game is {Done:false} game){
        Check(++steps<1200,"protocol terminates "+mode);int actor=game.Actor;var command=mode=="dice"?new Command{Move=game.Legal()[0].Move}:game.Suggest();
        var actorPeer=peers.Single(p=>Array.FindIndex(room.Seats,s=>s?.Session==p.Session)==actor);
        await actorPeer.Ok("act",x=>{x.Move=command.Move;x.Choice=command.Choice;x.Cards=command.Cards;});
        await a.Flush();
        if(mode=="mahjong"&&game.Legal().Any(x=>x.Move is "pung" or "kong" or "chow" or "win")){
            var responding=peers.Single(p=>Array.FindIndex(room.Seats,s=>s?.Session==p.Session)==game.Actor);await responding.Flush();
            File.WriteAllText(Path.Combine(output,"mahjong-claim.json"),Wire.Encode(new{type="state",room=responding.Room}));
        }
    }
    await a.Flush();Check(room.Seats.Sum(s=>s!.Balance)==1000*cap,"protocol settlement "+mode);
    File.WriteAllText(Path.Combine(output,mode+"-finished.json"),Wire.Encode(new{type="state",room=a.Room}));
    foreach(var peer in peers)await peer.Ok("leave");
}
foreach(string mode in new[]{"landlord","mahjong"}){
    await a.Ok("create",x=>{x.Game=mode;x.Public=true;});
    string code=a.Room.GetProperty("code").GetString()!;await b.Ok("join",x=>x.Code=code);
    Check((await b.Command("botAdd")).GetProperty("error").GetString()=="host_only","only host may fill bots");
    for(int i=2;i<OnlineRound.Capacity(mode);i++)await a.Ok("botAdd");
    File.WriteAllText(Path.Combine(output,mode+"-bots-waiting.json"),Wire.Encode(new{type="state",room=a.Room}));
    await a.Ok("botRemove");await a.Ok("botAdd");await a.Ok("ready");await b.Ok("ready");await a.Flush();
    var room=Current(a);int steps=0;
    while(room.Game is {Done:false} game){
        Check(++steps<1200,"bot room finite "+mode);int actor=game.Actor;
        if(room.Seats[actor]!.Session.Provider=="bot"){room.NextBot=DateTime.MinValue;hub.Expire();}
        else{var peer=room.Seats[actor]!.Session==a.Session?a:b;var cmd=game.Suggest();await peer.Ok("act",x=>{x.Move=cmd.Move;x.Choice=cmd.Choice;x.Cards=cmd.Cards;});}
        await a.Flush();
    }
    Check(room.Seats.Sum(s=>s!.Balance)==OnlineRound.Capacity(mode)*1000,"bot ledger "+mode);
    await a.Ok("leave");await b.Ok("leave");Check(((JsonElement)JsonSerializer.SerializeToElement(hub.Status())).GetProperty("rooms").GetInt32()==0,"orphan bots cleaned "+mode);
}
await a.Ok("create",x=>x.Game="landlord");
{string code=a.Room.GetProperty("code").GetString()!;await b.Ok("join",x=>x.Code=code);await a.Ok("botAdd");await a.Ok("leave");
 await c.Ok("join",x=>x.Code=code);Check(Current(b).HostId==b.Session.Id,"new player cannot steal transferred host seat");
 Check((await c.Command("botRemove")).GetProperty("error").GetString()=="host_only","transferred host permission");
 await b.Ok("botRemove");await b.Ok("botAdd");await c.Ok("leave");await b.Ok("leave");}
await a.Ok("create",x=>x.Game="mahjong");
{string code=a.Room.GetProperty("code").GetString()!;await a.Ok("botAdd");await a.Ok("botAdd");await a.Ok("botAdd");
 await b.Ok("join",x=>x.Code=code);Check(Current(a).Seats.Count(s=>s?.Session.Provider=="bot")==2,"human replaces bot before hand starts");
 await b.Ok("leave");await a.Ok("botAdd");await a.Ok("ready");var room=Current(a);var game=room.Game!;a.Drop();a.Session.Disconnected=DateTime.UtcNow.AddSeconds(-4);hub.Expire();
 Check(game.Done&&game.Winner==-1&&game.Stack.All(n=>n==1000),"all humans offline with bots refunds full hand");
 Check(JsonSerializer.SerializeToElement(hub.Status()).GetProperty("rooms").GetInt32()==0,"offline bot room removed");}
string proof=$"PASS {checks} assertions: 240 seeded new-mode rounds; human 3/4-seat rooms; hidden hands; legal actions and invalid-card rejection; ron/pong priority and multiple winners; bots/host transfer/replacement/offline cleanup; zero-sum settlements. No TCP listener; protocol uses in-memory sockets.";
File.WriteAllText(Path.Combine(output,"success.txt"),proof);Console.WriteLine(proof);

sealed class Peer:IAsyncDisposable{
    readonly RoomHub hub;readonly Sink socket=new();readonly Client client;readonly CancellationTokenSource stop=new();readonly Task pump;
    public Session Session{get;}public JsonElement Room=>socket.Room;
    public Peer(RoomHub hub,string name,bool modern=true){this.hub=hub;Session=hub.Login(name);client=new Client(socket);pump=client.Pump(stop.Token);hub.Attach(new Command{Type="hello",Protocol=1,Token=Session.Token,Games=modern?OnlineRound.Games:null},client);}
    public async Task Flush(){Session.RateCount=0;int before=socket.Pongs;hub.Handle(Session,client,new(){Type="ping"});for(int n=0;n<3000&&socket.Pongs==before;n++)await Task.Delay(1);if(socket.Pongs==before)throw new Exception("Flush timeout");}
    public async Task<JsonElement> Command(string type,Action<Command>? set=null){await Flush();var command=new Command{Type=type,Id=Guid.NewGuid().ToString("N"),Revision=Room.ValueKind==JsonValueKind.Object?Room.GetProperty("revision").GetInt64():null};set?.Invoke(command);hub.Handle(Session,client,command);await Flush();return socket.Ack;}
    public async Task Ok(string type,Action<Command>? set=null){var ack=await Command(type,set);if(!ack.GetProperty("ok").GetBoolean())throw new Exception(type+": "+ack);}
    public void Drop(){hub.Detach(Session,client);client.Stop();}
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
