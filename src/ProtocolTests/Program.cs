using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NightStud.Server;

int checks=0;
void Check(bool value,string label){checks++;if(!value)throw new Exception(label);}
string root=args.FirstOrDefault()??Path.Combine(Directory.GetCurrentDirectory(),"work","protocol020-tests");Directory.CreateDirectory(root);
var random=new Random(81729);
foreach(string mode in new[]{"stud","three","holdem","blackjack"}){
    int completed=0;
    for(int n=1;n<=600;n++){
        var balances=new[]{random.Next(80,5000),random.Next(80,5000)};
        var game=new OnlineRound(mode,10,n,balances,new Random(n));int steps=0;
        while(!game.Done){
            Check(++steps<120,"finite round "+mode);
            Check(game.Stack.Sum()+game.Pot==balances.Sum()&&game.Stack.Min()>=0,"ledger during "+mode);
            Check(game.Hand.SelectMany(h=>h).Concat(game.Board).Distinct().Count()==game.Hand.Sum(h=>h.Count)+game.Board.Length,"unique cards "+mode);
            int other=1-game.Actor;var hidden=game.VisibleCards(game.Actor,other);
            if(mode is "three" or "holdem")Check(hidden.All(v=>v==null),"opponent hole privacy "+mode);
            var options=game.Legal();Check(options.Length>0,"has legal move "+mode);
            BetOption choice;
            if(mode=="blackjack")choice=options.Single(o=>o.Move==(OnlineRound.Points(game.Hand[game.Actor])<17?"hit":"stand"));
            else if(n%3==0)choice=options.FirstOrDefault(o=>o.Move=="check")??options.FirstOrDefault(o=>o.Move=="call")??options.FirstOrDefault(o=>o.Move=="bet")??options.First();
            else choice=options[random.Next(options.Length)];
            game.Play(choice.Move,choice.Move is "bet" or "raise"?choice.Amount:null);
        }
        Check(game.Stack.Sum()==balances.Sum()&&game.Stack.Min()>=0,"settlement conservation "+mode);completed++;
        if(n<=8){var partial=new OnlineRound(mode,10,n,new[]{1000,1000},new Random(n));var before=partial.Stack.ToArray();var pot=partial.Pot;partial.Forfeit(0);Check(partial.Stack.Sum()==2000,"forfeit "+mode);partial.Forfeit(0);Check(partial.Stack.Sum()==2000,"forfeit idempotent "+mode);var aborted=new OnlineRound(mode,10,n,new[]{1000,1000},new Random(n));if(!aborted.Done){aborted.VoidHand();Check(aborted.Stack.SequenceEqual(new[]{1000,1000}),"refund original deposits "+mode);}}
    }
    Console.WriteLine($"PASS {completed} seeded {mode} hands");
}
Check(OnlineRound.ThreeScore(new[]{12,25,38})>OnlineRound.ThreeScore(new[]{0,1,2}),"triple beats straight flush");
Check(OnlineRound.ThreeScore(new[]{12,0,1})<OnlineRound.ThreeScore(new[]{0,1,2}),"A23 lowest straight flush");
Check(OnlineRound.Points(new[]{12,25,9})==12,"soft aces");

