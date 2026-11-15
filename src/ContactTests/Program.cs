using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using NightStud.Server;

int checks=0;void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
string root=Path.GetFullPath(args.FirstOrDefault()??"work/contact0500-tests");Directory.CreateDirectory(root);string data=Path.Combine(root,Guid.NewGuid().ToString("N"));
var accounts=new Accounts(data);var moderation=new Moderation(data);var hub=new RoomHub(90,null,accounts,moderation);
var a=await accounts.Register("contact_a","testing-contact-password","联络甲");var b=await accounts.Register("contact_b","testing-contact-password","联络乙");var c=await accounts.Register("contact_c","testing-contact-password","旁观者");
await using var one=new Peer(hub,hub.LoginAccount(a));await using var two=new Peer(hub,hub.LoginAccount(b));await using var outsider=new Peer(hub,hub.LoginAccount(c));
Check((await one.Do("contactSend",new(){Target=b.Id,Text="晚上一起打牌吗？"})).GetProperty("ok").GetBoolean(),"private send acknowledged after storage");
Check(one.Last("contactMessage").GetProperty("message").GetProperty("sender").GetString()==a.Id,"sender identity belongs to authenticated account");
Check(two.Last("contactMessage").GetProperty("message").GetProperty("text").GetString()=="晚上一起打牌吗？","recipient receives private message");Check(outsider.Count("contactMessage")==0,"third player never receives private payload");
var store=new ContactStore(new Accounts(data));Check(store.Unread(b.Id)==1&&store.Unread(c.Id)==0,"unread belongs only to recipient after database reopen");
await two.Do("contactHistory",new(){Target=a.Id});var history=two.Last("contactHistory").GetProperty("messages");Check(history.GetArrayLength()==1,"history is conversation-scoped");
await outsider.Do("contactHistory",new(){Target=a.Id});Check(outsider.Last("contactHistory").GetProperty("messages").GetArrayLength()==0,"arbitrary target cannot read third-party conversation");
await two.Do("contactRead",new(){Target=a.Id,Sequence=history[0].GetProperty("sequence").GetInt64()});Check(store.Unread(b.Id)==0,"explicit read clears only recipient's unread");
await two.Disconnect();Check((await one.Do("contactSend",new(){Target=b.Id,Text="你上线后再联系。"})).GetProperty("ok").GetBoolean(),"offline message saved");
await two.Reconnect();Check(two.Last("contacts").GetProperty("unread").GetInt32()==1,"offline unread delivered on reconnect");
Check(new ContactStore(new Accounts(data)).History(b.Id,a.Id).Length==2,"offline messages survive store and account restart");
await two.Do("contactSettings",new(){AllowMessages=false,AllowCalls=false});Check(store.Settings(b.Id)==new ContactPrivacy(false,false),"contact privacy persists");
Check((await one.Do("contactSend",new(){Target=b.Id,Text="不该送达"})).GetProperty("error").GetString()=="contact_unavailable","disabled messages rejected on server");
Check((await one.Do("phoneStart",new(){Target=b.Id})).GetProperty("error").GetString()=="contact_unavailable","do not disturb enforced on server");
await two.Do("contactSettings",new(){AllowMessages=true,AllowCalls=true});
Check((await one.Do("phoneStart",new(){Target=b.Id})).GetProperty("ok").GetBoolean(),"call begins");string call=two.Last("phone").GetProperty("id").GetString()!;
Check(two.Last("phone").GetProperty("incoming").GetBoolean()&&!one.Last("phone").GetProperty("incoming").GetBoolean(),"incoming direction is private and correct");Check(outsider.Count("phone")==0,"no third party receives phone states");
Check((await outsider.Do("phoneAccept",new(){Call=call})).GetProperty("ok").GetBoolean()==false,"third party cannot accept call");
Check((await two.Do("phoneAccept",new(){Call=call})).GetProperty("ok").GetBoolean(),"recipient accepts");Check(one.Last("phone").GetProperty("state").GetString()=="connected","caller sees connected state");
Check((await one.Do("contactSend",new(){Target=b.Id,Call=call,Text="电话接通了。"})).GetProperty("ok").GetBoolean(),"connected call text persists");Check(two.Last("contactMessage").GetProperty("message").GetProperty("kind").GetString()=="call","call text labeled");
Check((await outsider.Do("phoneEnd",new(){Call=call})).GetProperty("ok").GetBoolean()==false,"third party cannot end call");
await two.Do("phoneEnd",new(){Call=call});Check(one.Last("phone").GetProperty("state").GetString()=="ended","hangup reaches caller");
Check((await one.Do("phoneStart",new(){Target=b.Id})).GetProperty("error").GetString()=="call_rate","redial cooldown enforced");
await two.Do("contactBlock",new(){Target=a.Id,Blocked=true});Check(store.Blocked(b.Id,a.Id),"block persists");Check((await one.Do("contactSend",new(){Target=b.Id,Text="禁止的私信"})).GetProperty("ok").GetBoolean()==false,"block rejects private message");
await two.Do("contactBlock",new(){Target=a.Id,Blocked=false});
Check((await one.Do("contactSend",new(){Target=b.Id,Text="https://example.com"})).GetProperty("error").GetString()=="chat_filtered","links filtered in private messages too");
Check((await one.Do("contactSend",new(){Target=b.Id,Text="出售银行卡"})).GetProperty("error").GetString()=="chat_filtered","blocked content filtered in messages");
Check((await outsider.Do("contactSend",new(){Target=a.Id,Text=new string('x',121)})).GetProperty("error").GetString()=="chat_length","message length bounded");
Check((await outsider.Do("contactSend",new(){Target=c.Id,Text="self"})).GetProperty("error").GetString()=="contact_unavailable","self messaging rejected");
Check((await outsider.Do("contactSend",new(){Target="' OR 1=1",Text="bad target"})).GetProperty("ok").GetBoolean()==false,"invalid target does not expose account data");
// Deterministic store quotas and retention avoid a slow real-time spam test.
using(var db=accounts.Open()){using var cmd=db.CreateCommand();cmd.CommandText="UPDATE contact_messages SET created=$old";cmd.Parameters.AddWithValue("$old",DateTimeOffset.UtcNow.AddDays(-31).ToString("O"));cmd.ExecuteNonQuery();}store.Prune();Check(store.History(b.Id,a.Id).Length==0,"expired messages are removed after thirty days");
var guest=hub.Login("访客");await using var anonymous=new Peer(hub,guest);Check((await anonymous.Do("contacts")).GetProperty("error").GetString()=="account_required","guest identity cannot access account contacts");
string summary=$"PASS {checks} contact assertions: authenticated private scope, persisted offline messages, unread/read, call acceptance and ownership, hangup, redial, privacy, blocking, filtering and retention. In-memory production WebSocket peers; native UI and real TCP testing are separate.";
File.WriteAllText(Path.Combine(root,"success.txt"),summary);Console.WriteLine(summary);

