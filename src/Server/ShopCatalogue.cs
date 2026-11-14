using System.Text.Json;

namespace NightStud.Server;

// Artwork metadata only. This catalogue grants no inventory or trading rights.
public static class ShopCatalogue
{
    public static readonly IReadOnlyDictionary<string,GoodsLedger.Kind> All=Load();
    static Dictionary<string,GoodsLedger.Kind> Load(){
        var result=GoodsLedger.Catalogue.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
        using var stream=typeof(ShopCatalogue).Assembly.GetManifestResourceStream("NightStud.Server.shop-catalogue.json")!;
        using var doc=JsonDocument.Parse(stream);
        foreach(var i in doc.RootElement.GetProperty("items").EnumerateArray()){
            string id=i.GetProperty("id").GetString()!;
            if(!result.ContainsKey(id))result[id]=new GoodsLedger.Kind(id,i.GetProperty("label").GetString()!,i.GetProperty("atlas").GetString()!,i.GetProperty("sprite").GetString()!,0);
        }
        return result;
    }
}
