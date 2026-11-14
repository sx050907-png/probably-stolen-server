namespace NightStud.Server;

public sealed partial class RoomHub
{
    void SetBot(Room room,Session caller,bool add){
        Need(room.Economy=="practice","cash_bot","信用点房间由真人入座；练习房间可以补人机。");
        Need(room.Mode is "landlord" or "mahjong","bad_game","斗地主和麻将可以添加人机。");
        Need(room.HostId==caller.Id,"host_only","由房主调整人机座位。");
        Need(room.Game is null||room.Game.Done,"playing","本局结束后再调整座位。");
        int index=Array.FindLastIndex(room.Seats,s=>add?s==null:s?.Session.Provider=="bot");
        Need(index>=0,"no_seat",add?"没有空座位了。":"这桌没有人机。");
        room.Seats[index]=add?new Seat(new Session("陪桌人机 "+(index+1),provider:"bot"),room.BuyIn){Ready=true}:null;
        room.Game=null;room.Result=null;Array.Clear(room.Committed);
        foreach(var seat in room.Seats.Where(s=>s!=null))seat!.Ready=seat.Session.Provider=="bot";
        room.Revision++;room.Touched=DateTime.UtcNow;Audit(add?"bot_added":"bot_removed",caller,room);
    }
    void TickBot(Room room,DateTime now){
        if(room.Game is not {Done:false} game||now<room.NextBot||room.Seats.Any(s=>s==null||!Online(s.Session)))return;
        if(game.Actor<0||room.Seats[game.Actor] is not {} seat||seat.Session.Provider!="bot")return;
        Act(room,seat.Session,game.Suggest());Broadcast(room);
        if(game.Done)BroadcastLobby();
    }
}
