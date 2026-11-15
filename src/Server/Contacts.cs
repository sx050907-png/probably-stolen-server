namespace NightStud.Server;

public sealed partial class RoomHub {
    readonly ContactStore? contacts=accounts==null?null:new(accounts);
    sealed class PhoneCall(string from,string to){public readonly string Id=Guid.NewGuid().ToString("N");public string From=from,To=to,State="ringing";public DateTime Expires=DateTime.UtcNow.AddSeconds(25);}
    readonly Dictionary<string,PhoneCall> phoneCalls=new();
    readonly Dictionary<string,DateTime> phoneCooldown=new();
    DateTime contactPrune=DateTime.UtcNow;
    void ContactAccess(Session session)=>Need(session.AccountId!=null&&contacts!=null,"account_required","登录服务器账号后可使用联络功能。");
    void ContactPeer(Session session,string peer){ContactAccess(session);Need(peer!=session.Id&&peer.Length<=64&&contacts!.Identity(peer)!=null,"contact_unavailable","暂时无法联系这位玩家。");Need(moderation?.Banned(peer)!=true&&!contacts!.Blocked(peer,session.Id)&&!contacts.Blocked(session.Id,peer),"contact_unavailable","暂时无法联系这位玩家。");}
    void SendContacts(Session session){if(session.Contacts&&session.AccountId!=null&&contacts!=null)session.Client?.Send(new{type="contacts",settings=contacts.Settings(session.Id),threads=contacts.Threads(session.Id),unread=contacts.Unread(session.Id)});}
    void SendCall(PhoneCall call,string state,string reason=""){
        foreach(string id in new[]{call.From,call.To})if(FindPlayer(id) is {Contacts:true} session){string peer=id==call.From?call.To:call.From;session.Client?.Send(new{type="phone",id=call.Id,state,reason,incoming=id==call.To,peer,name=contacts?.Identity(peer)?.Name??"玩家",expires=call.Expires});}
    }
    void EndCall(PhoneCall call,string reason){phoneCalls.Remove(call.Id);SendCall(call,"ended",reason);}
    void EndPhoneFor(string id,string reason){foreach(var call in phoneCalls.Values.Where(c=>c.From==id||c.To==id).ToArray())EndCall(call,reason);}
    void ExpireContacts(DateTime now){foreach(var call in phoneCalls.Values.Where(c=>c.Expires<=now).ToArray())EndCall(call,call.State=="ringing"?"missed":"timeout");foreach(var key in phoneCooldown.Where(p=>(now-p.Value).TotalMinutes>5).Select(p=>p.Key).ToArray())phoneCooldown.Remove(key);if(now>=contactPrune){contacts?.Prune();contactPrune=now.AddMinutes(5);}}
    void ContactCommand(Session session,Command command){
        ContactAccess(session);
        Need(command.Target!=null&&command.Target.Length<=64&&command.Call!=null&&command.Call.Length<=64&&command.Receipt!=null&&command.Receipt.Length<=64&&command.Text!=null,"bad_message","联络消息格式无效。");
        ExpireContacts(DateTime.UtcNow);string peer=command.Target;
        if(command.Type=="contacts"){SendContacts(session);return;}
        if(command.Type=="contactReceipt"){
            Need(command.Receipt.Length is >0 and <=64,"bad_message","消息编号无效。");var saved=contacts!.Existing(session.Id,command.Receipt);
            if(saved!=null)session.Client!.Send(new{type="contactMessage",message=saved});else session.Client!.Send(new{type="contactMissing",id=command.Receipt});return;
        }
        if(command.Type=="contactSettings"){
            contacts!.Settings(session.Id,new(command.AllowMessages,command.AllowCalls));if(!command.AllowCalls)EndPhoneFor(session.Id,"privacy");SendContacts(session);Audit("contact_settings",session);return;
        }
        if(command.Type=="contactBlock"){
            Need(peer!=session.Id&&peer.Length<=64&&contacts!.Identity(peer)!=null,"contact_unavailable","这位玩家暂不可用。");contacts!.Block(session.Id,peer,command.Blocked);
            if(command.Blocked)foreach(var call in phoneCalls.Values.Where(c=>c.From==session.Id&&c.To==peer||c.To==session.Id&&c.From==peer).ToArray())EndCall(call,"unavailable");SendContacts(session);return;
        }
        if(command.Type=="contactRead"){Need(peer.Length<=64&&command.Sequence>=0,"bad_message","消息记录无效。");contacts!.MarkRead(session.Id,peer,command.Sequence);SendContacts(session);return;}
        if(command.Type=="contactHistory"){
            Need(peer.Length<=64&&command.Sequence>=0&&contacts!.Identity(peer)!=null,"contact_unavailable","这位玩家暂不可用。");session.Client!.Send(new{type="contactHistory",peer,name=contacts!.Identity(peer)!.Name,blocked=contacts.Blocked(session.Id,peer),before=command.Sequence,messages=contacts.History(session.Id,peer,command.Sequence)});return;
        }
        if(command.Type=="phoneEnd"){
            if(phoneCalls.TryGetValue(command.Call,out var old)){Need(old.From==session.Id||old.To==session.Id,"call_unavailable","这通电话不属于你。");EndCall(old,old.State=="ringing"?(old.To==session.Id?"declined":"cancelled"):"hangup");}return;
        }
        if(command.Type=="phoneAccept"){
            Need(phoneCalls.TryGetValue(command.Call,out var call)&&call.To==session.Id&&call.State=="ringing","call_unavailable","来电已经结束。");ContactPeer(session,call!.From);Need(contacts!.Settings(session.Id).AllowCalls,"calls_disabled","你已开启来电免打扰。");call.State="connected";call.Expires=DateTime.UtcNow.AddHours(1);SendCall(call,"connected");return;
        }
        ContactPeer(session,peer);
        if(command.Type=="phoneStart"){
            Need(contacts!.Settings(peer).AllowCalls,"contact_unavailable","对方暂不接听来电，可改为留言。");var recipient=FindPlayer(peer);Need(recipient!=null&&Online(recipient)&&recipient.Contacts,"contact_unavailable","对方离线或联络功能不可用，可改为留言。");
            Need(!phoneCalls.Values.Any(c=>c.From==session.Id||c.To==session.Id||c.From==peer||c.To==peer),"call_busy","你或对方正在通话。");
            Need(!phoneCooldown.TryGetValue(session.Id,out var recent)||(DateTime.UtcNow-recent).TotalSeconds>=30,"call_rate","请稍等片刻再拨号。");phoneCooldown[session.Id]=DateTime.UtcNow;
            var call=new PhoneCall(session.Id,peer);phoneCalls.Add(call.Id,call);SendCall(call,"ringing");Audit("phone_started",session);return;
        }
        Need(command.Type=="contactSend","unknown","未知联络操作。");Need(contacts!.Settings(peer).AllowMessages,"contact_unavailable","对方暂不接收私信和留言。");
        string kind="message";if(command.Call.Length>0){Need(phoneCalls.TryGetValue(command.Call,out var call)&&call.State=="connected"&&(call.From==session.Id&&call.To==peer||call.To==session.Id&&call.From==peer),"call_unavailable","通话已经结束，请改用私信。");kind="call";}
        var existing=contacts.Existing(session.Id,command.Id);ContactMessage message;
        if(existing!=null){Need(existing.Recipient==peer&&existing.Text==command.Text&&existing.Kind==kind,"bad_message","消息编号已使用。");message=existing;}
        else{string text=moderation?.Check(session.Id,command.Text)??Moderation.DefaultContent(command.Text);message=contacts.Save(command.Id,session.Id,peer,text,kind);Audit("contact_message",session);}
        session.Client!.Send(new{type="contactMessage",message});if(FindPlayer(peer) is {Contacts:true} other&&Online(other)){other.Client!.Send(new{type="contactMessage",message});SendContacts(other);}SendContacts(session);
    }
}