var events=new List<ServerEvent>();var hub=new RoomHub(3,events.Add);
await using var a=new Peer(hub,"甲");await using var b=new Peer(hub,"乙");await using var outsider=new Peer(hub,"旁观者");await using var legacy=new Peer(hub,"旧版",false);
await a.Flush();await b.Flush();
Check(a.Lobby.GetProperty("players").GetArrayLength()==4,"lobby roster");
await a.Command("create",c=>{c.Game="holdem";c.Public=false;c.Name="私人德州";});await b.Flush();
string privateCode=a.Room.GetProperty("code").GetString()!;
Check(!b.Lobby.GetProperty("rooms").EnumerateArray().Any(r=>r.GetProperty("code").GetString()==privateCode),"private code absent from public directory");
await a.Command("invite",c=>c.Target=b.Session.Id);await b.Flush();await outsider.Flush();
Check(b.Lobby.GetProperty("invitations").GetArrayLength()==1&&outsider.Lobby.GetProperty("invitations").GetArrayLength()==0,"invitation recipient only");
File.WriteAllText(Path.Combine(root,"lobby-invite.json"),Wire.Encode(b.Lobby));
string invitation=b.Lobby.GetProperty("invitations")[0].GetProperty("id").GetString()!;
Check((await outsider.Command("acceptInvite",c=>c.Invitation=invitation)).GetProperty("error").GetString()=="invite_expired","outsider cannot steal invitation");
await b.Command("acceptInvite",c=>c.Invitation=invitation);await a.Flush();
Check(b.Room.GetProperty("code").GetString()==privateCode,"accept invitation joins real room");
await a.Command("ready");await b.Command("ready");await a.Flush();
Check(a.Room.GetProperty("phase").GetString()=="playing","holdem ready");
Check(a.Room.GetProperty("players")[1].GetProperty("cards").EnumerateArray().All(c=>c.ValueKind==JsonValueKind.Null),"actual per-seat hole redaction");
await a.Command("leave");await b.Command("leave");
await a.Command("create",c=>{c.Game="three";c.Public=true;c.Name="公开金花";});await outsider.Flush();
Check(outsider.Lobby.GetProperty("rooms").EnumerateArray().Any(r=>r.GetProperty("game").GetString()=="three"),"public room visible");
await a.Flush();File.WriteAllText(Path.Combine(root,"lobby-open.json"),Wire.Encode(a.Lobby));
string code=a.Room.GetProperty("code").GetString()!;
Check((await legacy.Command("join",c=>c.Code=code)).GetProperty("error").GetString()=="client_update_required","legacy cannot silently join unsupported game");
await b.Command("join",c=>c.Code=code);await a.Command("ready");await b.Command("ready");await a.Flush();
await b.Command("act",c=>{c.Move="bet";c.Amount=10;});await a.Flush();
File.WriteAllText(Path.Combine(root,"three-playing.json"),Wire.Encode(new{type="state",room=a.Room}));
while(a.Room.GetProperty("phase").GetString()=="playing"){
    await a.Flush();await b.Flush();int actor=a.Room.GetProperty("actor").GetInt32();var peer=actor==a.Room.GetProperty("ownSeat").GetInt32()?a:b;
    await peer.Command("act",c=>c.Move="showdown");await a.Flush();
}
Check(a.Room.GetProperty("result").GetProperty("net").EnumerateArray().Sum(v=>v.GetInt32())==0,"room ledger after three showdown");
File.WriteAllText(Path.Combine(root,"three-finished.json"),Wire.Encode(new{type="state",room=a.Room}));
Check(events.Any(e=>e.Event=="hand_settled")&&events.Any(e=>e.Event=="action_rejected"),"audit includes settlement and rejection");
File.WriteAllText(Path.Combine(root,"lobby.json"),Wire.Encode(a.Lobby));

foreach(string mode in new[]{"holdem","blackjack"}){
    await a.Command("leave");await b.Command("leave");await a.Command("create",c=>{c.Game=mode;c.Public=true;c.Name=mode;});
    string room=a.Room.GetProperty("code").GetString()!;await b.Command("join",c=>c.Code=room);await a.Command("ready");await b.Command("ready");await a.Flush();
    if(mode=="holdem"){await b.Command("act",c=>c.Move="check");await a.Flush();}
    if(mode=="blackjack"){
        int attempts=0;
        while((a.Room.GetProperty("phase").GetString()=="finished"||a.Room.GetProperty("actor").GetInt32()!=0)&&attempts++<12){
            if(a.Room.GetProperty("phase").GetString()=="playing")await b.Command("act",c=>c.Move="stand");
            await a.Command("ready");await b.Command("ready");await a.Flush();
        }
        Check(a.Room.GetProperty("phase").GetString()=="playing"&&a.Room.GetProperty("actor").GetInt32()==0,"active blackjack fixture");
    }
    File.WriteAllText(Path.Combine(root,mode+"-playing.json"),Wire.Encode(new{type="state",room=a.Room}));
    int steps=0;
    while(a.Room.GetProperty("phase").GetString()=="playing"){
        Check(++steps<80,"room terminates "+mode);await a.Flush();await b.Flush();int actor=a.Room.GetProperty("actor").GetInt32();var peer=actor==0?a:b;
        var legal=peer.Room.GetProperty("legalMoves").EnumerateArray().ToArray();string move=mode=="blackjack"?"stand":legal.Any(m=>m.GetProperty("move").GetString()=="check")?"check":"call";
        await peer.Command("act",c=>c.Move=move);await a.Flush();
        if(mode=="holdem"&&a.Room.GetProperty("board").GetArrayLength()==3)File.WriteAllText(Path.Combine(root,"holdem-flop.json"),Wire.Encode(new{type="state",room=a.Room}));
    }
    Check(a.Room.GetProperty("result").GetProperty("net").EnumerateArray().Sum(v=>v.GetInt32())==0,"actual room settlement "+mode);
    File.WriteAllText(Path.Combine(root,mode+"-finished.json"),Wire.Encode(new{type="state",room=a.Room}));
}

