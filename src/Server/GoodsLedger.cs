using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NightStud.Server;

public sealed class GoodsLedger
{
    readonly Accounts accounts;
    public sealed record Kind(string Id,string Label,string Atlas,string Sprite,int BaseValue);
    public sealed record Lot(string Id,string Owner,string Save,string Room,int Hand,int Seat,string Payload,int Quote,string State,string Recipient,string RecipientSave,string AcceptedBy,Kind Kind,string BetMove,int BetAmount,int BetRound,string Expires);
    static readonly Regex Key=new("^[a-f0-9]{32}$",RegexOptions.CultureInvariant);
    static readonly string[] Story={"quest","story","mission","unique","unsellable","key_item","storyline","questbound"};
    static readonly Dictionary<string,Kind> catalogue=LoadCatalogue();
    public static IReadOnlyDictionary<string,Kind> Catalogue=>catalogue;
    static Dictionary<string,Kind> LoadCatalogue(){
        using var stream=typeof(GoodsLedger).Assembly.GetManifestResourceStream("NightStud.Server.stake-catalogue.json")!;
        using var doc=JsonDocument.Parse(stream);var result=doc.RootElement.GetProperty("items").EnumerateArray().Select(i=>new Kind(i.GetProperty("id").GetString()!,i.GetProperty("label").GetString()!,i.GetProperty("atlas").GetString()!,i.GetProperty("sprite").GetString()!,i.GetProperty("baseValue").GetInt32())).ToDictionary(k=>k.Id);foreach(var g in NightStud.Shared.GearCatalogue.All)result[g.Identifier]=new(g.Identifier,g.Name,"night_stud",g.Icon,g.Price);return result;
    }
    static void Need([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool ok,string code,string message){if(!ok)throw new WireFault(code,message);}
    static SqliteCommand Cmd(SqliteConnection db,SqliteTransaction? tx,string sql,params (string,object)[] args){var c=db.CreateCommand();c.Transaction=tx;c.CommandText=sql;foreach(var p in args)c.Parameters.AddWithValue(p.Item1,p.Item2);return c;}
    static void Scope(string account,string save,string id){Need(Key.IsMatch(account)&&Key.IsMatch(save)&&Key.IsMatch(id),"goods_scope","账号、存档或押品编号无效。");}
    public GoodsLedger(Accounts accounts){
        this.accounts=accounts;using var db=accounts.Open();using var c=Cmd(db,null,"""
          CREATE TABLE IF NOT EXISTS goods_stakes(id TEXT PRIMARY KEY,owner TEXT NOT NULL,save_id TEXT NOT NULL,room TEXT NOT NULL,hand INTEGER NOT NULL,seat INTEGER NOT NULL,payload TEXT NOT NULL,quote INTEGER NOT NULL,state TEXT NOT NULL,recipient TEXT NOT NULL DEFAULT '',recipient_save TEXT NOT NULL DEFAULT '',accepted_by TEXT NOT NULL DEFAULT '',kind TEXT NOT NULL);
          CREATE INDEX IF NOT EXISTS goods_recipient ON goods_stakes(recipient,recipient_save,state);
          CREATE INDEX IF NOT EXISTS goods_room ON goods_stakes(room,hand);
          """);c.ExecuteNonQuery();
        var columns=new HashSet<string>();using(var info=Cmd(db,null,"PRAGMA table_info(goods_stakes)"))using(var rows=info.ExecuteReader())while(rows.Read())columns.Add(rows.GetString(1));
        foreach(var field in new[]{("bet_move","TEXT NOT NULL DEFAULT ''"),("bet_amount","INTEGER NOT NULL DEFAULT 0"),("bet_round","INTEGER NOT NULL DEFAULT 0"),("expires","TEXT NOT NULL DEFAULT ''")})if(!columns.Contains(field.Item1)){using var add=Cmd(db,null,"ALTER TABLE goods_stakes ADD COLUMN "+field.Item1+" "+field.Item2);add.ExecuteNonQuery();}
    }
    public static Kind Validate(string payload){
        try{
            Need(payload!=null&&System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(payload,Wire.Json))<=NightStud.Shared.GamePayloadBudget.ResponseStringBytes,"bad_lot","物品资料较长，请分开交易。");
            Need(RoomHub.ValidLot(payload),"bad_lot","押品数据超出范围。");using var doc=JsonDocument.Parse(payload,new JsonDocumentOptions{MaxDepth=16});var nodes=doc.RootElement.GetProperty("nodes");int count=nodes.GetArrayLength();var parents=new int[count];Kind? root=null;int index=0;
            foreach(var node in nodes.EnumerateArray()){
                if(node.TryGetProperty("name",out var name))Need(name.ValueKind is JsonValueKind.Null||name.ValueKind==JsonValueKind.String&&name.GetString()!.Length<=128,"bad_lot","物品名称过长。");
                Need(node.GetProperty("unitCount").TryGetInt32(out int units)&&units is >0 and <=1000,"bad_lot","物品数量超出范围。");
                Need(catalogue.TryGetValue(node.GetProperty("identifier").GetString()!,out var kind),"protected_lot","这件物品不在当前版本的可押物品目录中。");if(index==0)root=kind;
                var children=node.GetProperty("children");var slots=node.GetProperty("slots");Need(children.ValueKind==JsonValueKind.Array&&slots.ValueKind==JsonValueKind.Array&&children.GetArrayLength()==slots.GetArrayLength(),"bad_lot","箱内物品连接无效。");
                foreach(var child in children.EnumerateArray()){Need(child.TryGetInt32(out int n)&&n>index&&n<count&&++parents[n]==1,"bad_lot","箱内物品连接无效。");}
                foreach(var slot in slots.EnumerateArray())Need(slot.TryGetInt32(out int n)&&n is >=0 and <=64,"bad_lot","箱内位置无效。");
                var tags=node.GetProperty("tags");Need(tags.ValueKind==JsonValueKind.Array&&tags.GetArrayLength()<=96,"bad_lot","押品状态超出范围。");
                foreach(var tag in tags.EnumerateArray()){
                    string key=tag.GetProperty("key").GetString()??"";Need(key.Length<=128&&!Story.Any(m=>key.Contains(m,StringComparison.OrdinalIgnoreCase)),"protected_lot","剧情物品受保护，无法上桌。");
                    if(key==NightStud.Shared.GearCatalogue.RemainingTag){var spec=NightStud.Shared.GearCatalogue.Find(kind!.Id);Need(spec?.Durable==true&&tag.TryGetProperty("number",out var wear)&&wear.TryGetInt32(out int remaining)&&remaining>=(spec.Repairable?0:1)&&remaining<=spec.Uses&&(remaining!=0||tag.TryGetProperty("text",out var marker)&&marker.ValueKind==JsonValueKind.String&&marker.GetString()=="0"),"bad_lot","装置耐久数据无效。");}
                    if(key==NightStud.Shared.GearCatalogue.ChargeTag){var spec=NightStud.Shared.GearCatalogue.Find(kind!.Id);Need(spec?.Rechargeable==true&&tag.TryGetProperty("number",out var energy)&&energy.TryGetInt32(out int charge)&&charge>=0&&charge<=spec.ChargeCapacity&&(charge!=0||tag.TryGetProperty("text",out var marker)&&marker.ValueKind==JsonValueKind.String&&marker.GetString()=="0"),"bad_lot","装置电量数据无效。");}
                    foreach(var p in tag.EnumerateObject()){
                        Need(p.Value.ValueKind!=JsonValueKind.String||p.Value.GetString()!.Length<=(p.Name=="text"?512:128),"bad_lot","押品状态文本过长。");
                        Need(p.Value.ValueKind!=JsonValueKind.Number||p.Value.TryGetDouble(out double v)&&double.IsFinite(v),"bad_lot","押品数值无效。");
                    }
                }
                var gear=NightStud.Shared.GearCatalogue.Find(kind!.Id);
                if(gear?.Repairable==true)Need(units==1&&tags.EnumerateArray().Count(t=>t.GetProperty("key").GetString()==NightStud.Shared.GearCatalogue.RemainingTag)==1&&(!gear.Rechargeable||tags.EnumerateArray().Count(t=>t.GetProperty("key").GetString()==NightStud.Shared.GearCatalogue.ChargeTag)==1),"bad_lot","装置状态不完整。");
                foreach(string key in new[]{"shape","modified"})if(node.TryGetProperty(key,out var shape)&&shape.ValueKind!=JsonValueKind.Null){Need(shape.ValueKind==JsonValueKind.String&&shape.GetString()!.Length<=2000,"bad_lot","押品形状超出范围。");using var parsed=JsonDocument.Parse(shape.GetString()!,new JsonDocumentOptions{MaxDepth=10});Need(parsed.RootElement.ValueKind==JsonValueKind.Object,"bad_lot","押品形状无效。");}
                index++;
            }
            Need(parents.Skip(1).All(n=>n==1),"bad_lot","押品中有未连接的物品。");return root!;
        }catch(WireFault){throw;}catch(Exception e)when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException){throw new WireFault("bad_lot","押品数据无效。");}
    }
    static Lot Row(SqliteDataReader r){string kind=r.GetString(12);return new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt32(4),r.GetInt32(5),r.GetString(6),r.GetInt32(7),r.GetString(8),r.GetString(9),r.GetString(10),r.GetString(11),catalogue.TryGetValue(kind,out var k)?k:new(kind,"押品","","",0),r.GetString(13),r.GetInt32(14),r.GetInt32(15),r.GetString(16));}
    // Keep explicit columns so schema upgrades cannot silently reorder receipt fields.
    const string Columns="id,owner,save_id,room,hand,seat,payload,quote,state,recipient,recipient_save,accepted_by,kind,bet_move,bet_amount,bet_round,expires";
    static List<Lot> Read(SqliteConnection db,SqliteTransaction? tx,string where,params (string,object)[] args){using var c=Cmd(db,tx,"SELECT "+Columns+" FROM goods_stakes WHERE "+where,args);using var r=c.ExecuteReader();var rows=new List<Lot>();while(r.Read())rows.Add(Row(r));return rows;}
    public Lot? Find(string id){Need(Key.IsMatch(id),"goods_id","押品编号无效。");using var db=accounts.Open();return Read(db,null,"id=$i",("$i",id)).SingleOrDefault();}
    public Lot Offer(string owner,string save,string id,string room,int hand,int seat,string payload,int quote,string betMove="",int betAmount=0,int betRound=0){
        Scope(owner,save,id);Need(Key.IsMatch(room)&&hand>0&&seat is >=0 and <4&&quote is >0 and <=1000000000,"goods_amount","押品须有正数估价。");
        Need(betMove.Length==0||betMove is "bet" or "raise" or "call" or "allIn"&&betRound is >=1 and <=4&&betAmount>=quote&&quote<=1000000,"asset_amount","实物抵注金额无效。");
        using var db=accounts.Open();using var tx=db.BeginTransaction();var previous=Read(db,tx,"id=$i",("$i",id)).SingleOrDefault();
        if(previous!=null){Need(previous.Owner==owner&&previous.Save==save,"goods_scope","这份押品属于其他账号或存档。");return previous;}
        using(var quota=Cmd(db,tx,"SELECT COUNT(*) FROM goods_stakes WHERE (owner=$a OR recipient=$a) AND state IN ('offered','accepted','locked','claimable')",("$a",owner)))Need(Convert.ToInt32(quota.ExecuteScalar())<8,"goods_quota","请先领取或完成现有押品，再添加新的押品。");
        var kind=Validate(payload);
        using var c=Cmd(db,tx,"INSERT INTO goods_stakes(id,owner,save_id,room,hand,seat,payload,quote,state,kind,bet_move,bet_amount,bet_round,expires) VALUES($i,$a,$s,$r,$h,$p,$d,$q,'offered',$k,$m,$v,$n,$e)",("$i",id),("$a",owner),("$s",save),("$r",room),("$h",hand),("$p",seat),("$d",payload),("$q",quote),("$k",kind.Id),("$m",betMove),("$v",betAmount),("$n",betRound),("$e",betMove.Length==0?"":DateTimeOffset.UtcNow.AddSeconds(16).ToString("O")));c.ExecuteNonQuery();tx.Commit();return Find(id)!;
    }
    public Lot Cancel(string owner,string save,string id){
        Scope(owner,save,id);using var db=accounts.Open();using var tx=db.BeginTransaction();var row=Read(db,tx,"id=$i",("$i",id)).SingleOrDefault();
        if(row==null){using var c=Cmd(db,tx,"INSERT INTO goods_stakes(id,owner,save_id,room,hand,seat,payload,quote,state,recipient,recipient_save,kind) VALUES($i,$a,$s,'',0,0,'',0,'cancelled',$a,$s,'')",("$i",id),("$a",owner),("$s",save));c.ExecuteNonQuery();}
        else{Need(row.Owner==owner&&row.Save==save,"goods_scope","这份押品属于其他账号或存档。");Need(row.State!="locked","goods_playing","这手牌还没结束。");using var c=Cmd(db,tx,"UPDATE goods_stakes SET state='claimable',recipient=owner,recipient_save=save_id WHERE id=$i AND state IN ('offered','accepted')",("$i",id));c.ExecuteNonQuery();}
        tx.Commit();return Find(id)!;
    }
    public void Approve(string id,string receiver,string[]? participants=null){
        using var db=accounts.Open();using var tx=db.BeginTransaction();var row=Read(db,tx,"id=$i",("$i",id)).SingleOrDefault();
        Need(row!=null&&row.Owner!=receiver&&row.BetMove.Length==0&&row.State is "offered" or "accepted","goods_changed","押品已经改变，请重新查看。");
        var approvals=row!.AcceptedBy.Split(',',StringSplitOptions.RemoveEmptyEntries).ToHashSet();approvals.Add(receiver);
        bool all=participants==null||participants.Where(a=>a!=row.Owner).All(approvals.Contains);
        using var c=Cmd(db,tx,"UPDATE goods_stakes SET state=$s,accepted_by=$a WHERE id=$i",("$i",id),("$s",all?"accepted":"offered"),("$a",string.Join(',',approvals.Order())));c.ExecuteNonQuery();tx.Commit();
    }
    public void LockBet(string id,string receiver){using var db=accounts.Open();using var c=Cmd(db,null,"UPDATE goods_stakes SET state='locked',accepted_by=$a WHERE id=$i AND state='offered' AND owner<>$a AND bet_move<>'' AND expires>$t",("$i",id),("$a",receiver),("$t",DateTimeOffset.UtcNow.ToString("O")));Need(c.ExecuteNonQuery()==1,"goods_changed","这次抵注已结束，请重新选择。");}
    public Lot RejectBet(string id){using var db=accounts.Open();using var c=Cmd(db,null,"UPDATE goods_stakes SET state='claimable',recipient=owner,recipient_save=save_id WHERE id=$i AND state='offered' AND bet_move<>''",("$i",id));c.ExecuteNonQuery();return Find(id)!;}
    public List<Lot> Table(string room,int hand){using var db=accounts.Open();return Read(db,null,"room=$r AND hand=$h AND state IN ('offered','accepted','locked','claimable','delivered')",("$r",room),("$h",hand));}
    public List<Lot> Inbox(string account,string save){Scope(account,save,new string('0',32));using var db=accounts.Open();return Read(db,null,"recipient=$a AND recipient_save=$s AND state='claimable' LIMIT 8",("$a",account),("$s",save));}
    public void Begin(SqliteConnection db,SqliteTransaction tx,string room,int hand,string[] participants){
        var lots=Read(db,tx,"room=$r AND hand=$h AND state IN ('offered','accepted')",("$r",room),("$h",hand));
        Need(lots.All(l=>l.BetMove.Length==0&&l.Seat>=0&&l.Seat<participants.Length&&l.Owner==participants[l.Seat]&&l.State=="accepted"&&participants.Where(a=>a!=l.Owner).All(a=>l.AcceptedBy.Split(',').Contains(a))),"goods_unconfirmed","桌上还有押品等待确认，请先看清货物。");
        using var c=Cmd(db,tx,"UPDATE goods_stakes SET state='locked' WHERE room=$r AND hand=$h AND state='accepted'",("$r",room),("$h",hand));c.ExecuteNonQuery();
    }
    public void Finish(SqliteConnection db,SqliteTransaction tx,string room,int hand,int winner,string[] participants,string[] saves,int[]? winningSeats=null){
        var winners=(winningSeats??(winner<0?Array.Empty<int>():new[]{winner})).Distinct().Order().ToArray();Need(winners.All(i=>i>=0&&i<participants.Length),"goods_scope","获胜座位无效。");
        var awarded=new long[participants.Length];
        foreach(var row in Read(db,tx,"room=$r AND hand=$h AND state='locked'",("$r",room),("$h",hand)).OrderByDescending(l=>l.Quote).ThenBy(l=>l.Id)){
            int target=winners.Length==0||winners.Contains(row.Seat)?row.Seat:winners.OrderBy(i=>awarded[i]).ThenBy(i=>i).First();
            if(target!=row.Seat)awarded[target]+=row.Quote;
            using var c=Cmd(db,tx,"UPDATE goods_stakes SET state='claimable',recipient=$a,recipient_save=$s WHERE id=$i",("$i",row.Id),("$a",participants[target]),("$s",saves[target]));c.ExecuteNonQuery();}
        using var pending=Cmd(db,tx,"UPDATE goods_stakes SET state='claimable',recipient=owner,recipient_save=save_id WHERE room=$r AND hand=$h AND state IN ('offered','accepted')",("$r",room),("$h",hand));pending.ExecuteNonQuery();
    }
    public void ReturnUnplayed(string room){using var db=accounts.Open();using var c=Cmd(db,null,"UPDATE goods_stakes SET state='claimable',recipient=owner,recipient_save=save_id WHERE room=$r AND state IN ('offered','accepted')",("$r",room));c.ExecuteNonQuery();}
    public void Recover(){using var db=accounts.Open();using var c=Cmd(db,null,"UPDATE goods_stakes SET state='claimable',recipient=owner,recipient_save=save_id WHERE state IN ('offered','accepted','locked')");c.ExecuteNonQuery();}
    public void Delivered(string id,string recipient,string save){Scope(recipient,save,id);using var db=accounts.Open();using var c=Cmd(db,null,"UPDATE goods_stakes SET state='delivered',payload='' WHERE id=$i AND recipient=$a AND recipient_save=$s AND state='claimable'",("$i",id),("$a",recipient),("$s",save));c.ExecuteNonQuery();}
}
