namespace NightStud.Server;

// Each hand owns its loaners. The player's store inventory never backs these actions.
internal sealed class TableTechHand
{
    sealed class Kit
    {
        internal int Lenses=2, Shields=1;
        internal bool Armed, UsedThisTurn;
        internal string Last="";
        internal List<int> ReadCards=new();
    }

    readonly Kit[] kits={new(),new()};

    static int HiddenCount(OnlineRound game,int viewer)=>
        game.Mode=="stud"?1:game.Hand[1-viewer].Count;

    internal void EndTurn(int seat)=>kits[seat].UsedThisTurn=false;

    internal void Use(OnlineRound game,int seat,string item)
    {
        var kit=kits[seat];
        if(kit.UsedThisTurn)throw new WireFault("tech_turn_used","这回合已经用过装置了，先出牌。");
        switch(item){
            case "phaseLens":
                if(kit.Lenses==0)throw new WireFault("tech_empty","这手的虹膜片用完了。");
                if(kit.ReadCards.Count>=HiddenCount(game,seat))
                    throw new WireFault("tech_no_card","对手的底牌你已经看过了。");
                var other=kits[1-seat];
                kit.Lenses--;
                if(other.Armed){
                    other.Armed=false;
                    kit.Last="blocked";
                }else{
                    kit.ReadCards.Add(game.Hand[1-seat][kit.ReadCards.Count]);
                    kit.Last="read";
                }
                break;
            case "signalThimble":
                if(kit.Shields==0)throw new WireFault("tech_empty","这手的顶针已经用过了。");
                kit.Shields--;
                kit.Armed=true;
                kit.Last="armed";
                break;
            default:throw new WireFault("tech_item","这桌只提供虹膜片和补衣顶针。");
        }
        kit.UsedThisTurn=true;
    }

    internal object View(OnlineRound game,int seat,bool paused)
    {
        var kit=kits[seat];
        bool canUse=!paused && !game.Done && game.Actor==seat && !kit.UsedThisTurn;
        return new{lenses=kit.Lenses,shields=kit.Shields,armed=kit.Armed,canUse,
            canScan=canUse && kit.Lenses>0 && kit.ReadCards.Count<HiddenCount(game,seat),
            readCards=kit.ReadCards.ToArray(),last=kit.Last};
    }
}

public sealed partial class RoomHub
{
    static bool CanPlayTech(Session session,Room room)=>session.TableTech && session.Games.Contains(room.Mode);
    static bool SupportsTech(Room room)=>room.Economy=="practice" && room.Mode is "stud" or "three" or "holdem";

    void SetTechRules(Room room,Session host,bool? enabled)
    {
        Need(host.Id==room.HostId,"host_only","房主可以调整牌桌规则。");
        Need(SupportsTech(room),"tech_room","科技桌只用于梭哈、炸金花或德州的练习房。");
        Need(room.Game==null || room.Game.Done,"playing","打完这手再换规则。");
        Need(room.Seats.All(seat=>seat==null || CanPlayTech(seat.Session,room)),"tech_client","有桌友的客户端还不支持科技桌。");
        Need(enabled.HasValue,"tech_rules","请选择是否启用科技桌。");
        if(room.TechEnabled==enabled)return;
        room.TechEnabled=enabled==true;
        if(room.Game!=null)room.Game.Kit=null;
        foreach(var seat in room.Seats)if(seat!=null)seat.Ready=false;
        room.Revision++;
        room.Touched=DateTime.UtcNow;
    }

    void UseTableTech(Room room,Session player,string item)
    {
        Need(room.TechEnabled && SupportsTech(room),"tech_room","这桌没有启用科技。");
        Need(player.TableTech,"tech_client","请先更新客户端。");
        var game=room.Game;
        Need(game is {Done:false,Kit:not null},"not_playing","双方准备发牌后才能用装置。");
        Need(room.Seats.All(seat=>seat!=null && Online(seat.Session)),"peer_offline","等桌友重新连上再出手。");
        int seat=OwnSeat(room,player);
        Need(game!.Actor==seat,"out_of_turn","轮到你时才能用装置。");
        game.Kit!.Use(game,seat,item);
        // Publishing a room revision or a table event here would give away the action.
        room.Touched=DateTime.UtcNow;
    }
}
