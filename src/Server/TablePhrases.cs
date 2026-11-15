namespace NightStud.Server;

public sealed partial class RoomHub {
    void TablePhrase(Session session,Command command){
        var room=Current(session);Need(room!=null&&room.Mode=="landlord"&&room.Game is {Done:false},"not_playing","开始斗地主后可使用牌桌语音。");
        int own=OwnSeat(room,session);bool manual=command.Move?.StartsWith("say:",StringComparison.Ordinal)==true;
        Need(room.Code==command.Code&&room.Revision==command.Revision&&own>=0&&(manual||room.Game.Actor==own),"stale","牌桌已变化，这次语音已取消。");
        string phrase=manual?command.Move![4..]:command.Move??"";
        Need(manual?new[]{"wait","coffee","nice","partner","show","seventeen","ready"}.Contains(phrase):phrase=="wait","bad_phrase","请选择现有的牌桌语音。");
        var now=DateTime.UtcNow;Need((manual||(now-room.Touched).TotalSeconds>=20)&&(now-room.PhraseAt).TotalSeconds>=(manual?8:35),"phrase_rate","同桌语音需要稍等片刻。");room.PhraseAt=now;
        var value=new{type="tablePhrase",id=Guid.NewGuid().ToString("N"),code=room.Code,hand=room.Hands,phrase,speaker=manual?own:(room.Game.Actor+1)%3,manual};
        foreach(var seat in room.Seats.Where(s=>s!=null&&Online(s.Session)))seat!.Session.Client?.Send(value);Audit("table_phrase",session,room,phrase);
    }
}