sealed class Peer:IAsyncDisposable {
 readonly RoomHub hub;readonly Session session;readonly Socket socket=new();Client client;CancellationTokenSource stop=new();Task pump;
 public Peer(RoomHub hub,Session session){this.hub=hub;this.session=session;client=new(socket);pump=client.Pump(stop.Token);Attach();}
 void Attach()=>hub.Attach(new(){Type="hello",Protocol=1,Token=session.Token,Capabilities=["contacts"]},client);
 public JsonElement Last(string type)=>socket.Frames.LastOrDefault(f=>f.GetProperty("type").GetString()==type);
 public int Count(string type)=>socket.Frames.Count(f=>f.GetProperty("type").GetString()==type);
 public async Task<JsonElement> Do(string type,Command? command=null){command??=new();command.Type=type;command.Id=Guid.NewGuid().ToString("N");session.RateSince=DateTime.UtcNow.AddSeconds(-2);hub.Handle(session,client,command);for(int i=0;i<100;i++){await Task.Delay(5);var ack=Last("ack");if(ack.ValueKind==JsonValueKind.Object&&ack.GetProperty("id").GetString()==command.Id){await Task.Delay(5);return ack;}}throw new Exception("missing ack: "+type);}
 public async Task Disconnect(){hub.Detach(session,client);client.Stop();stop.Cancel();await pump;}
 public Task Reconnect(){stop.Dispose();stop=new();client=new(socket);pump=client.Pump(stop.Token);Attach();return Task.Delay(30);}
 public async ValueTask DisposeAsync(){await Disconnect();stop.Dispose();}
 sealed class Socket:WebSocket {
  public readonly ConcurrentQueue<JsonElement> Frames=new();public override WebSocketState State=>WebSocketState.Open;public override WebSocketCloseStatus? CloseStatus=>null;public override string? CloseStatusDescription=>null;public override string? SubProtocol=>null;public override void Abort(){}public override void Dispose(){}
  public override Task CloseAsync(WebSocketCloseStatus s,string? t,CancellationToken c)=>Task.CompletedTask;public override Task CloseOutputAsync(WebSocketCloseStatus s,string? t,CancellationToken c)=>Task.CompletedTask;public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte>b,CancellationToken c)=>throw new NotSupportedException();
  public override Task SendAsync(ArraySegment<byte>b,WebSocketMessageType type,bool end,CancellationToken cancel){using var doc=JsonDocument.Parse(b.AsMemory());Frames.Enqueue(doc.RootElement.Clone());return Task.CompletedTask;}
 }
}
