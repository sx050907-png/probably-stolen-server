using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NightStud;

var address = args.ElementAtOrDefault(0) ?? "http://127.0.0.1:18741";
var key = Environment.GetEnvironmentVariable("NIGHTSTUD_ACCESS_KEY") ?? throw new Exception("Set test key in environment");
using var http = new HttpClient { BaseAddress = new Uri(address) };
int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
var health = await http.GetAsync("/health");
Check(health.IsSuccessStatusCode, "health");
Check(health.Headers.CacheControl?.NoStore == true, "no-store");
var wrong = await http.PostAsJsonAsync("/api/sessions", new { name = "错误口令", accessKey = "wrong" });
Check(wrong.StatusCode == HttpStatusCode.Unauthorized, "access key gate");
async Task<Player> Login(string name) {
    var response = await http.PostAsJsonAsync("/api/sessions", new { name, accessKey = key });
    response.EnsureSuccessStatusCode();
    var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    return await Player.Connect(address, body.GetProperty("token").GetString()!);
}
await using var a = await Login("甲");
await using var b = await Login("乙");
await using var observer = await Login("旁观测试");
var created = await a.Command("create", new() { ["ante"] = 10, ["buyIn"] = 1000 });
Check(created.GetProperty("ok").GetBoolean(), "create");
string code = a.Room.GetProperty("code").GetString()!;
Check(code.Length == 8, "room code");
await b.Command("join", new() { ["code"] = code });
await a.StateWhere(r => r.ValueKind != JsonValueKind.Null && r.GetProperty("players")[1].ValueKind != JsonValueKind.Null);
await observer.Command("join", new() { ["code"] = code }, "room_full");
Check(observer.Room.ValueKind == JsonValueKind.Null, "third player excluded");

async Task ReadyBoth() {
    await a.Command("sync"); await b.Command("sync");
    await a.Command("ready");
    await b.Command("sync"); await b.Command("ready");
    await a.StateWhere(r => r.ValueKind != JsonValueKind.Null && r.GetProperty("phase").GetString() == "playing");
    await a.Command("sync"); await b.Command("sync");
}
void Ledger(JsonElement room) {
    int sum = room.GetProperty("pot").GetInt32();
    foreach (var player in room.GetProperty("players").EnumerateArray()) if (player.ValueKind != JsonValueKind.Null) sum += player.GetProperty("stack").GetInt32();
    Check(sum == 2000, "chip conservation");
}
void Privacy(Player player) {
    var r = player.Room; int own = r.GetProperty("ownSeat").GetInt32();
    var ownCards = r.GetProperty("players")[own].GetProperty("cards");
    var otherCards = r.GetProperty("players")[1-own].GetProperty("cards");
    Check(ownCards.EnumerateArray().All(c => c.ValueKind == JsonValueKind.Number), "own cards visible");
    Check(otherCards[0].ValueKind == JsonValueKind.Null && otherCards.EnumerateArray().Skip(1).All(c => c.ValueKind == JsonValueKind.Number), "opponent hole hidden");
    Check(!r.TryGetProperty("deck", out _) && !r.TryGetProperty("token", out _), "no deck or token in view");
    Ledger(r);
}
Player Actor() => a.Room.GetProperty("actor").GetInt32() == a.Room.GetProperty("ownSeat").GetInt32() ? a : b;
await ReadyBoth();
Privacy(a); Privacy(b);
var notActor = Actor() == a ? b : a;
await notActor.Command("act", new() { ["move"] = "check" }, "out_of_turn");
var actor = Actor();
await actor.Command("act", new() { ["move"] = "check", ["revision"] = -1 }, "stale");
await actor.Command("act", new() { ["move"] = "bet", ["amount"] = -1 }, "bad_amount");
string reused = Guid.NewGuid().ToString("N");
await actor.Command("act", new() { ["move"] = "check", ["id"] = reused });
int revision = actor.Room.GetProperty("revision").GetInt32();
await actor.Command("act", new() { ["move"] = "check", ["id"] = reused }, "duplicate");
Check(actor.Room.GetProperty("revision").GetInt32() == revision, "duplicate cannot mutate");
var rounds = new HashSet<int> { 1 };
for (int n = 0; n < 12; n++) {
    await a.Command("sync"); await b.Command("sync");
    if (a.Room.GetProperty("phase").GetString() == "finished") break;
    rounds.Add(a.Room.GetProperty("round").GetInt32());
    Privacy(a); Privacy(b);
    await Actor().Command("act", new() { ["move"] = "check" });
}
await a.Command("sync"); await b.Command("sync");
Check(rounds.SetEquals(new[] { 1,2,3,4 }), "four betting rounds");
Check(a.Room.GetProperty("phase").GetString() == "finished", "showdown finished");
var hands = a.Room.GetProperty("players").EnumerateArray().Select(p => p.GetProperty("cards").EnumerateArray().Select(c => c.GetInt32()).ToArray()).ToArray();
Check(hands.All(h => h.Length == 5) && hands.SelectMany(h => h).Distinct().Count() == 10, "showdown card uniqueness");
int comparison = Cards.Score(hands[0]).CompareTo(Cards.Score(hands[1]));
Check(a.Room.GetProperty("result").GetProperty("winnerSeat").GetInt32() == (comparison == 0 ? -1 : comparison > 0 ? 0 : 1), "winner by hand strength");
Ledger(a.Room);
Console.WriteLine("PASS: private cards, four rounds, validation, duplicate commands, showdown and chip ledger");

