using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using NightStud.Server;
using NightStud.Shared;

int checks=0,ties=0,assetActions=0;
void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
void Fault(Action act,string code){try{act();throw new Exception("accepted "+code);}catch(WireFault e){Check(e.Code==code,code+" got "+e.Code);}}
string Id()=>Guid.NewGuid().ToString("N");
string root=Path.GetFullPath(args.ElementAtOrDefault(0)??"work/assets060-tests");Directory.CreateDirectory(root);
string payload=args.Length>1?File.ReadAllText(args[1]):Wire.Encode(new{label="样本",nodes=new[]{new{identifier="processed_juice",unitCount=1,children=Array.Empty<int>(),slots=Array.Empty<int>(),tags=Array.Empty<object>()}}});
foreach(string mode in new[]{"stud","three","holdem"})for(int seed=0;seed<400;seed++){
    var rng=new Random(seed*9+mode.Length);var game=new OnlineRound(mode,10,1,new[]{1000,1000},rng);var rounds=new HashSet<int>();int steps=0;
    while(!game.Done&&steps++<150){
        int actor=game.Actor;int before=game.Stack.Sum()+game.Pot;bool used=false;
        if(rounds.Add(game.Round)||steps%4==0){
            try{int quote=Math.Min(13+rng.Next(58),game.Stack[1-actor]+game.Due);if(quote>0){var plan=game.QuoteAsset(quote);int cash=game.Stack[actor];game.PlayAsset(actor,quote,plan.Move,plan.Amount);Check(game.Stack.Sum()+game.Pot==before+quote,"asset value enters pot once");if(!game.Done)Check(game.Stack[actor]==cash-plan.Cash,"only agreed cash removed by physical bet");used=true;assetActions++;}}
            catch(WireFault e)when(e.Code=="asset_amount"){}
        }
        if(!used){var legal=game.Legal();var move=legal.FirstOrDefault(m=>m.Move=="check")??legal.FirstOrDefault(m=>m.Move=="call")??legal.FirstOrDefault(m=>m.Move=="bet")??legal.First(m=>m.Move=="allIn");game.Play(move.Move,move.Move is "bet" or "raise"?move.Amount:null);}
        Check(game.Stack.Sum()+game.Pot==2000+game.AssetValue,"mixed pot conserves cash plus physical backing");
    }
    Check(game.Done,"seeded physical hand completes "+mode);Check(game.CreditBalances().Sum()==2000,"physical winnings never become extra credits "+mode);if(game.Winner<0)ties++;
}
Check(assetActions>1200&&ties>0,"all three modes exercise assets and tied settlement");
foreach(string mode in new[]{"stud","three","holdem"}){
    var game=new OnlineRound(mode,10,1,new[]{1000,1000},new Random(5));int seat=game.Actor;var p=game.QuoteAsset(25);game.PlayAsset(seat,25,p.Move,p.Amount);game.VoidHand();Check(game.CreditBalances().SequenceEqual(new[]{1000,1000}),"void returns each person's original cash "+mode);
}

var dbAccounts=new Accounts(Path.Combine(root,"group-ledger"));var ledger=new StakeLedger(dbAccounts);var goods=new GoodsLedger(dbAccounts);
foreach(int count in new[]{3,4}){
    var people=Enumerable.Range(0,count).Select(_=>Id()).ToArray();var saves=people.Select(_=>Id()).ToArray();string room=Id();var lots=people.Select(_=>Id()).ToArray();
    for(int i=0;i<count;i++){ledger.Transfer(people[i],saves[i],Id(),"in",1000);ledger.Reserve(people[i],saves[i],room,i,100);goods.Offer(people[i],saves[i],lots[i],room,1,i,payload,12+i*5);}
    goods.Approve(lots[0],people[1],people);
    Fault(()=>ledger.BeginHand(room,Enumerable.Repeat(100,count).ToArray(),(db,tx)=>goods.Begin(db,tx,room,1,people)),"goods_unconfirmed");
    for(int i=0;i<count;i++)for(int j=0;j<count;j++)if(i!=j)goods.Approve(lots[i],people[j],people);
    ledger.BeginHand(room,Enumerable.Repeat(100,count).ToArray(),(db,tx)=>goods.Begin(db,tx,room,1,people));
    int[] balances=count==3?new[]{80,110,110}:new[]{70,100,110,120};int[] winners=count==3?new[]{1,2}:new[]{2,3};
    ledger.FinishHand(room,balances,(db,tx)=>goods.Finish(db,tx,room,1,winners[0],people,saves,winners));
    ledger.FinishHand(room,balances,(db,tx)=>goods.Finish(db,tx,room,1,winners[0],people,saves,winners));
    foreach(int i in Enumerable.Range(0,count)){var lot=goods.Find(lots[i])!;Check(winners.Select(w=>people[w]).Contains(lot.Recipient),"only winning group participants receive items");Check(lot.RecipientSave==saves[Array.IndexOf(people,lot.Recipient)],"group physical delivery bound to recipient save");if(winners.Contains(i))Check(lot.Recipient==people[i],"group winner keeps their own pledged object");}
    Check(lots.Select(id=>goods.Find(id)!.Id).Distinct().Count()==count,"each group item assigned once");
}

