namespace NightStud.Server;

public sealed partial class RoomHub {
    void TablePhrase(Session session,Command command){
        var room=Current(session);Need(room!=null&&NightStud.Shared.TableVoiceRules.Keys(room.Mode).Count>0&&room.Game is {Done:false},"not_playing","开始牌局后可使用牌桌语音。");
        int own=OwnSeat(room,session);bool manual=command.Move?.StartsWith("say:",StringComparison.Ordinal)==true;
        Need(room.Code==command.Code&&room.Revision==command.Revision&&own>=0&&(manual||room.Game.Actor==own),"stale","牌桌已变化，这次语音已取消。");
        string phrase=manual?command.Move![4..]:command.Move??"";
        Need(manual?NightStud.Shared.TableVoiceRules.Allowed(room.Mode,phrase):room.Mode=="landlord"&&phrase=="wait","bad_phrase","请选择当前玩法的牌桌语音。");
        var now=DateTime.UtcNow;Need((manual||(now-room.Touched).TotalSeconds>=20)&&(now-room.PhraseAt).TotalSeconds>=(manual?NightStud.Shared.TableVoiceRules.ManualCooldownSeconds:35),"phrase_rate","同桌语音需要稍等片刻。");room.PhraseAt=now;
        var value=new{type="tablePhrase",id=Guid.NewGuid().ToString("N"),code=room.Code,hand=room.Hands,phrase,speaker=manual?own:(room.Game.Actor+1)%3,manual};
        foreach(var seat in room.Seats.Where(s=>s!=null&&Online(s.Session)))seat!.Session.Client?.Send(value);Audit("table_phrase",session,room,phrase);
    }
}