await ReadyBoth();
actor = Actor();
var bet = actor.Room.GetProperty("legalMoves").EnumerateArray().First(m => m.GetProperty("move").GetString() == "bet");
int amount = bet.GetProperty("amount").GetInt32()+10;
await actor.Command("act", new() { ["move"] = "bet", ["amount"] = amount });
await a.Command("sync"); await b.Command("sync");
actor = Actor();
var raise = actor.Room.GetProperty("legalMoves").EnumerateArray().First(m => m.GetProperty("move").GetString() == "raise");
await actor.Command("act", new() { ["move"] = "raise", ["amount"] = raise.GetProperty("amount").GetInt32() });
await a.Command("sync"); await b.Command("sync");
await Actor().Command("act", new() { ["move"] = "call" });
await a.Command("sync"); await b.Command("sync");
Check(a.Room.GetProperty("round").GetInt32() == 2, "bet raise call advances street");
await Actor().Command("act", new() { ["move"] = "allIn" });
await a.Command("sync"); await b.Command("sync");
await Actor().Command("act", new() { ["move"] = "call" });
await a.Command("sync"); await b.Command("sync");
Check(a.Room.GetProperty("phase").GetString() == "finished", "all in runs out");
Check(a.Room.GetProperty("players")[0].GetProperty("cards").GetArrayLength() == 5, "all in completes board");
Ledger(a.Room);
await a.Command("leave"); await b.Command("sync"); await b.Command("leave");

await a.Command("create"); code = a.Room.GetProperty("code").GetString()!;
await b.Command("join", new() { ["code"] = code }); await ReadyBoth();
actor = Actor(); int foldingSeat = actor.Room.GetProperty("ownSeat").GetInt32();
await actor.Command("act", new() { ["move"] = "fold" });
await a.Command("sync"); await b.Command("sync");
Check(a.Room.GetProperty("result").GetProperty("winnerSeat").GetInt32() == 1-foldingSeat, "fold winner");
Privacy(a); Privacy(b); // A fold never publishes the other player's hole card.
await ReadyBoth();
int handNumber = a.Room.GetProperty("hand").GetInt32();
int potBefore = a.Room.GetProperty("pot").GetInt32();
await a.Drop();
await b.StateWhere(r => r.GetProperty("phase").GetString() == "paused");
await b.Command("act", new() { ["move"] = "check" }, "peer_offline");
await a.Reconnect();
await a.StateWhere(r => r.GetProperty("phase").GetString() == "playing");
await b.Command("sync");
Check(a.Room.GetProperty("hand").GetInt32() == handNumber && a.Room.GetProperty("pot").GetInt32() == potBefore, "reconnect restores same hand");
Privacy(a); Privacy(b);
Console.WriteLine("PASS: wagers, all-in, fold privacy, pause and authenticated reconnect");

await a.Drop();
await b.StateWhere(r => r.GetProperty("phase").GetString() == "waiting", 12000);
Check(b.Room.GetProperty("players")[0].ValueKind == JsonValueKind.Null, "timed out seat released");
Check(b.Room.GetProperty("result").GetProperty("winnerSeat").GetInt32() == 1, "disconnect timeout forfeits");
using var stranger = new ClientWebSocket();
stranger.Options.SetRequestHeader("Origin", "https://unrelated.invalid");
try { await stranger.ConnectAsync(Player.WsUri(address), CancellationToken.None); Check(false, "origin check"); }
catch (WebSocketException) { Check(true, "origin check"); }
using var invalid = new ClientWebSocket();
await invalid.ConnectAsync(Player.WsUri(address), CancellationToken.None);
await invalid.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"protocol\":1,\"token\":\"invalid\"}"), WebSocketMessageType.Text, true, CancellationToken.None);
var bytes = new byte[2048]; var received = await invalid.ReceiveAsync(bytes, CancellationToken.None);
Check(Encoding.UTF8.GetString(bytes,0,received.Count).Contains("unauthorized"), "invalid token rejected");
invalid.Abort();
Console.WriteLine("PASS: disconnect forfeit, origin restriction and invalid session rejection");

