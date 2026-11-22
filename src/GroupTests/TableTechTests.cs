using System.Text.Json;
using NightStud.Server;

internal static class TableTechTests
{
    static readonly string[] Capabilities={"tableTech","storeStakes"};

    public static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        var passed=new List<string>();
        void Check(bool value,string name)
        {
            if(!value)throw new Exception(name);
            passed.Add(name);
        }
        static JsonElement Tech(Peer player)=>player.Room.GetProperty("tech");
        static int Count(Peer player,string name)=>Tech(player).GetProperty(name).GetInt32();
        static bool Flag(Peer player,string name)=>Tech(player).GetProperty(name).GetBoolean();
        static int[] Cards(Peer player)=>Tech(player).GetProperty("readCards").EnumerateArray().Select(c=>c.GetInt32()).ToArray();
        static int[] OwnCards(Peer player)=>player.Room.GetProperty("players")[player.Room.GetProperty("ownSeat").GetInt32()].GetProperty("cards").EnumerateArray().Select(c=>c.GetInt32()).ToArray();
        static long Revision(Peer player)=>player.Room.GetProperty("revision").GetInt64();
        static int Total(Peer player)=>player.Room.GetProperty("pot").GetInt32();
        static string Last(Peer player)=>Tech(player).GetProperty("last").GetString()!;
        async Task Reject(Peer player,string type,Action<Command> set,string name,params string[] codes)
        {
            var ack=await player.Command(type,set);
            if(ack.GetProperty("ok").GetBoolean())throw new Exception(name+": unexpectedly accepted");
            string error=ack.GetProperty("error").GetString()!;
            Check(codes.Length==0||codes.Contains(error),name+" ("+error+")");
        }
        static void Save(string path,Peer player)=>File.WriteAllText(path,Wire.Encode(new{type="state",room=player.Room}));
        var hub=new RoomHub(10);
        var legacy=hub.Login("旧版牌友");
        await using var old=new Peer(hub,legacy);
        foreach(string game in new[]{"stud","three","holdem"}){
            string suffix=" / "+game;
            await using var host=new Peer(hub,hub.Login("练习室主"),true,Capabilities);
            await using var visitor=new Peer(hub,hub.Login("练习桌友"),true,Capabilities);
            await host.Ok("create",c=>{c.Game=game;c.Public=true;});
            string code=host.Room.GetProperty("code").GetString()!;
            Check(!host.Room.GetProperty("techEnabled").GetBoolean(),"default off"+suffix);
            await old.Ok("join",c=>c.Code=code);
            await Reject(host,"techRules",c=>c.Enabled=true,"legacy seat blocks opt-in"+suffix,"tech_client");
            await old.Ok("leave");
            await host.Ok("techRules",c=>c.Enabled=true);
            await old.Flush();
            var listing=old.Lobby.GetProperty("rooms").EnumerateArray().Single(x=>x.GetProperty("code").GetString()==code);
            Check(listing.GetProperty("techEnabled").GetBoolean(),"directory declares table rules"+suffix);
            Check(!listing.TryGetProperty("tech",out _),"public directory has no kit or result"+suffix);
            await Reject(old,"join",c=>c.Code=code,"legacy join blocked"+suffix,"tech_client","client_update_required");
            Check(!listing.GetProperty("canJoin").GetBoolean(),"legacy directory marks enabled room unavailable"+suffix);
            await Reject(host,"invite",c=>c.Target=old.Session.Id,"legacy invite blocked"+suffix,"tech_client");
            await host.Ok("invite",c=>c.Target=visitor.Session.Id);
            await visitor.Flush();
            var invite=visitor.Lobby.GetProperty("invitations").EnumerateArray().Single(x=>x.GetProperty("code").GetString()==code);
            Check(invite.GetProperty("techEnabled").GetBoolean(),"invite discloses tech mode"+suffix);
            await visitor.Ok("acceptInvite",c=>c.Invitation=invite.GetProperty("id").GetString()!);
            await Reject(visitor,"techRules",c=>c.Enabled=false,"guest cannot change rules"+suffix,"host_only");
            await Reject(visitor,"ready",c=>{},"ready without consent rejected"+suffix,"tech_consent");
            await Reject(visitor,"ready",c=>c.Enabled=false,"explicit refusal cannot ready up"+suffix,"tech_consent");
            Check(!visitor.Room.GetProperty("players")[1].GetProperty("ready").GetBoolean(),"failed consent preserves readiness"+suffix);
            await visitor.Ok("ready",c=>c.Enabled=true);
            await host.Ok("techRules",c=>c.Enabled=false);
            await visitor.Flush();
            Check(!visitor.Room.GetProperty("players")[1].GetProperty("ready").GetBoolean(),"changing rules revokes consent"+suffix);
            await host.Ok("techRules",c=>c.Enabled=true);
            Save(Path.Combine(output,game+"-waiting.json"),host);
            await host.Ok("ready",c=>c.Enabled=true);await visitor.Ok("ready",c=>c.Enabled=true);await host.Flush();
            var first=host.Room.GetProperty("actor").GetInt32()==0?host:visitor;
            var second=first==host?visitor:host;
            Save(Path.Combine(output,game+"-playing.json"),first);
            Check(Count(first,"lenses")==2 && Count(first,"shields")==1 && !Flag(first,"armed") && Flag(first,"canUse"),"starting kit"+suffix);
            Check(!Flag(second,"canUse"),"non-actor controls disabled"+suffix);
            await Reject(second,"useTech",c=>c.Item="phaseLens","off turn"+suffix,"out_of_turn");
            await Reject(first,"useTech",c=>c.Item="realItem","unavailable device"+suffix,"tech_item");
            await Reject(first,"useTech",c=>{c.Item="phaseLens";c.Revision=Revision(first)-1;},"stale action"+suffix,"stale");
            int pot=Total(first),otherStateCount=second.StateCount;
            long revision=Revision(first);
            string legal=first.Room.GetProperty("legalMoves").GetRawText(),events=first.Room.GetProperty("events").GetRawText();
            string armId=Guid.NewGuid().ToString("N");
            await first.Ok("useTech",c=>{c.Item="signalThimble";c.Id=armId;});
            await second.Flush();
            Check(Flag(first,"armed") && Count(first,"shields")==0 && Last(first)=="armed","arm consumed once"+suffix);
            Check(second.StateCount==otherStateCount && Revision(first)==revision,"arm private without timing broadcast"+suffix);
            Check(pot==Total(first) && legal==first.Room.GetProperty("legalMoves").GetRawText() && events==first.Room.GetProperty("events").GetRawText(),"using gear preserves normal public turn"+suffix);
            await Reject(first,"useTech",c=>{c.Item="signalThimble";c.Id=armId;},"duplicate id"+suffix,"duplicate");
            await Reject(first,"useTech",c=>c.Item="phaseLens","double-click with new id"+suffix,"tech_turn_used");
            await first.Ok("act",c=>c.Move="bet");
            await second.Ok("useTech",c=>c.Item="phaseLens");
            Check(Count(second,"lenses")==1 && Cards(second).Length==0 && Last(second)=="blocked","shield blocks without leaking card"+suffix);
            Save(Path.Combine(output,game+"-blocked.json"),second);
            await second.Ok("act",c=>c.Move="raise");
            await first.Flush();
            Check(!Flag(first,"armed"),"shield consumed by one scan"+suffix);
            int expected=OwnCards(second)[0];
            await first.Ok("useTech",c=>c.Item="phaseLens");
            Check(Cards(first).SequenceEqual(new[]{expected}) && Last(first)=="read","reader sees correct hole card"+suffix);
            Check(Cards(second).Length==0 && Last(second)=="blocked","other viewer retains only own receipt"+suffix);
            Save(Path.Combine(output,game+"-read.json"),first);
            string before=Tech(first).GetRawText();
            var legacyConnection=new Client(new Sink());
            try{
                hub.Attach(new Command{Type="hello",Protocol=1,Token=first.Session.Token,Games=OnlineRound.Games},legacyConnection);
                throw new Exception("legacy client replaced active player"+suffix);
            }catch(WireFault e){Check(e.Code=="tech_client","legacy reconnect refused"+suffix);}
            finally{legacyConnection.Stop();}
            await first.Flush();
            Check(Tech(first).GetRawText()==before,"failed capability downgrade preserves original connection"+suffix);
            await Reject(host,"techRules",c=>c.Enabled=false,"cannot switch rules mid-hand"+suffix,"playing");
            first.Drop();
            await second.Flush();
            Check(second.Room.GetProperty("phase").GetString()=="paused","disconnect pauses round"+suffix);
            await Reject(second,"useTech",c=>c.Item="phaseLens","paused cannot consume"+suffix,"peer_offline","out_of_turn");
            await using var resumed=new Peer(hub,first.Session,true,Capabilities);
            await resumed.Flush();
            Check(Tech(resumed).GetRawText()==before,"reconnect preserves counters, latch and read history"+suffix);
            await Reject(resumed,"useTech",c=>c.Item="phaseLens","reconnect cannot repeat turn"+suffix,"tech_turn_used");
            await resumed.Ok("act",c=>c.Move="raise");
            await second.Ok("useTech",c=>c.Item="phaseLens");
            Check(Cards(second).SequenceEqual(new[]{OwnCards(resumed)[0]}) && Count(second,"lenses")==0,"shield blocks exactly one read"+suffix);
            await second.Ok("act",c=>c.Move="raise");
            if(game=="stud"){
                await Reject(resumed,"useTech",c=>c.Item="phaseLens","same stud hole cannot be read twice"+suffix,"tech_no_card");
                Check(Count(resumed,"lenses")==1,"invalid repeat scan free"+suffix);
            }else{
                await resumed.Ok("useTech",c=>c.Item="phaseLens");
                Check(Cards(resumed).SequenceEqual(OwnCards(second).Take(2)),"second scan reads distinct next hole"+suffix);
            }
            await resumed.Ok("act",c=>c.Move="raise");
            await Reject(second,"useTech",c=>c.Item="phaseLens","spent lenses cannot be reused"+suffix,"tech_empty");
            await second.Ok("act",c=>c.Move="fold");
            await resumed.Flush();
            Check(resumed.Room.GetProperty("players").EnumerateArray().Sum(p=>p.GetProperty("stack").GetInt32())==2000,"settlement conserves room bankroll"+suffix);
            await Reject(resumed,"useTech",c=>c.Item="signalThimble","settled hands reject device use"+suffix,"not_playing");
            await resumed.Ok("ready",c=>c.Enabled=true);await second.Ok("ready",c=>c.Enabled=true);await resumed.Flush();
            Check(Cards(resumed).Length==0 && Count(resumed,"lenses")==2 && Count(resumed,"shields")==1 && Last(resumed)=="","new hand clears private knowledge and refills kit"+suffix);
            await resumed.Ok("leave");await second.Ok("leave");
        }
        await using var plainHost=new Peer(hub,hub.Login("普通练习室"),true,Capabilities);
        await plainHost.Ok("create",c=>c.Game="stud");
        await old.Ok("join",c=>c.Code=plainHost.Room.GetProperty("code").GetString()!);
        await plainHost.Ok("ready");await old.Ok("ready");
        await Reject(plainHost,"useTech",c=>c.Item="phaseLens","ordinary practice stays gear-free","tech_room");
        await plainHost.Ok("leave");await old.Ok("leave");
        foreach(string mode in new[]{"blackjack","dice","mahjong","landlord"}){
            await plainHost.Ok("create",c=>c.Game=mode);
            await Reject(plainHost,"techRules",c=>c.Enabled=true,"unsupported mode stays gear-free "+mode,"tech_room");
            await plainHost.Ok("leave");
        }
        var accounts=new Accounts(Path.Combine(Path.GetTempPath(),"nightstud-tech-tests-"+Guid.NewGuid().ToString("N")));
        var cashHub=new RoomHub(10,accounts:accounts);
        var account=await accounts.Register("feature_test","fixture-password","信用点房");
        await using var cashier=new Peer(cashHub,cashHub.LoginAccount(account),true,Capabilities);
        await cashier.Ok("create",c=>{c.Game="stud";c.Economy="store";});
        await Reject(cashier,"techRules",c=>c.Enabled=true,"store stake room rejects loaner rule","tech_room");
        await cashier.Ok("leave");
        File.WriteAllLines(Path.Combine(output,"checks.txt"),passed);
        string proof=$"PASS {passed.Count} room protocol checks across Stud, Three, Holdem, ordinary, legacy, unsupported and store rooms.";
        File.WriteAllText(Path.Combine(output,"success.txt"),proof);
        Console.WriteLine(proof);
    }
}
