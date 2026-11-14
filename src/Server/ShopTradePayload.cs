using System.Text.Json;
using NightStud.Shared;

namespace NightStud.Server;

// A batch is one trade receipt and one price. Individual typed lots retain their
// existing tree format; card-table stakes still accept single lots only.
public static class ShopTradePayload
{
    public sealed record Parcel(string[] Items,string[] Labels,ShopThing[]? Layout=null){public bool Batch=>Items.Length>1||Layout!=null;}
    static void Need(bool ok){if(!ok)throw new WireFault("bad_lot","这批货的清单无效，请重新选货。");}
    public static Parcel Parse(string payload){
        try{
            Need(payload is {Length:>1 and <=60000}&&!payload.Contains("$type",StringComparison.OrdinalIgnoreCase)&&GamePayloadBudget.ServerStringBytes(payload)<=GamePayloadBudget.ResponseStringBytes);
            using var doc=JsonDocument.Parse(payload,new JsonDocumentOptions{MaxDepth=16});var root=doc.RootElement;
            Need(root.ValueKind==JsonValueKind.Object);
            if(!root.TryGetProperty("tradeBatch",out var version)){
                GoodsLedger.Validate(payload);return new([payload],Labels(root).ToArray());
            }
            Need(version.TryGetInt32(out int v)&&v==1);
            var properties=root.EnumerateObject().ToArray();
            Need(properties.All(p=>p.Name is "tradeBatch" or "label" or "items" or "counterLayout")&&properties.Select(p=>p.Name).Distinct().Count()==properties.Length);
            Need(root.TryGetProperty("label",out _));string label=Label(root);Need(label.Length<=128);
            bool positioned=root.TryGetProperty("counterLayout",out var positions);var array=root.GetProperty("items");Need(array.ValueKind==JsonValueKind.Array&&array.GetArrayLength()>=(positioned?1:2)&&array.GetArrayLength()<=8);
            var items=new List<string>();var labels=new List<string>{label};int nodes=0;
            foreach(var entry in array.EnumerateArray()){
                Need(entry.ValueKind==JsonValueKind.String);string part=entry.GetString()!;
                GoodsLedger.Validate(part);using var lot=JsonDocument.Parse(part,new JsonDocumentOptions{MaxDepth=16});
                Need(!lot.RootElement.TryGetProperty("tradeBatch",out _));
                nodes+=lot.RootElement.GetProperty("nodes").GetArrayLength();Need(nodes<=64);
                items.Add(part);labels.AddRange(Labels(lot.RootElement));
            }
            ShopThing[]? layout=null;
            if(positioned){
                Need(positions.ValueKind==JsonValueKind.Array&&positions.GetArrayLength()==items.Count);layout=JsonSerializer.Deserialize<ShopThing[]>(positions.GetRawText(),Wire.Json);Need(layout!=null&&layout.Length==items.Count);
                for(int i=0;i<items.Count;i++){var p=layout![i];using var leaf=JsonDocument.Parse(items[i]);var n=leaf.RootElement.GetProperty("nodes")[0];Need(p!=null&&p.Id!=null&&System.Text.RegularExpressions.Regex.IsMatch(p.Id,"^[A-Za-z0-9_-]{1,64}$")&&p.Zone=="counter"&&p.Kind==n.GetProperty("identifier").GetString()&&p.Count==n.GetProperty("unitCount").GetInt32()&&p.Quote is >0 and <=1000000&&p.Flags is >=0 and <=7&&p.CanTrade&&p.Orientation is >=0 and <=3&&p.W is >0 and <=1024&&p.H is >0 and <=1024&&p.X is >=0 and <=2048&&p.Y is >=0 and <=2048);}
            }
            return new(items.ToArray(),labels.ToArray(),layout);
        }catch(WireFault){throw;}catch(Exception e)when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException){throw new WireFault("bad_lot","这批货的清单无效，请重新选货。");}
    }
    static string Label(JsonElement root){
        if(!root.TryGetProperty("label",out var label))return "";
        Need(label.ValueKind==JsonValueKind.String);return label.GetString()!;
    }
    static IEnumerable<string> Labels(JsonElement root){
        yield return Label(root);
        foreach(var node in root.GetProperty("nodes").EnumerateArray())if(node.TryGetProperty("name",out var name)&&name.ValueKind==JsonValueKind.String)yield return name.GetString()!;
    }
}
