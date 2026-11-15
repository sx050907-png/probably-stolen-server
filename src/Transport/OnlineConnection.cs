#nullable disable
using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NightStudOnline
{
    // No Unity or game access on the worker. All callbacks are marshalled by the mod entry.
    public sealed class OnlineConnection : IDisposable
    {
        readonly Uri server;
        readonly string name;
        string key, token,password,refreshToken;
        readonly bool remember;
        readonly string auth,username;
        bool resumeNext;
        readonly SemaphoreSlim sending = new SemaphoreSlim(1, 1);
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        ClientWebSocket socket;
        public Action<string> Frame;
        public Action<string> RememberCredential;
        public Action ForgetCredential;
        public bool Online => socket?.State == WebSocketState.Open;
        public OnlineConnection(string address,string nickname,string accessKey,string authMode="legacy",string accountUsername=null,string accountPassword=null,bool rememberLogin=false,string rememberedCredential=null)
        {
            if(!Uri.TryCreate(address.Trim().TrimEnd('/')+"/",UriKind.Absolute,out var uri) ||
                (uri.Scheme!="http" && uri.Scheme!="https") || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath!="/" || uri.Query.Length>0 || uri.Fragment.Length>0)
                throw new ArgumentException("服务器请填写 http://主机:端口 或 https://域名。");
            server=uri;name=nickname;key=accessKey;auth=authMode;username=accountUsername;password=accountPassword;remember=rememberLogin;refreshToken=rememberedCredential;
        }
        static HttpClient AuthClient()=>new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(12)};
        public Action<CancellationToken> LogoutRequest(){
            string remembered=refreshToken??"",session=token??"";
            return cancel=>{using var http=AuthClient();Post(http,"api/auth/logout",new JObject{{"refreshToken",remembered},{"token",session}},cancel);};
        }
        JObject Post(HttpClient http,string path,JObject value,CancellationToken cancel){
            using var body=new StringContent(value.ToString(Formatting.None),Encoding.UTF8,"application/json");
            using var response=http.PostAsync(new Uri(server,path),body,cancel).GetAwaiter().GetResult();
            string text=response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if(text.Length>16384)throw new IOException("登录响应过长");
            JObject data;try{data=JObject.Parse(text);}catch(JsonException){data=new JObject();}
            if((int)response.StatusCode==429||(int)response.StatusCode>=500)
                throw new ServiceUnavailable((string)data["error"],(string)data["message"],(int?)data["retryAfterSeconds"]??30);
            if(!response.IsSuccessStatusCode)throw new LoginFailure((string)data["message"]??(response.StatusCode==System.Net.HttpStatusCode.NotFound?"服务器需要更新到 0.3 才能使用账号登录。":"登录失败，请检查账号信息或稍后重试。"),(string)data["error"]);
            return data;
        }
        sealed class LoginFailure:Exception{public readonly string Code;public LoginFailure(string message,string code=null):base(message){Code=code;}}
        sealed class ServiceUnavailable:Exception{public readonly string Status;public readonly int Retry;public ServiceUnavailable(string status,string message,int retry):base(message){Status=status=="maintenance"||status=="closed"?status:"disconnected";Retry=Math.Min(300,Math.Max(2,retry));}}
        void Unavailable(string status="disconnected",string message=null,int retry=0){
            if(status!="maintenance"&&status!="closed")status="disconnected";
            string fallback=status=="maintenance"?"服务器正在维护，请稍后再试。":status=="closed"?"服务器已停止提供联机服务。":"暂时无法连接服务器，可能是网络异常或服务器离线。";
            Frame?.Invoke(new JObject{{"type","transport"},{"status",status},{"message",string.IsNullOrWhiteSpace(message)?fallback:message.Substring(0,Math.Min(140,message.Length))},{"retryAfterSeconds",retry}}.ToString(Formatting.None));
        }
        void ProbeUnavailable(CancellationToken cancel){
            try{
                using var http=AuthClient();http.Timeout=TimeSpan.FromSeconds(3);
                using var response=http.GetAsync(new Uri(server,"health"),cancel).GetAwaiter().GetResult();
                string body=response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if(body.Length<=16384){var health=JObject.Parse(body);if(health["announcement"] is JObject notice)Frame?.Invoke(new JObject{{"type","announcement"},{"announcement",notice}}.ToString(Formatting.None));var service=health["service"];
                    string status=(string)service?["status"];if(status=="maintenance"||status=="closed"){Unavailable(status,(string)service["message"],(int?)service["retryAfterSeconds"]??60);return;}}
            }catch(Exception)when(!cancel.IsCancellationRequested){}
            if(!cancel.IsCancellationRequested)Unavailable();
        }
        JObject Authenticate(HttpClient http,CancellationToken cancel){
            if(auth!="legacy"&&auth!="guest"&&server.Scheme!="https"&&!server.IsLoopback)throw new LoginFailure("账号登录需要 HTTPS，请先使用服主配置好的 HTTPS 地址。");
            if(auth=="legacy")return Post(http,"api/sessions",new JObject{{"name",name},{"accessKey",key}},cancel);
            if(auth=="guest")return Post(http,"api/auth/guest",new JObject{{"name",name}},cancel);
            if(auth=="register"){
                Post(http,"api/auth/register",new JObject{{"username",username},{"password",password},{"name",name}},cancel);
                Frame?.Invoke("{\"type\":\"auth\",\"status\":\"registered\",\"message\":\"注册成功，请登录。\"}");return null;
            }
            if(auth=="resume"){
                if(string.IsNullOrEmpty(refreshToken)){Frame?.Invoke("{\"type\":\"auth\",\"status\":\"missing\"}");return null;}
                return Post(http,"api/auth/refresh",new JObject{{"refreshToken",refreshToken}},cancel);
            }
            if(auth=="password")return Post(http,"api/auth/login",new JObject{{"username",username},{"password",password},{"remember",remember}},cancel);
            if(auth!="steam")throw new LoginFailure("登录方式无效。");
            var flow=Post(http,"api/auth/steam/begin",new JObject{{"name",name}},cancel);
            string url=(string)flow["url"];
            if(!Uri.TryCreate(url,UriKind.Absolute,out var authorize)||authorize.Scheme!="https"||authorize.GetLeftPart(UriPartial.Authority)!=server.GetLeftPart(UriPartial.Authority)||authorize.AbsolutePath!="/auth/steam/start")throw new LoginFailure("Steam 授权地址无效。");
            Frame?.Invoke(new JObject{{"type","auth"},{"status","steamAuthorize"},{"url",url}}.ToString(Formatting.None));
            var deadline=DateTime.UtcNow.AddMinutes(3);
            while(DateTime.UtcNow<deadline){
                if(cancel.WaitHandle.WaitOne(2500))cancel.ThrowIfCancellationRequested();
                var result=Post(http,"api/auth/steam/poll",new JObject{{"state",flow["state"]},{"pollSecret",flow["pollSecret"]},{"remember",remember}},cancel);
                if(result["token"]!=null)return result;
            }
            throw new LoginFailure("Steam 登录已过期，请重新点击登录。");
        }
        public void Run(CancellationToken lifetime)
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(lifetime,stop.Token);
            var cancel=linked.Token;
            try
            {
                if(token==null)
                {
                    using var http=AuthClient();
                    var identity=resumeNext?Post(http,"api/auth/refresh",new JObject{{"refreshToken",refreshToken}},cancel):Authenticate(http,cancel);password=null;if(identity==null)return;
                    token=(string)identity["token"];
                    if(string.IsNullOrEmpty(token))throw new IOException("会话响应无效");
                    refreshToken=(string)identity["refreshToken"];
                    if(!string.IsNullOrEmpty(refreshToken))RememberCredential?.Invoke(refreshToken);
                    resumeNext=false;
                }
                using var ws=new ClientWebSocket();socket=ws;ws.Options.KeepAliveInterval=TimeSpan.FromSeconds(15);
                var uri=new UriBuilder(server){Scheme=server.Scheme=="https"?"wss":"ws",Path="/ws"}.Uri;
                using(var connect=CancellationTokenSource.CreateLinkedTokenSource(cancel)){connect.CancelAfter(TimeSpan.FromSeconds(12));ws.ConnectAsync(uri,connect.Token).GetAwaiter().GetResult();}
                Send(new JObject{{"type","hello"},{"protocol",1},{"token",token},{"games",new JArray("stud","three","holdem","blackjack","landlord","mahjong","dice")},{"capabilities",new JArray("storeStakes","sharedShop","shopReception","roundGoods","shopBell","tradeBatch","shopCounterBatch","contacts")}}.ToString(Formatting.None),cancel);
                var bytes=new byte[4096];
                while(!cancel.IsCancellationRequested && ws.State==WebSocketState.Open)
                {
                    using var message=new MemoryStream();WebSocketReceiveResult part;
                    do{
                        using var idle=CancellationTokenSource.CreateLinkedTokenSource(cancel);idle.CancelAfter(TimeSpan.FromSeconds(45));
                        part=ws.ReceiveAsync(new ArraySegment<byte>(bytes),idle.Token).GetAwaiter().GetResult();
                        if(part.MessageType==WebSocketMessageType.Close)return;
                        if(part.MessageType!=WebSocketMessageType.Text || message.Length+part.Count>196608)throw new IOException("消息格式无效");
                        message.Write(bytes,0,part.Count);
                    }while(!part.EndOfMessage);
                    string text=Encoding.UTF8.GetString(message.ToArray());
                    var value=JObject.Parse(text);
                    if((string)value["type"]=="error" && (string)value["error"]=="unauthorized")throw new LoginFailure("登录会话已过期，请重新登录。","session_expired");
                    if((string)value["type"]=="error" && (string)value["error"]=="account_banned")throw new LoginFailure("该账号已被服主停用。","account_banned");
                    Frame?.Invoke(text);
                }
            }
            catch(ServiceUnavailable error){Unavailable(error.Status,error.Message,error.Retry);}
            catch(LoginFailure error){
                password=null;
                if(error.Code=="session_expired"&&!string.IsNullOrEmpty(refreshToken)){token=null;resumeNext=true;Unavailable("disconnected","服务器会话已更新，正在恢复登录。");}
                else{if((auth=="resume"||resumeNext)&&(error.Code=="remember_expired"||error.Code=="account_banned"))ForgetCredential?.Invoke();Frame?.Invoke(new JObject{{"type","transport"},{"status","rejected"},{"message",error.Message}}.ToString(Formatting.None));}
            }
            catch(OperationCanceledException)when(cancel.IsCancellationRequested) { }
            catch(Exception) when(!cancel.IsCancellationRequested)
            { ProbeUnavailable(cancel); }
            finally { socket=null; }
        }
        public void Send(string text,CancellationToken lifetime)
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(lifetime,stop.Token);
            sending.Wait(linked.Token);
            try{
                var ws=socket;
                if(ws==null || ws.State!=WebSocketState.Open)throw new IOException("尚未连接");
                ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)),WebSocketMessageType.Text,true,linked.Token).GetAwaiter().GetResult();
            }finally{sending.Release();}
        }
        public void Dispose(){stop.Cancel();socket?.Abort();key=token=password=refreshToken=null;}
        public void Interrupt()=>socket?.Abort();
    }
}