string logDir=Path.Combine(root,"logs");Directory.CreateDirectory(logDir);
string old=Path.Combine(logDir,"nightstud-20000101-00.jsonl");File.WriteAllText(old,"old");File.SetLastWriteTimeUtc(old,new DateTime(2000,1,1,0,0,0,DateTimeKind.Utc));
const string secret="test-secret-do-not-log-2026";string token=new string('A',64);
using(var disk=new RollingLog(logDir,secret,1,1)){
    var logger=disk.CreateLogger("test");
    try{throw new InvalidOperationException("root failure "+secret);}catch(Exception ex){logger.LogError(ex,"failure token={Token} key={Key}",token,secret);}
    for(int i=0;i<350;i++)logger.LogInformation("rotation {Index} {Payload}",i,new string('x',6000));
}
var files=Directory.GetFiles(logDir,"nightstud-*.jsonl");Check(!File.Exists(old)&&files.Length>=2,"retention and size rotation");
string logs=string.Join('\n',files.Select(File.ReadAllText));Check(!logs.Contains(secret)&&!logs.Contains(token),"secrets scrubbed from logs");
Check(logs.Contains("InvalidOperationException")&&logs.Contains("root failure")&&logs.Contains("stack"),"error reason and stack logged");
foreach(string line in files.SelectMany(File.ReadAllLines))using(JsonDocument.Parse(line))checks++;
File.WriteAllText(Path.Combine(root,"success.txt"),$"PASS {checks} assertions: 2400 seeded rounds; public/private lobby; invitations and ownership; legacy capability gate; hidden cards; settlements; JSONL logging, retention, rotation and redaction. No TCP listener started.");
Console.WriteLine(File.ReadAllText(Path.Combine(root,"success.txt")));

sealed class Peer: IAsyncDisposable
{
    readonly RoomHub hub;readonly SinkSocket socket=new();readonly Client client;readonly Task pump;readonly CancellationTokenSource stop=new();
    public Session Session {get;}
    public JsonElement Room=>socket.Room;
    public JsonElement Lobby=>socket.Lobby;
    public Peer(RoomHub hub,string name,bool modern=true){this.hub=hub;Session=hub.Login(name);client=new Client(socket);pump=client.Pump(stop.Token);hub.Attach(new Command{Type="hello",Protocol=1,Token=Session.Token,Games=modern?new[]{"stud","three","holdem","blackjack"}:null},client);}
    public async Task Flush(){await Task.Delay(90);int previous=socket.Pongs;hub.Handle(Session,client,new Command{Type="ping"});var until=DateTime.UtcNow.AddSeconds(4);while(socket.Pongs==previous&&DateTime.UtcNow<until)await Task.Delay(1);if(socket.Pongs==previous)throw new Exception("Frame flush timeout");}
    public async Task<JsonElement> Command(string type,Action<Command>? configure=null){await Flush();var cmd=new Command{Type=type,Id=Guid.NewGuid().ToString("N"),Revision=Room.ValueKind==JsonValueKind.Object?Room.GetProperty("revision").GetInt64():null};configure?.Invoke(cmd);hub.Handle(Session,client,cmd);await Flush();return socket.Ack;}
    public async ValueTask DisposeAsync(){hub.Detach(Session,client);client.Stop();stop.Cancel();await pump;stop.Dispose();}
}
sealed class SinkSocket:WebSocket
{
    public JsonElement Room,Lobby,Ack;public int Pongs;
    WebSocketState state=WebSocketState.Open;
    public override WebSocketCloseStatus? CloseStatus=>null;public override string? CloseStatusDescription=>null;public override WebSocketState State=>state;public override string? SubProtocol=>null;
    public override void Abort()=>state=WebSocketState.Aborted;
    public override Task CloseAsync(WebSocketCloseStatus status,string? text,CancellationToken cancel){state=WebSocketState.Closed;return Task.CompletedTask;}
    public override Task CloseOutputAsync(WebSocketCloseStatus status,string? text,CancellationToken cancel)=>CloseAsync(status,text,cancel);
    public override void Dispose()=>Abort();
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> bytes,CancellationToken cancel)=>throw new NotSupportedException();
    public override Task SendAsync(ArraySegment<byte> bytes,WebSocketMessageType type,bool end,CancellationToken cancel){
        using var document=JsonDocument.Parse(bytes.AsMemory());var frame=document.RootElement;string? kind=frame.GetProperty("type").GetString();
        if(kind=="state")Room=frame.GetProperty("room").Clone();else if(kind=="lobby")Lobby=frame.Clone();else if(kind=="ack")Ack=frame.Clone();else if(kind=="pong")Interlocked.Increment(ref Pongs);return Task.CompletedTask;
    }
}