var rng = new Random(877);
for (int n=0;n<200;n++) {
    var game = new Table(rng); game.Stack[0]=game.Stack[1]=1000; game.Start();
    for(int step=0;step<1000 && !game.Done;step++) {
        var choices=Enum.GetValues<Move>().Where(game.Legal).ToArray(); game.Play(choices[rng.Next(choices.Length)]);
        Check(game.Stack.Sum()+game.Pot==2000, "random game conservation");
    }
    Check(game.Done, "random game terminates");
}
Console.WriteLine($"PASS: {checks} assertions; 200 additional full rule simulations. Network integration completed.");

sealed class Player : IAsyncDisposable
{
    readonly string address,token;
    readonly Channel<JsonElement> inbox=Channel.CreateUnbounded<JsonElement>();
    readonly object gate=new();
    ClientWebSocket socket=null!;
    CancellationTokenSource stop=new();
    Task reading=Task.CompletedTask;
    bool connected;
    JsonElement room=JsonDocument.Parse("null").RootElement.Clone();
    public JsonElement Room {get{lock(gate)return room;}}
    Player(string address,string token){this.address=address;this.token=token;}
    public static Uri WsUri(string address)=>new(address.Replace("https://","wss://").Replace("http://","ws://").TrimEnd('/')+"/ws");
    public static async Task<Player> Connect(string address,string token){var player=new Player(address,token);await player.Reconnect();await player.StateWhere(_=>true);return player;}
    public async Task Reconnect(){
        connected=true;
        socket=new ClientWebSocket();stop=new CancellationTokenSource();
        await socket.ConnectAsync(WsUri(address),CancellationToken.None);
        reading=Read();await Send(new {type="hello",protocol=1,token});
    }
    async Task Read(){
        try{while(!stop.IsCancellationRequested){
            using var data=new MemoryStream();var buffer=new byte[4096];WebSocketReceiveResult frame;
            do{frame=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),stop.Token);if(frame.MessageType==WebSocketMessageType.Close)return;data.Write(buffer,0,frame.Count);}while(!frame.EndOfMessage);
            using var json=JsonDocument.Parse(data.ToArray());var value=json.RootElement.Clone();
            if(value.GetProperty("type").GetString()=="state"){lock(gate)room=value.GetProperty("room").Clone();}
            await inbox.Writer.WriteAsync(value);
        }}catch(Exception ex)when(ex is WebSocketException or OperationCanceledException or ObjectDisposedException){}
    }
    Task Send(object value)=>socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))),WebSocketMessageType.Text,true,CancellationToken.None);
    async Task<JsonElement> Wait(Func<JsonElement,bool> predicate,int timeout=8000){using var cts=new CancellationTokenSource(timeout);while(true){var value=await inbox.Reader.ReadAsync(cts.Token);if(predicate(value))return value;}}
    public Task<JsonElement> StateWhere(Func<JsonElement,bool> predicate,int timeout=8000)=>Wait(m=>m.GetProperty("type").GetString()=="state"&&predicate(m.GetProperty("room")),timeout);
    public async Task<JsonElement> Command(string type,Dictionary<string,object>? extra=null,string? error=null){
        await Task.Delay(60); // Stay below the same per-session rate limit as a game client.
        if(type=="sync"){await Send(new {type="sync"});return await StateWhere(_=>true);}
        var command=new Dictionary<string,object>{["type"]=type,["id"]=Guid.NewGuid().ToString("N")};
        if(Room.ValueKind!=JsonValueKind.Null)command["revision"]=Room.GetProperty("revision").GetInt64();
        if(extra!=null)foreach(var pair in extra)command[pair.Key]=pair.Value;
        await Send(command);
        var ack=await Wait(m=>m.GetProperty("type").GetString()=="ack"&&m.GetProperty("id").GetString()==(string)command["id"]);
        if(ack.GetProperty("ok").GetBoolean()!=(error==null) || (error!=null&&ack.GetProperty("error").GetString()!=error))throw new Exception($"{type}: {ack}");
        await StateWhere(_=>true);return ack;
    }
    public async Task Drop(){if(!connected)return;connected=false;socket.Abort();stop.Cancel();await reading;socket.Dispose();stop.Dispose();}
    public async ValueTask DisposeAsync(){await Drop();}
}
