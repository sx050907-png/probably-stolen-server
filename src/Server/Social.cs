using System.Text.Json;
using System.Text.RegularExpressions;

namespace NightStud.Server;

public sealed partial class RoomHub
{
    sealed class Shop(string name){public string Name=name,Image="";public DateTime Updated=DateTime.UtcNow;}
    readonly Dictionary<string,Shop> shops=new();
    readonly Dictionary<string,string> visiting=new();
    readonly Dictionary<string,Queue<object>> chats=new();
    readonly Dictionary<string,DateTime> shopRate=new();
    DateTime mediaDay=DateTime.UtcNow.Date;long mediaBytes;
    void SetPrivacy(Session session,Command command){
        Need(session.AccountId!=null&&accounts!=null,"account_required","登录账号后可设置隐私。");
        session.Privacy=new(command.AllowVisits,command.AllowInvites);accounts!.SetPrivacy(session.AccountId!,session.Privacy);
        if(!session.Privacy.AllowVisits){shops.Remove(session.Id);DropSharedShop(session.Id);foreach(string visitor in visiting.Where(p=>p.Value==session.Id).Select(p=>p.Key).ToArray())if(FindPlayer(visitor) is {} guest)LeaveVisit(guest);}
        if(!session.Privacy.AllowInvites)foreach(var invite in invitations.Values.Where(i=>i.To==session.Id).ToArray())invitations.Remove(invite.Id);
        Audit("privacy_changed",session);
    }
    Session? FindPlayer(string id)=>sessions.Values.FirstOrDefault(s=>s.Id==id);
    string SocialRoom(Session session)=>visiting.TryGetValue(session.Id,out string? host)?"visit:"+host:
        visiting.Values.Contains(session.Id)?"visit:"+session.Id:session.RoomCode!=null?"table:"+session.RoomCode:"lobby";
    void Chat(Session sender,string text,string expectedChannel=""){
        Need(sender.AccountId!=null,"account_required","注册或 Steam 登录后可发言。");
        string message=moderation?.Check(sender.Id,text)??(text.Length is >0 and <=120?text:throw new WireFault("chat_length","消息请保持在 1–120 字内。"));
        string channel=SocialRoom(sender);Need(expectedChannel.Length==0||expectedChannel==channel,"chat_scope","已经离开刚才的聊天场合，请重新打开聊天。");
        var entry=new{ id=Guid.NewGuid().ToString("N"),playerId=sender.Id,name=sender.Name,provider=sender.Provider,text=message,time=DateTimeOffset.UtcNow};
        if(!chats.TryGetValue(channel,out var history)){if(chats.Count>=256)chats.Remove(chats.Keys.First());chats[channel]=history=new();}history.Enqueue(entry);while(history.Count>32)history.Dequeue();
        foreach(var recipient in sessions.Values.Where(s=>Online(s)&&SocialRoom(s)==channel))recipient.Client!.Send(new{type="chat",channel,entry});
        Audit("chat_sent",sender,null,channel.StartsWith("visit:")?"visit":channel.StartsWith("table:")?"table":"lobby");
    }
    void Visit(Session visitor,string target){
        Need(visitor.AccountId!=null,"account_required","登录账号后可串门。");
        Need(visitor.RoomCode==null,"in_room","先离开当前牌局再串门。");
        var owner=FindPlayer(target);Need(owner!=null&&Online(owner)&&owner.Id!=visitor.Id,"visit_unavailable","店主当前不在线。");
        Need(owner!.Privacy.AllowVisits&&shops.ContainsKey(owner.Id),"visits_disabled","店主暂未开放串门。");
        Need(owner.RoomCode==null&&!visiting.ContainsKey(owner.Id),"visit_unavailable","店主暂时不在店里。");
        Need(!sharedShops.ContainsKey(owner.Id)||visitor.SharedShop,"client_update","店主使用新版串门，请更新模组后再来。");
        Need(visiting.Values.Count(id=>id==target)<4,"visit_full","这家店的访客已满。");
        EndShopPresence(visitor);visiting[visitor.Id]=owner.Id;var shop=shops[owner.Id];
        if(sharedShops.ContainsKey(owner.Id))PlaceNewVisitor(visitor.Id,owner.Id);
        visitor.Client?.Send(new{type="visit",host=new{id=owner.Id,name=owner.Name,shop=shop.Name,provider=owner.Provider},image=shop.Image});
        owner.Client?.Send(new{type="visitors",players=visiting.Where(p=>p.Value==owner.Id).Select(p=>FindPlayer(p.Key)).Where(s=>s!=null).Select(s=>new{id=s!.Id,name=s.Name,provider=s.Provider}).ToArray()});
        BroadcastSharedShop(owner.Id);
        Audit("visit_started",visitor,null,owner.Id);
    }
    void LeaveVisit(Session visitor){
        if(!visiting.TryGetValue(visitor.Id,out var host))return;
        CancelVisitTrades(visitor.Id,host);
        visiting.Remove(visitor.Id);
        ClearVisitorActions(visitor.Id);
        visitor.Client?.Send(new{type="visit",host=(object?)null});
        FindPlayer(host)?.Client?.Send(new{type="visitors",players=visiting.Where(p=>p.Value==host).Select(p=>FindPlayer(p.Key)).Where(s=>s!=null).Select(s=>new{id=s!.Id,name=s.Name,provider=s.Provider}).ToArray()});
        BroadcastSharedShop(host);
        Audit("visit_ended",visitor);
    }
    void PublishShop(Session owner,Command command){
        Need(owner.AccountId!=null&&owner.Privacy.AllowVisits,"visits_disabled","开启串门后才能发布店铺。");
        Need(owner.RoomCode==null&&!visiting.ContainsKey(owner.Id),"visits_disabled","回到自己的店里后可接待访客。");
        Need(!owner.SharedShop&&!sharedShops.ContainsKey(owner.Id),"client_update","新版串门请发布共享店铺场景。");
        var now=DateTime.UtcNow;Need(!shopRate.TryGetValue(owner.Id,out var previous)||(now-previous).TotalSeconds>=4,"shop_rate","店铺更新过快。");shopRate[owner.Id]=now;
        Need(command.Name.Length is >0 and <=32&&!command.Name.Any(char.IsControl),"bad_shop","店铺名称无效。");
        moderation?.Content(command.Name);
        if(command.Payload.Length>0){
            byte[] image;try{image=Convert.FromBase64String(command.Payload);}catch(FormatException){throw new WireFault("bad_image","店铺画面无效。");}
            Need(ValidJpeg(image),"bad_image","店铺画面须为 960×540 以内的小幅 JPEG。");
            if(mediaDay!=now.Date){mediaDay=now.Date;mediaBytes=0;}
            long budget=(long)(int.TryParse(Environment.GetEnvironmentVariable("NIGHTSTUD_MEDIA_DAILY_MB"),out int configured)?Math.Clamp(configured,16,4096):512)*1024*1024;
            long charge=(long)command.Payload.Length*visiting.Values.Count(id=>id==owner.Id);
            Need(mediaBytes+charge<=budget,"media_budget","今日店铺画面流量已用完，聊天和交易可继续使用。");mediaBytes+=charge;
        }
        bool first=!shops.ContainsKey(owner.Id);string snapshot=command.Payload.Length>0?command.Payload:shops.TryGetValue(owner.Id,out var old)?old.Image:"";
        shops[owner.Id]=new Shop(command.Name){Image=snapshot,Updated=now};if(first)BroadcastLobby();
        if(command.Payload.Length==0)return;
        foreach(var guest in visiting.Where(p=>p.Value==owner.Id).Select(p=>FindPlayer(p.Key)).Where(s=>s!=null&&Online(s)))
            guest!.Client!.Send(new{type="shopFrame",host=owner.Id,image=command.Payload});
    }
    public static bool ValidJpeg(byte[] bytes)=>NightStud.Shared.PreviewImage.Valid(bytes);
    static bool Related(string a,string b,Dictionary<string,string> visits)=>visits.TryGetValue(a,out var host)&&host==b||visits.TryGetValue(b,out host)&&host==a;
    void OfferTrade(Session seller,Command command){
        Need(accounts!=null&&seller.AccountId!=null,"account_required","登录账号后可交易。");
        Need(Regex.IsMatch(command.Trade,"^[a-f0-9]{32}$")&&command.Price is >0 and <=1000000,"bad_trade","交易金额或编号无效。");
        // Recover a saved escrow by its existing receipt even after the visit ended.
        // Do this before checking a new offer's online recipient and pending quota.
        using(var existing=accounts!.Open())using(var lookup=existing.CreateCommand()){
            lookup.CommandText="SELECT seller,buyer,price,state FROM trades WHERE id=$i";lookup.Parameters.AddWithValue("$i",command.Trade);
            bool found,cancelled=false;using(var row=lookup.ExecuteReader()){
                found=row.Read();if(found){cancelled=row.GetString(3)=="cancelled";if(!cancelled)Need(row.GetString(0)==seller.Id&&row.GetString(1)==command.Target&&row.GetInt32(2)==command.Price,"bad_trade","交易编号已使用。");}
            }
            if(found){SendTrade(seller,command.Trade);if(!cancelled&&FindPlayer(command.Target) is {} recipient)SendTrade(recipient,command.Trade);return;}
        }
        ResolveCounterPayload(seller,command);CheckShopQuote(seller,command);
        var buyer=FindPlayer(command.Target);Need(buyer?.AccountId!=null&&Online(buyer)&&Related(seller.Id,buyer.Id,visiting),"trade_unavailable","买卖双方需在同一家店内。");
        var parcel=ShopTradePayload.Parse(command.Payload);
        Need(!parcel.Batch||seller.TradeBatches&&buyer!.TradeBatches,"client_update","双方更新模组后可一起交易多件货物。");
        Need(parcel.Layout==null||command.CounterLots!=null,"bad_counter","柜台摆位需要重新核对。");
        CheckCounterOffer(seller,command);
        foreach(string label in parcel.Labels)moderation?.Content(label);
        using var db=accounts!.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM trades WHERE state='proposed' AND seller=$s";cmd.Parameters.AddWithValue("$s",seller.Id);
        using(var expired=db.CreateCommand()){expired.CommandText="UPDATE trades SET state='cancelled',payload='' WHERE state='proposed' AND created<$t";expired.Parameters.AddWithValue("$t",DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O"));expired.ExecuteNonQuery();}
        Need(Convert.ToInt64(cmd.ExecuteScalar())<8,"trade_limit","请先完成或撤回现有报价。");
        cmd.CommandText="INSERT OR IGNORE INTO trades(id,seller,buyer,payload,price,state,created) VALUES($i,$s,$b,$p,$v,'proposed',$c)";
        cmd.Parameters.AddWithValue("$i",command.Trade);cmd.Parameters.AddWithValue("$b",buyer!.Id);cmd.Parameters.AddWithValue("$p",command.Payload);cmd.Parameters.AddWithValue("$v",command.Price);cmd.Parameters.AddWithValue("$c",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
        if(command.Inquiry.Length>0)EndShopInquiry(command.Inquiry,"quoted","店主已报价。");
        ClearCounterAfterOffer(seller);SendTrade(seller,command.Trade);SendTrade(buyer,command.Trade);Audit("trade_offered",seller);
    }
    public static bool ValidLot(string payload){
        if(payload.Length is <2 or >60000)return false;
        try{
            using var doc=JsonDocument.Parse(payload,new JsonDocumentOptions{MaxDepth=16});var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("nodes",out var nodes)||nodes.ValueKind!=JsonValueKind.Array||nodes.GetArrayLength() is <1 or >64)return false;
            foreach(var node in nodes.EnumerateArray()){
                if(!node.TryGetProperty("identifier",out var id)||!Regex.IsMatch(id.GetString()??"","^[A-Za-z0-9_]{1,96}$"))return false;
                if(!node.TryGetProperty("unitCount",out var count)||!count.TryGetInt32(out int n)||n is <1 or >1000)return false;
            }
            return !payload.Contains("$type",StringComparison.OrdinalIgnoreCase);
        }catch(JsonException){return false;}catch(InvalidOperationException){return false;}
    }
    void AcceptTrade(Session buyer,Command command){
        Need(accounts!=null&&buyer.AccountId!=null,"account_required","登录账号后可交易。");
        using var db=accounts!.Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT seller,buyer,state,created FROM trades WHERE id=$i";cmd.Parameters.AddWithValue("$i",command.Trade);
        string seller,state;DateTimeOffset created;
        using(var row=cmd.ExecuteReader()){Need(row.Read()&&row.GetString(1)==buyer.Id,"trade_unavailable","这份报价不属于你。");seller=row.GetString(0);state=row.GetString(2);created=DateTimeOffset.Parse(row.GetString(3));}
        Need(state=="committed"||state=="proposed"&&created>DateTimeOffset.UtcNow.AddMinutes(-2)&&Related(seller,buyer.Id,visiting),"trade_expired","报价已过期或交易双方已离店。");
        if(state=="proposed")CheckTradeReception(seller,buyer.Id);
        // Each client has already saved its own item escrow before acceptance.
        // Keep the authoritative receipt, remove the large transferred item document.
        cmd.CommandText="UPDATE trades SET state='committed',payload='' WHERE id=$i AND state='proposed'";cmd.ExecuteNonQuery();tx.Commit();
        SendTrade(buyer,command.Trade);if(FindPlayer(seller) is {} owner)SendTrade(owner,command.Trade);Audit("trade_committed",buyer);
    }
    void CancelTrade(Session session,Command command){
        Need(accounts!=null&&session.AccountId!=null,"account_required","登录账号后可交易。");using var db=accounts!.Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT seller,buyer FROM trades WHERE id=$i";cmd.Parameters.AddWithValue("$i",command.Trade);
        string seller="",buyer="";using(var row=cmd.ExecuteReader()){if(row.Read()){seller=row.GetString(0);buyer=row.GetString(1);}}
        cmd.CommandText="UPDATE trades SET state='cancelled',payload='' WHERE id=$i AND (seller=$s OR buyer=$s) AND state='proposed'";cmd.Parameters.AddWithValue("$s",session.Id);int changed=cmd.ExecuteNonQuery();SendTrade(session,command.Trade);
        if(changed>0){string other=seller==session.Id?buyer:seller;if(other!=session.Id&&FindPlayer(other) is {} peer)SendTrade(peer,command.Trade);}
    }
    void CancelVisitTrades(string visitor,string host){
        if(accounts==null)return;
        using var db=accounts.Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT id FROM trades WHERE state='proposed' AND ((seller=$v AND buyer=$h) OR (seller=$h AND buyer=$v))";cmd.Parameters.AddWithValue("$v",visitor);cmd.Parameters.AddWithValue("$h",host);
        var ids=new List<string>();using(var row=cmd.ExecuteReader()){while(row.Read())ids.Add(row.GetString(0));}
        cmd.CommandText="UPDATE trades SET state='cancelled',payload='' WHERE state='proposed' AND ((seller=$v AND buyer=$h) OR (seller=$h AND buyer=$v))";cmd.ExecuteNonQuery();tx.Commit();
        foreach(string id in ids)foreach(string player in new[]{visitor,host}.Distinct())if(FindPlayer(player) is {} s)SendTrade(s,id);
    }
    void TradeReceipt(Session session,Command command){Need(accounts!=null&&session.AccountId!=null,"account_required","登录账号后可查询交易。");SendTrade(session,command.Trade);}
    void SendTrade(Session session,string id){
        if(accounts==null)return;Need(Regex.IsMatch(id,"^[a-f0-9]{32}$"),"bad_trade","交易编号无效。");using var db=accounts.Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT seller,buyer,payload,price,state,created FROM trades WHERE id=$i";cmd.Parameters.AddWithValue("$i",id);
        string seller="",buyer="",payload="",state="";int price=0;bool found;
        using(var row=cmd.ExecuteReader()){
            found=row.Read();if(found){seller=row.GetString(0);buyer=row.GetString(1);payload=row.GetString(2);price=row.GetInt32(3);state=row.GetString(4);
                if(state!="cancelled")Need(seller==session.Id||buyer==session.Id,"trade_unavailable","这份交易不属于你。");
                if(state=="proposed"&&DateTimeOffset.Parse(row.GetString(5))<DateTimeOffset.UtcNow.AddMinutes(-2))state="cancelled";
            }
        }
        if(!found){
            FundsRate(session);cmd.CommandText="INSERT OR IGNORE INTO trades(id,seller,buyer,payload,price,state,created) VALUES($i,$s,$s,'',0,'cancelled',$t)";cmd.Parameters.AddWithValue("$s",session.Id);cmd.Parameters.AddWithValue("$t",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();state="cancelled";
        }else if(state=="cancelled"){
            cmd.CommandText="UPDATE trades SET state='cancelled',payload='' WHERE id=$i AND state='proposed'";cmd.ExecuteNonQuery();
        }
        // Cancelled receipts contain no private party, item or price data. A
        // participant with a local journal can safely refund only after this
        // tombstone exists; a late original offer cannot revive the same ID.
        if(state=="cancelled"){session.Client?.Send(new{type="trade",id,state});return;}
        session.Client?.Send(new{type="trade",id,seller,buyer,payload,price,state});
    }
}
