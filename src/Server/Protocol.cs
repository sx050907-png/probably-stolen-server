using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace NightStud.Server;

public static class Wire
{
    public const int Version = 1;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Encode(object value) => JsonSerializer.Serialize(value, Json);
    public static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static async Task<string?> Receive(WebSocket socket, CancellationToken token)
    {
        using var data = new MemoryStream();
        var buffer = new byte[2048];
        WebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (frame.MessageType == WebSocketMessageType.Close) return null;
            if (frame.MessageType != WebSocketMessageType.Text || data.Length + frame.Count > 131072)
                throw new WireFault("bad_frame", "消息超过允许大小。");
            data.Write(buffer, 0, frame.Count);
        } while (!frame.EndOfMessage);
        return new UTF8Encoding(false, true).GetString(data.ToArray());
    }
}

public sealed class WireFault(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record Login(string Name, string AccessKey);
public sealed class Command
{
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    public string Token { get; set; } = "";
    public int Protocol { get; set; }
    public string Code { get; set; } = "";
    public long? Revision { get; set; }
    public string Move { get; set; } = "";
    public int? Amount { get; set; }
    public int? Choice { get; set; }
    public int[]? Cards { get; set; }
    public int Ante { get; set; } = 10;
    public int BuyIn { get; set; } = 1000;
    public bool Ready { get; set; } = true;
    public string Game { get; set; } = "stud";
    public bool Public { get; set; }
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string Invitation { get; set; } = "";
    public string[]? Games { get; set; }
    public string Filter { get; set; }="all";
    public bool AllowVisits { get; set; }
    public bool AllowInvites { get; set; }=true;
    public bool AllowMessages {get;set;}=true;
    public bool AllowCalls {get;set;}=true;
    public bool Blocked {get;set;}
    public string Number {get;set;}="";
    public string Call {get;set;}="";
    public long Sequence {get;set;}
    public string Text { get; set; }="";
    public string Channel {get;set;}="";
    public string Payload { get; set; }="";
    public string Trade { get; set; }="";
    public int Price { get; set; }
    public string Receipt { get; set; }="";
    public string Economy { get; set; }="practice";
    public string SaveId { get; set; }="";
    public string Transfer { get; set; }="";
    public string Direction { get; set; }="";
    public string[]? Capabilities { get; set; }
    public long? SceneRevision {get;set;}
    public long? ShopPresence {get;set;}
    public string ShopEpoch {get;set;}="";
    public string Item {get;set;}="";
    public string Inquiry {get;set;}="";
    public string Avatar {get;set;}="";
    public string Facing {get;set;}="";
    public bool Wager {get;set;}
    public long? CounterRevision {get;set;}
    public string CounterLot {get;set;}="";
    public string CounterHash {get;set;}="";
    public string[]? CounterLots {get;set;}
    public string[]? CounterHashes {get;set;}
    public NightStud.Shared.ShopThing? CounterItem {get;set;}
}

public sealed class Client(WebSocket socket)
{
    readonly Channel<string> outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(32)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    public WebSocket Socket { get; } = socket;
    public bool Closed { get; private set; }
    DateTime budgetAt=DateTime.UtcNow;int received;
    public void CheckTraffic(int bytes){
        if((DateTime.UtcNow-budgetAt).TotalSeconds>=5){budgetAt=DateTime.UtcNow;received=0;}
        if((received+=bytes)>160000)throw new WireFault("bandwidth_limit","发送数据过快，连接已暂停。");
    }

    // Every socket has exactly one writer. A slow receiver cannot hold the room lock.
    public void Send(object value)
    {
        if (!Closed && !outgoing.Writer.TryWrite(Wire.Encode(value))) Stop();
    }
    public void Stop()
    {
        Closed = true;
        outgoing.Writer.TryComplete();
        Socket.Abort();
    }
    public async Task Pump(CancellationToken token)
    {
        try
        {
            await foreach (var value in outgoing.Reader.ReadAllAsync(token))
                await Socket.SendAsync(Encoding.UTF8.GetBytes(value), WebSocketMessageType.Text, true, token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
    }
}

// The game's engine accepts Random. Only the shuffle consumes this source in online play.
public sealed class SecureRandom : Random
{
    public override int Next(int maxValue) => RandomNumberGenerator.GetInt32(maxValue);
    public override int Next(int minValue, int maxValue) => RandomNumberGenerator.GetInt32(minValue, maxValue);
}