Room Actual(RoomHub hub,Peer p)=>(Room)((System.Collections.IDictionary)typeof(RoomHub).GetField("rooms",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(hub)!)[p.Room.GetProperty("code").GetString()!]!;
for(int scenario=0;scenario<8;scenario++){
    var accounts=new Accounts(Path.Combine(root,"room-"+scenario));var cash=new StakeLedger(accounts);var items=new GoodsLedger(accounts);var hub=new RoomHub(3,null,accounts);
    var identities=new[]{await accounts.Register("player_a","test-password-4638","玩家甲"),await accounts.Register("player_b","test-password-4638","玩家乙")};
    var peers=identities.Select(i=>new Peer(hub,hub.LoginAccount(i),Id())).ToArray();
    try{
        foreach(var p in peers)cash.Transfer(p.Session.Id,p.Save,Id(),"in",1000);
        await peers[0].Ok("create",c=>{c.Economy="store";c.Game=scenario%3==0?"stud":scenario%3==1?"holdem":"three";c.BuyIn=1000;c.Ante=10;});
        string code=peers[0].Room.GetProperty("code").GetString()!;await peers[1].Ok("join",c=>c.Code=code);foreach(var p in peers)await p.Ok("fund");foreach(var p in peers)await p.Ok("ready");
        var room=Actual(hub,peers[0]);var game=room.Game!;int actor=game.Actor;var owner=peers[actor];var other=peers[1-actor];
        if(scenario==3)File.WriteAllText(Path.Combine(root,"native-live-room.json"),Wire.Encode(new{type="state",room=owner.Room}));
        string lot=Id();var plan=game.QuoteAsset(25);
        await owner.Ok("goodsOffer",c=>{c.Wager=true;c.Transfer=lot;c.Payload=payload;c.Price=25;c.Amount=plan.Amount;c.Move=plan.Move;});
        Check(game.AssetValue==0&&items.Find(lot)!.State=="offered","offer waits for recipient restoration check");
        if(scenario==3)File.WriteAllText(Path.Combine(root,"native-pending-room.json"),Wire.Encode(new{type="state",room=owner.Room}));
        var denied=await owner.Command("act",c=>c.Move="fold");Check(denied.GetProperty("error").GetString()=="asset_pending","normal moves pause during pending asset");
        if(scenario==0){await other.Ok("goodsReject",c=>c.Transfer=lot);Check(items.Find(lot)!.Recipient==owner.Session.Id&&game.AssetValue==0,"unreadable physical proposal returns without a bet");continue;}
        if(scenario==1){using(var db=accounts.Open())using(var c=db.CreateCommand()){c.CommandText="UPDATE goods_stakes SET expires=$t WHERE id=$i";c.Parameters.AddWithValue("$t",DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O"));c.Parameters.AddWithValue("$i",lot);c.ExecuteNonQuery();}hub.Expire();Check(items.Find(lot)!.State=="claimable"&&game.Actor==actor&&game.AssetValue==0,"timed-out proposal returns and turn is unchanged");continue;}
        await other.Ok("goodsApprove",c=>c.Transfer=lot);int total=game.AssetValue;
        await other.Ok("goodsApprove",c=>c.Transfer=lot);Check(game.AssetValue==25&&game.AssetValue==total,"duplicate approval applies material wager once");
        Check(items.Find(lot)!.State=="locked","accepted material wager is durable");
        var cancel=await owner.Command("goodsCancel",c=>c.Transfer=lot);Check(cancel.GetProperty("error").GetString()=="goods_playing","locked material wager cannot be taken back");
        if(scenario==2){cash.RecoverInterruptedServer();items.Recover();Check(peers.All(p=>cash.Balance(p.Session.Id,p.Save)==1000)&&items.Find(lot)!.Recipient==owner.Session.Id,"server restart returns original cash and physical escrow");continue;}
        if(scenario==6){
            using(var db=accounts.Open())using(var c=db.CreateCommand()){c.CommandText="CREATE TRIGGER asset_fixture_fail BEFORE UPDATE OF state ON goods_stakes WHEN NEW.state='claimable' BEGIN SELECT RAISE(ABORT,'fixture failure'); END";c.ExecuteNonQuery();}
            var response=await peers[game.Actor].Command("act",c=>c.Move="fold");Check(response.GetProperty("error").GetString()=="cash_storage"&&room.CashFault&&items.Find(lot)!.State=="locked","cash plus physical finalization rolls back on write failure");
            using(var db=accounts.Open())using(var c=db.CreateCommand()){c.CommandText="DROP TRIGGER asset_fixture_fail";c.ExecuteNonQuery();}
            hub.Expire();Check(!room.CashFault&&items.Find(lot)!.State=="claimable","failed mixed settlement resumes once storage recovers");
            foreach(var p in peers)await p.Ok("leave");Check(peers.Sum(p=>cash.Balance(p.Session.Id,p.Save))==2000,"retrying mixed settlement creates no credit");continue;
        }
        if(scenario==7){foreach(var p in peers){p.Drop();p.Session.Disconnected=DateTime.UtcNow.AddSeconds(-4);}hub.Expire();Check(peers.All(p=>cash.Balance(p.Session.Id,p.Save)==1000)&&items.Find(lot)!.Recipient==owner.Session.Id,"both clients disconnect: original cash and each item returned");continue;}
        var allLots=new List<string>{lot};int guard=0;var roundSeen=new HashSet<int>{game.Round};
        while(!game.Done&&guard++<100){
            int s=game.Actor;var p=peers[s];
            if(scenario==3&&roundSeen.Add(game.Round)){
                string next=Id();var wager=game.QuoteAsset(20);await p.Ok("goodsOffer",c=>{c.Wager=true;c.Transfer=next;c.Payload=payload;c.Price=20;c.Amount=wager.Amount;c.Move=wager.Move;});await peers[1-s].Ok("goodsApprove",c=>c.Transfer=next);allLots.Add(next);
            }else{var legal=game.Legal();var move=scenario==4?legal.Single(m=>m.Move=="fold"):legal.FirstOrDefault(m=>m.Move=="check")??legal.FirstOrDefault(m=>m.Move=="call")??legal.FirstOrDefault(m=>m.Move=="bet")??legal.Single(m=>m.Move=="allIn");await p.Ok("act",c=>{c.Move=move.Move;if(move.Move is "bet" or "raise")c.Amount=move.Amount;});}
        }
        Check(game.Done&&game.CreditBalances().Sum()==2000,"server mixed-wager hand settles "+scenario);
        foreach(string id in allLots){var row=items.Find(id)!;Check(row.State=="claimable"&&row.Recipient==peers[game.Winner<0?row.Seat:game.Winner].Session.Id,"mixed-wager winner receives actual object");}
        foreach(var p in peers)await p.Ok("leave");Check(peers.Sum(p=>cash.Balance(p.Session.Id,p.Save))==2000,"cash returned from mixed game remains conserved");
    }finally{foreach(var p in peers)await p.DisposeAsync();}
}
foreach(string mode in new[]{"landlord","mahjong"}){
    var accounts=new Accounts(Path.Combine(root,mode));var cash=new StakeLedger(accounts);var items=new GoodsLedger(accounts);var hub=new RoomHub(3,null,accounts);int count=OnlineRound.Capacity(mode);
    var players=new List<Peer>();for(int n=0;n<count;n++){var identity=await accounts.Register("group_player_"+n,"test-password-4739","桌友"+n);players.Add(new Peer(hub,hub.LoginAccount(identity),Id()));}
    try{
        foreach(var p in players)cash.Transfer(p.Session.Id,p.Save,Id(),"in",1000);
        await players[0].Ok("create",c=>{c.Economy="store";c.Game=mode;c.BuyIn=1000;c.Ante=10;});string code=players[0].Room.GetProperty("code").GetString()!;
        foreach(var p in players.Skip(1))await p.Ok("join",c=>c.Code=code);foreach(var p in players)await p.Ok("fund");
        var room=Actual(hub,players[0]);var lots=players.Select(_=>Id()).ToArray();
        for(int i=0;i<count;i++){int seat=i;await players[i].Ok("goodsOffer",c=>{c.Transfer=lots[seat];c.Payload=payload;c.Price=12+seat;});}
        File.WriteAllText(Path.Combine(root,"native-group-"+mode+".json"),Wire.Encode(new{type="state",room=players[0].Room}));
        for(int i=0;i<count-1;i++)await players[i].Ok("ready");var denied=await players[count-1].Command("ready");Check(denied.GetProperty("error").GetString()=="goods_unconfirmed"&&room.Game==null,"group table does not deal before every native item consent "+mode);
        for(int i=0;i<count;i++)for(int j=0;j<count;j++)if(i!=j){int owner=i;await players[j].Ok("goodsApprove",c=>c.Transfer=lots[owner]);}
        foreach(var p in players)await p.Ok("ready");var game=room.Game!;int steps=0;
        while(!game.Done&&steps++<1500){var suggestion=game.Suggest();await players[game.Actor].Ok("act",c=>{c.Move=suggestion.Move;c.Amount=suggestion.Amount;c.Choice=suggestion.Choice;c.Cards=suggestion.Cards;});}
        Check(game.Done,"actual group game with material collateral completes "+mode);var winners=Enumerable.Range(0,count).Where(i=>game.Stack[i]>room.Initial[i]).ToArray();
        foreach(string id in lots){var row=items.Find(id)!;Check(row.State=="claimable","group physical result is durable "+mode);Check(winners.Length==0?row.Recipient==row.Owner:winners.Select(i=>players[i].Session.Id).Contains(row.Recipient),"actual group winners receive physical collateral "+mode);}
        foreach(var p in players)await p.Ok("leave");Check(players.Sum(p=>cash.Balance(p.Session.Id,p.Save))==count*1000,"group physical hand creates no credits "+mode);
    }finally{foreach(var p in players)await p.DisposeAsync();}
}
var message=$"PASS {checks} checks; {assetActions} seeded physical wagers and {ties} tied hands: three poker modes, agreed cash supplements, material/cash conservation, void refunds, three/four-party consent and whole-item distribution, complete landlord and mahjong physical games, durable live proposals, timeout/rejection, exactly-once approval, locked cancellation denial, crash refunds and atomic settlement retry. In-memory peers only; no TCP listener, native client pair or user campaigns.";
File.WriteAllText(Path.Combine(root,"success.txt"),message);Console.WriteLine(message);

sealed class Peer:IAsyncDisposable{
    readonly RoomHub hub;readonly Sink socket=new();readonly Client client;readonly CancellationTokenSource stop=new();readonly Task pump;
    public Session Session{get;}public string Save{get;}public JsonElement Room=>socket.Room;
    public Peer(RoomHub hub,Session session,string save){this.hub=hub;Session=session;Save=save;client=new Client(socket);pump=client.Pump(stop.Token);hub.Attach(new Command{Type="hello",Protocol=1,Token=Session.Token,Games=OnlineRound.Games,Capabilities=new[]{"storeStakes","roundGoods"}},client);}
    // State-machine tests isolate monetary transitions. Traffic gates are covered separately.
    public async Task Flush(){Session.RateCount=0;int before=socket.Pongs;hub.Handle(Session,client,new(){Type="ping"});for(int n=0;n<3000&&socket.Pongs==before;n++)await Task.Delay(1);if(socket.Pongs==before)throw new Exception("Flush timeout");}
    public async Task<JsonElement> Command(string type,Action<Command>? set=null){await Flush();var command=new Command{Type=type,Id=Guid.NewGuid().ToString("N"),SaveId=Save,Revision=Room.ValueKind==JsonValueKind.Object?Room.GetProperty("revision").GetInt64():null};set?.Invoke(command);hub.Handle(Session,client,command);await Flush();return socket.Ack;}
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
