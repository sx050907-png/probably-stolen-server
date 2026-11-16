using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using NightStud.Server;

int checks=0;
void Check(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
void Fault(Action action,string code){try{action();throw new Exception("Expected "+code);}catch(WireFault e){Check(e.Code==code,"rejection "+code);}}
string Id()=>Guid.NewGuid().ToString("N");
string root=args.FirstOrDefault()??Path.Combine("work","cash050-"+Id());Directory.CreateDirectory(root);
var accounts=new Accounts(Path.Combine(root,"ledger"));var ledger=new StakeLedger(accounts);string a=Id(),b=Id(),save=Id();
var goodsLedger=new GoodsLedger(accounts);var kind=GoodsLedger.Catalogue["processed_juice"];
string Lot(string identifier="processed_juice",string key="")=>Wire.Encode(new{label="样本",nodes=new[]{new{identifier,unitCount=1,children=Array.Empty<int>(),slots=Array.Empty<int>(),tags=key.Length==0?Array.Empty<object>():new object[]{new{key}},shape=(string?)null,modified=(string?)null}}});
string fixtureLot=Lot();Check(GoodsLedger.Catalogue.Keys.Except(NightStud.Shared.GearCatalogue.All.Select(g=>g.Identifier)).Count()==443,"native catalogue still contains exactly the exported non-story items");
Check(NightStud.Shared.GearCatalogue.All.All(g=>GoodsLedger.Catalogue.TryGetValue(g.Identifier,out var kind)&&kind.BaseValue==g.Price),"all ten physical devices are explicitly listed with their declared base values");
foreach(string plot in new[]{"lou_locker","lou_etched_round","luxury_supply_crate"})Fault(()=>GoodsLedger.Validate(Lot(plot)),"protected_lot");
if(args.Length>1)Check(GoodsLedger.Validate(File.ReadAllText(args[1])).Id=="processed_juice","actual native generated item document accepted by server");
Check(GoodsLedger.Validate(fixtureLot).Id=="processed_juice","ordinary native goods accepted");Fault(()=>GoodsLedger.Validate(Lot("unknown_quest_item")),"protected_lot");Fault(()=>GoodsLedger.Validate(Lot(key:"questbound")),"protected_lot");
string missingLot=Id();goodsLedger.Cancel(a,save,missingLot);Check(goodsLedger.Offer(a,save,missingLot,Id(),1,0,fixtureLot,12).State=="cancelled","cancel-before-delayed physical offer tombstone");
string hoarder=Id();var quotaIds=new List<string>();for(int i=0;i<8;i++){string id=Id();quotaIds.Add(id);goodsLedger.Offer(hoarder,save,id,Id(),1,0,fixtureLot,12);}
Fault(()=>goodsLedger.Offer(hoarder,save,Id(),Id(),1,0,fixtureLot,12),"goods_quota");goodsLedger.Cancel(hoarder,save,quotaIds[0]);goodsLedger.Delivered(quotaIds[0],hoarder,save);goodsLedger.Offer(hoarder,save,Id(),Id(),1,0,fixtureLot,12);
string deposit=Id();ledger.Transfer(a,save,deposit,"in",2000);ledger.Transfer(a,save,deposit,"in",2000);
Check(ledger.Balance(a,save)==2000,"duplicate deposit once");Fault(()=>ledger.Transfer(a,save,deposit,"in",2001),"cash_conflict");
Fault(()=>ledger.Find(b,save,deposit),"cash_scope");Fault(()=>ledger.Find(a,Id(),deposit),"cash_scope");
Fault(()=>ledger.Transfer(a,save,Id(),"out",2001),"cash_balance");Fault(()=>ledger.Transfer(a,save,Id(),"in",-5),"cash_amount");
string cancelled=Id();Check(ledger.CancelDeposit(a,save,cancelled,300).State=="cancelled","cancel missing deposit tombstone");
Check(ledger.Transfer(a,save,cancelled,"in",300).State=="cancelled"&&ledger.Balance(a,save)==2000,"late deposit cannot revive cancelled transfer");
string outCancel=Id();ledger.CancelDeposit(a,save,outCancel,50,"out");Check(ledger.Acknowledge(a,save,outCancel).State=="cancelled","cancelled withdrawal stays cancelled");
string nearCap=Id(),edgeRoom=Id();using(var db=accounts.Open())using(var c=db.CreateCommand()){c.CommandText="INSERT INTO cash_wallets(account,save_id,balance) VALUES($a,$s,999900000)";c.Parameters.AddWithValue("$a",nearCap);c.Parameters.AddWithValue("$s",save);c.ExecuteNonQuery();}
Fault(()=>ledger.Reserve(nearCap,save,edgeRoom,0,100000),"cash_balance");Check(ledger.Balance(nearCap,save)==999900000,"near-cap table reserve rejected before mutation");
for(int n=0;n<16;n++)ledger.Transfer(nearCap,save,Id(),"out",1000000);var edge=ledger.Reserve(nearCap,save,edgeRoom,0,1000000);
for(int i=1;i<4;i++){string owner=Id();ledger.Transfer(owner,save,Id(),"in",1000000);ledger.Reserve(owner,save,edgeRoom,i,1000000);}
ledger.BeginHand(edgeRoom,new[]{1000000,1000000,1000000,1000000});ledger.FinishHand(edgeRoom,new[]{4000000,0,0,0});ledger.Release(edge.Id,nearCap,save);Check(ledger.Balance(nearCap,save)==986900000,"large legal table payout can always be released below cap");ledger.RecoverInterruptedServer();
Check(ledger.Transfer(a,save,outCancel,"out",50).State=="cancelled"&&ledger.Balance(a,save)==2000,"late withdrawal cancelled");
string withdrawal=Id();ledger.Transfer(a,save,withdrawal,"out",500);ledger.Transfer(a,save,withdrawal,"out",500);ledger.Acknowledge(a,save,withdrawal);ledger.Acknowledge(a,save,withdrawal);
Check(ledger.Balance(a,save)==1500&&ledger.Find(a,save,withdrawal)!.State=="delivered","withdraw reserved once and acknowledged idempotently");
ledger.Transfer(b,save,Id(),"in",2000);string roomId=Id();var ha=ledger.Reserve(a,save,roomId,0,1000);var hb=ledger.Reserve(b,save,roomId,1,1000);
Check(ha.Id==ledger.Reserve(a,save,roomId,0,1000).Id&&ledger.Balance(a,save)==500,"repeat fund does not debit twice");
Fault(()=>ledger.Reserve(b,save,roomId,0,1000),"cash_scope");
ledger.BeginHand(roomId,new[]{1000,1000});Fault(()=>ledger.Release(ha.Id,a,save),"cash_playing");
Fault(()=>ledger.FinishHand(roomId,new[]{1200,801}),"cash_ledger");
ledger.FinishHand(roomId,new[]{1200,800});ledger.FinishHand(roomId,new[]{1200,800});
Fault(()=>ledger.FinishHand(roomId,new[]{1300,700}),"cash_ledger");
Check(ledger.Release(ha.Id,a,save)==1200&&ledger.Release(ha.Id,a,save)==0,"leave payout once");
Check(ledger.Release(hb.Id,b,save)==800&&ledger.Balance(a,save)==1700&&ledger.Balance(b,save)==1800,"persistent zero sum");
ledger.Transfer(a,save,Id(),"in",10000);ledger.Transfer(b,save,Id(),"in",10000);
for(int n=0;n<20;n++){
    string r=Id();var x=ledger.Reserve(a,save,r,0,1000);ledger.Reserve(b,save,r,1,1000);ledger.BeginHand(r,new[]{1000,1000});
    if(n%2==0)ledger.FinishHand(r,new[]{1100,900});
    int aa=ledger.Balance(a,save),bb=ledger.Balance(b,save);var fresh=new StakeLedger(new Accounts(Path.Combine(root,"ledger")));
    Check(fresh.RecoverInterruptedServer()==2,"restart releases both seats");
    Check(fresh.Balance(a,save)==aa+(n%2==0?1100:1000)&&fresh.Balance(b,save)==bb+(n%2==0?900:1000),"restart uses finished result or refunds unfinished hand");
    Check(fresh.RecoverInterruptedServer()==0,"repeated restart cannot repay");
}

for(int n=0;n<4;n++){
    string r=Id(),x=Id(),y=Id();ledger.Reserve(a,save,r,0,1000);ledger.Reserve(b,save,r,1,1000);
    goodsLedger.Offer(a,save,x,r,1,0,fixtureLot,12);goodsLedger.Offer(b,save,y,r,1,1,fixtureLot,12);goodsLedger.Approve(x,b);goodsLedger.Approve(y,a);
    ledger.BeginHand(r,new[]{1000,1000},(db,tx)=>goodsLedger.Begin(db,tx,r,1,new[]{a,b}));
    if(n==0){try{ledger.FinishHand(r,new[]{900,1100},(db,tx)=>{goodsLedger.Finish(db,tx,r,1,1,new[]{a,b},new[]{save,save});throw new IOException("synthetic commit failure");});}catch(IOException){}Check(goodsLedger.Find(x)!.State=="locked","cash and goods transaction rolls back together");}
    if(n%2==1)ledger.FinishHand(r,new[]{900,1100},(db,tx)=>goodsLedger.Finish(db,tx,r,1,1,new[]{a,b},new[]{save,save}));
    int aa=ledger.Balance(a,save),bb=ledger.Balance(b,save);ledger.RecoverInterruptedServer();goodsLedger.Recover();
    Check(ledger.Balance(a,save)==aa+(n%2==0?1000:900)&&ledger.Balance(b,save)==bb+(n%2==0?1000:1100),"restart credit matches physical result");
    Check(goodsLedger.Find(x)!.Recipient==(n%2==0?a:b)&&goodsLedger.Find(y)!.Recipient==b,"restart returns unfinished stakes and preserves committed winner");
    goodsLedger.Delivered(x,goodsLedger.Find(x)!.Recipient,save);Check(goodsLedger.Find(x)!.State=="delivered","physical claim delivered");
}
var roomAccounts=new Accounts(Path.Combine(root,"rooms"));var roomLedger=new StakeLedger(roomAccounts);var hub=new RoomHub(3,accounts:roomAccounts);
var people=new List<Peer>();
for(int i=0;i<4;i++){var identity=await roomAccounts.Register("cashqa_"+i,"test-only-password-050","测试店主"+i);people.Add(new Peer(hub,hub.LoginAccount(identity),Id()));}
Room Current(Peer p)=>((Dictionary<string,Room>)typeof(RoomHub).GetField("rooms",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(hub)!)[p.Session.RoomCode!];
try{
    people[0].Session.FundsCount=60;
    Check((await people[0].Command("cashCancel",c=>{c.Transfer=Id();c.Direction="in";c.Amount=1;})).GetProperty("error").GetString()=="funds_rate","financial mutation flood rejected");
    people[0].Session.FundsSince=DateTime.UtcNow.AddMinutes(-2);await people[0].Ok("cashCancel",c=>{c.Transfer=Id();c.Direction="in";c.Amount=1;});
    string missingTrade=Id();await people[0].Ok("tradeReceipt",c=>c.Trade=missingTrade);
    await people[0].Ok("tradeOffer",c=>{c.Trade=missingTrade;c.Target=people[1].Session.Id;c.Payload=fixtureLot;c.Price=12;});
    using(var db=roomAccounts.Open())using(var c=db.CreateCommand()){c.CommandText="SELECT state FROM trades WHERE id=$i";c.Parameters.AddWithValue("$i",missingTrade);Check((string?)c.ExecuteScalar()=="cancelled","missing shop trade persisted cancelled before refund; late offer cannot revive");}
    foreach(string mode in OnlineRound.Games){
        var peers=people.Take(OnlineRound.Capacity(mode)).ToArray();foreach(var peer in peers)roomLedger.Transfer(peer.Session.Id,peer.Save,Id(),"in",10000);
        var before=peers.Select(p=>roomLedger.Balance(p.Session.Id,p.Save)).ToArray();
        await peers[0].Ok("create",c=>{c.Game=mode;c.Economy="store";c.BuyIn=1000;});string code=peers[0].Room.GetProperty("code").GetString()!;
        foreach(var peer in peers.Skip(1))await peer.Ok("join",c=>c.Code=code);
        var room=Current(peers[0]);Check(room.Seats.All(s=>s!.Balance==0),"joining does not create free credit "+mode);
        Check((await peers[0].Command("ready")).GetProperty("error").GetString()=="need_fund","unfunded ready rejected "+mode);
        Check((await peers[0].Command("botAdd")).GetProperty("error").GetString()=="cash_bot","real credit bots rejected "+mode);
        foreach(var peer in peers)await peer.Ok("fund");
        var lotIds=new[]{Id(),Id()};
        if(peers.Length==2){
            for(int i=0;i<2;i++){int seat=i;await peers[i].Ok("goodsOffer",c=>{c.Transfer=lotIds[seat];c.Payload=fixtureLot;c.Price=12;});}
            Check(peers[0].Room.GetProperty("goods").GetArrayLength()==2,"physical goods visible on table "+mode);
            Check(!peers[0].Room.GetRawText().Contains("nodes"),"table state contains metadata, not large item documents "+mode);
            await peers[0].Ok("ready");var denied=await peers[1].Command("ready");Check(denied.GetProperty("error").GetString()=="goods_unconfirmed","both parties must approve physical goods "+mode);
            Check(room.Game==null,"unconfirmed goods never deal cards "+mode);
            for(int i=0;i<2;i++){int other=1-i;await peers[i].Ok("goodsApprove",c=>c.Transfer=lotIds[other]);}
        }
        foreach(var peer in peers)await peer.Ok("ready");int steps=0;
        while(room.Game is {Done:false} game){
            Check(++steps<1500,"cash round terminates "+mode);var actor=peers[game.Actor];Command move;
            if(mode is "landlord" or "mahjong")move=game.Suggest();
            else if(mode=="dice")move=new(){Move=game.Legal()[0].Move};
            else{var pick=game.Legal().FirstOrDefault(x=>x.Move is "check" or "call" or "stand" or "showdown")??game.Legal()[0];move=new(){Move=pick.Move};}
            await actor.Ok("act",c=>{c.Move=move.Move;c.Amount=move.Amount;c.Choice=move.Choice;c.Cards=move.Cards;});
        }
        Check(room.Game!.Stack.Sum()==1000*peers.Length,"cash final total "+mode);var final=room.Game.Stack.ToArray();
        if(peers.Length==2){var gl=new GoodsLedger(roomAccounts);for(int i=0;i<2;i++){var lot=gl.Find(lotIds[i])!;int winner=room.Game.Winner<0?i:room.Game.Winner;Check(lot.State=="claimable"&&lot.Recipient==peers[winner].Session.Id&&lot.RecipientSave==peers[winner].Save,"physical winner or tie return "+mode);gl.Delivered(lot.Id,peers[winner].Session.Id,peers[winner].Save);gl.Delivered(lot.Id,peers[winner].Session.Id,peers[winner].Save);Check(gl.Find(lot.Id)!.Payload=="","claim acknowledgement retires payload once "+mode);}}
        File.WriteAllText(Path.Combine(root,mode+"-finished.json"),Wire.Encode(new{type="state",room=peers[0].Room}));
        foreach(var peer in peers)await peer.Ok("leave");
        for(int i=0;i<peers.Length;i++)Check(roomLedger.Balance(peers[i].Session.Id,peers[i].Save)==before[i]-1000+final[i],"native stake settlement "+mode+" seat "+i);
    }
    await people[0].Ok("create",c=>{c.Game="stud";c.Economy="store";});string resumeCode=people[0].Room.GetProperty("code").GetString()!;
    await people[0].Ok("fund");int balance=Current(people[0]).Seats[0]!.Balance;
    await people[1].Ok("join",c=>c.Code=resumeCode);Check(Current(people[0]).Seats[0]!.Balance==balance,"join never resets funded balance");
    await people[1].Ok("fund");await people[0].Ok("ready");await people[1].Ok("ready");
    var live=Current(people[0]);long total=people.Take(2).Sum(p=>(long)roomLedger.Balance(p.Session.Id,p.Save))+live.Initial.Sum();
    Check((await people[live.Game!.Actor].Command("act",c=>{c.Move=live.Game.Legal()[0].Move;c.SaveId=Id();})).GetProperty("error").GetString()=="cash_scope","switched-save action rejected");
    foreach(var peer in people.Take(2)){peer.Drop();peer.Session.Disconnected=DateTime.UtcNow.AddSeconds(-4);}hub.Expire();
    Check(people.Take(2).Sum(p=>(long)roomLedger.Balance(p.Session.Id,p.Save))==total,"both offline refund and release holds");
    Check(people.Take(2).All(p=>p.Session.RoomCode==null),"expired seats cleared");
}finally{foreach(var peer in people)await peer.DisposeAsync();}
string proof=$"PASS {checks} checks: durable native-credit transfer receipts, cancel-before-retry, ownership and save scope, no duplicate payout, 20 restart scenarios, all seven real-credit game settlements, no bot money, preserved joins, switched-save rejection, offline refunds; native item allowlist, protected item rejection, physical offer cancellation tombstones, mutual acceptance, five two-seat mode physical payouts and tie returns. In-memory sockets only.";
File.WriteAllText(Path.Combine(root,"success.txt"),proof);Console.WriteLine(proof);

sealed class Peer:IAsyncDisposable{
    readonly RoomHub hub;readonly Sink socket=new();readonly Client client;readonly CancellationTokenSource stop=new();readonly Task pump;
    public Session Session{get;}public string Save{get;}public JsonElement Room=>socket.Room;
    public Peer(RoomHub hub,Session session,string save){this.hub=hub;Session=session;Save=save;client=new Client(socket);pump=client.Pump(stop.Token);hub.Attach(new Command{Type="hello",Protocol=1,Token=Session.Token,Games=OnlineRound.Games,Capabilities=new[]{"storeStakes"}},client);}
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
