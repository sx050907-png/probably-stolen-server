using NightStud;

namespace NightStud.Server;

public sealed class Session(string name,string? accountId=null,string provider="guest")
{
    public string Id { get; } = accountId??Guid.NewGuid().ToString("N");
    public string Token { get; set; } = Wire.Token();
    public string? AccountId { get; } = accountId;
    public string Provider { get; } = provider;
    public Privacy Privacy=new();
    public string PlayerFilter="all";
    public string Name { get; } = name;
    public string? RoomCode;
    public Client? Client;
    public DateTime LastSeen = DateTime.UtcNow;
    public DateTime? Disconnected;
    public Queue<string> Recent { get; } = new();
    public HashSet<string> Used { get; } = new();
    public DateTime RateSince = DateTime.UtcNow;
    public int RateCount;
    public HashSet<string> Games=new(StringComparer.Ordinal){"stud"};
    public string ShopHours="";
    public bool StoreStakes;
    public bool SharedShop;
    public bool ShopReception;
    public bool ShopBell;
    public bool TradeBatches;
    public bool CounterBatches;
    public bool Contacts;
    public bool AssetBets;
    public bool TableTech;
    public string Avatar="hood_ash";
    public DateTime FundsSince=DateTime.UtcNow;
    public int FundsCount;
}

public sealed class Seat(Session session, int balance)
{
    public Session Session { get; } = session;
    public int Balance = balance;
    public bool Ready;
    public string CashHold="",SaveId="";
}

public sealed class Room(string code, int ante, int buyIn, string mode="stud", bool isPublic=false, string name="",string economy="practice")
{
    public string Economy {get;}=economy;
    public string LedgerId {get;}=Guid.NewGuid().ToString("N");
    public bool CashFault;
    public bool TechEnabled;
    public string Code { get; } = code;
    public int Ante { get; } = ante;
    public int BuyIn { get; } = buyIn;
    public long Revision;
    public int Hands;
    public Seat?[] Seats { get; } = new Seat?[OnlineRound.Capacity(mode)];
    public string Mode { get; } = mode;
    public bool Public { get; } = isPublic;
    public string Name { get; } = name;
    public OnlineRound? Game;
    public int[] Committed { get; } = new int[OnlineRound.Capacity(mode)];
    public int[] Initial { get; } = new int[OnlineRound.Capacity(mode)];
    public DateTime NextBot = DateTime.UtcNow;
    public string HostId="";
    public object? Result;
    public List<object> Events { get; } = new();
    public DateTime Touched = DateTime.UtcNow;
    public DateTime PhraseAt=DateTime.MinValue;
}

public sealed partial class RoomHub(int graceSeconds, Action<ServerEvent>? audit = null,Accounts? accounts=null,Moderation? moderation=null)
{
    readonly object gate = new();
    ServiceState availability=ServiceStatus.Online;
    NightStud.Shared.ServerNotice? announcement;
    public NightStud.Shared.ServerNotice? Announcement {get{lock(gate)return announcement;}}
    public void SetAnnouncement(NightStud.Shared.ServerNotice? value){
        lock(gate){if(value?.Id==announcement?.Id&&value?.Title==announcement?.Title&&value?.Body==announcement?.Body&&value?.UpdatedAt==announcement?.UpdatedAt)return;announcement=value;
            foreach(var session in sessions.Values.Where(Online))session.Client?.Send(new{type="announcement",announcement});
        }
    }
    public ServiceState Availability {get{lock(gate)return availability;}}
    public void SetAvailability(ServiceState value){
        lock(gate){if(value==availability)return;availability=value;
            foreach(var session in sessions.Values.Where(Online))session.Client?.Send(new{type="service",status=value.Status,message=value.Message,retryAfterSeconds=value.RetryAfterSeconds});
        }
    }
    readonly StakeLedger? cash=OpenCash(accounts);
    readonly GoodsLedger? goods=OpenGoods(accounts);
    static GoodsLedger? OpenGoods(Accounts? accounts){if(accounts==null)return null;var ledger=new GoodsLedger(accounts);ledger.Recover();return ledger;}
    static StakeLedger? OpenCash(Accounts? accounts){if(accounts==null)return null;var ledger=new StakeLedger(accounts);ledger.RecoverInterruptedServer();return ledger;}
    readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    readonly Dictionary<string, Room> rooms = new(StringComparer.Ordinal);
    readonly Dictionary<string, Invitation> invitations = new();
    readonly Dictionary<string, DateTime> inviteCooldown = new();
    sealed record Invitation(string Id,string From,string To,string Code,DateTime Expires);
    public static readonly string[] Features={"lobby","invitations","stud","three","holdem","blackjack","landlord","mahjong","dice","bots","accounts","privacy","playerFilter","chat","visits","trades","storeStakes","goodsStakes","sharedShop","shopReception","roundGoods","groupGoods","shopBell","shopDisplay","shopCounter","tradeBatch","shopCounterBatch","higherStakes","contacts","playerPhones","playerMarket","physicalGear","gearDurability","gearMaintenance","landlordBanter","tableVoice","tableVoiceAllModes","tableTech","shopHours"};
    void Audit(string name,Session session,Room? room=null,string? reason=null)=>audit?.Invoke(new ServerEvent(name,session.Id,room?.Code,room?.Mode,reason));
    static void FundsRate(Session session){
        var now=DateTime.UtcNow;if((now-session.FundsSince).TotalMinutes>=1){session.FundsSince=now;session.FundsCount=0;}
        Need(++session.FundsCount<=60,"funds_rate","暂存操作过于频繁，请稍候再试。");
    }
    public int GraceSeconds { get; } = graceSeconds;
    static string[] FeaturesFor(Session session)=>Features.Where(f=>(f!="tableTech"||session.TableTech)&&(f!="shopBell"||session.ShopBell)&&(f!="shopCounterBatch"||session.CounterBatches)).ToArray();

    public object Status()
    {
        lock (gate) return new { ok = true, version = ServerBuild.Version, protocol = Wire.Version,
            mode = "practice", features = Features, rooms = rooms.Count, online = sessions.Values.Count(Online), reconnectSeconds = GraceSeconds, service=availability, announcement };
    }

    public Session Login(string name)
    {
        lock (gate)
        {
            if (name is null || name.Trim().Length is < 1 or > 20 || name.Any(char.IsControl))
                throw new WireFault("bad_name", "昵称请填 1–20 个字。");
            if (sessions.Count >= 2000) throw new WireFault("server_full", "当前会话已满，请稍后再试。");
            var session = new Session(name.Trim());
            sessions.Add(session.Token, session);
            Audit("session_created",session);
            return session;
        }
    }

    public Session LoginAccount(AccountIdentity identity){
        lock(gate){
            var existing=sessions.Values.FirstOrDefault(s=>s.AccountId==identity.Id);
            if(existing!=null){
                EndPhoneFor(existing.Id,"reconnected");string old=existing.Token;existing.Client?.Stop();existing.Client=null;existing.Disconnected=DateTime.UtcNow;
                existing.Token=Wire.Token();sessions.Remove(old);sessions.Add(existing.Token,existing);existing.Privacy=accounts?.GetPrivacy(identity.Id)??new();existing.Avatar=accounts?.GetAppearance(identity.Id)??"hood_ash";
                if(Current(existing) is {} room){room.Revision++;Broadcast(room);}Audit("account_reauthenticated",existing);return existing;
            }
            Need(sessions.Count<2000,"server_full","当前会话已满，请稍后再试。");
            var session=new Session(Accounts.Nickname(identity.Name),identity.Id,identity.Provider){Privacy=accounts?.GetPrivacy(identity.Id)??new(),Avatar=accounts?.GetAppearance(identity.Id)??"hood_ash"};
            sessions.Add(session.Token,session);Audit("account_logged_in",session);return session;
        }
    }

    public void Logout(string? token){
        lock(gate){
            if(token==null||!sessions.TryGetValue(token,out var session))return;
            if(Current(session) is {} room)Leave(room,session,"logout");
            EndPhoneFor(session.Id,"disconnected");EndShopPresence(session);session.Client?.Stop();sessions.Remove(token);BroadcastLobby();Audit("account_logged_out",session);
        }
    }

    static bool Online(Session session) => session.Provider=="bot" || session.Client is { Closed: false };
    public Session Attach(Command hello, Client client)
    {
        lock (gate)
        {
            if (hello.Type != "hello" || hello.Protocol != Wire.Version)
                throw new WireFault("protocol", "客户端协议版本不匹配。");
            if (!sessions.TryGetValue(hello.Token ?? "", out var session))
                throw new WireFault("unauthorized", "会话已过期，请重新连接。");
            Need(moderation?.Banned(session.Id)!=true,"account_banned","该账号已被服主停用。");
            bool tableTech=hello.Capabilities?.Contains("tableTech")==true;
            var games=new HashSet<string>((hello.Games??Array.Empty<string>()).Where(OnlineRound.Supported).Take(OnlineRound.Games.Length),StringComparer.Ordinal){"stud"};
            if(Current(session) is {TechEnabled:true} techRoom)
                Need(tableTech && games.Contains(techRoom.Mode),"tech_client","请用支持这局玩法和科技桌的客户端重连。");
            session.Client?.Stop();
            session.Client = client;
            session.TableTech=tableTech;
            session.Games=games;
            session.StoreStakes=hello.Capabilities?.Contains("storeStakes")==true;
            session.SharedShop=hello.Capabilities?.Contains("sharedShop")==true;
            session.ShopReception=session.SharedShop&&hello.Capabilities?.Contains("shopReception")==true;
            session.ShopBell=session.SharedShop&&hello.Capabilities?.Contains("shopBell")==true;
            session.TradeBatches=hello.Capabilities?.Contains("tradeBatch")==true;
            session.CounterBatches=session.TradeBatches&&session.SharedShop&&hello.Capabilities?.Contains("shopCounterBatch")==true;
            session.AssetBets=hello.Capabilities?.Contains("roundGoods")==true;
            session.Contacts=hello.Capabilities?.Contains("contacts")==true;
            session.Disconnected = null;
            session.LastSeen = DateTime.UtcNow;
            client.Send(new { type = "welcome", protocol = Wire.Version, playerId = session.Id,
                name = session.Name, provider=session.Provider, phone=phones?.Number(session.AccountId)??"", mode = "practice", features = FeaturesFor(session), reconnectSeconds = GraceSeconds });
            client.Send(new{type="announcement",announcement});
            SendContacts(session);
            if (Current(session) is { } room) { room.Revision++; Broadcast(room); }
            else SendLobby(session);
            Audit("session_attached",session,Current(session));BroadcastLobby();
            return session;
        }
    }

    public void Detach(Session session, Client client)
    {
        lock (gate)
        {
            if (session.Client != client) return;
            EndPhoneFor(session.Id,"disconnected");
            session.Client = null;
            if(shops.ContainsKey(session.Id))WithdrawHostedShop(session,"away");
            session.Disconnected = DateTime.UtcNow;
            Audit("session_detached",session,Current(session));
            if (Current(session) is { } room)
            {
                foreach (var seat in room.Seats.Where(s => s is not null)) seat!.Ready = false;
                room.Revision++;
                Broadcast(room);
            }
            BroadcastLobby();
        }
    }

    Room? Current(Session session) => session.RoomCode is { } code && rooms.TryGetValue(code, out var room) ? room : null;
    static int OwnSeat(Room room, Session session) => Array.FindIndex(room.Seats, s => s?.Session == session);
    static void Need([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string code, string message)
    { if (!condition) throw new WireFault(code, message); }

    public void Handle(Session session, Client client, Command command)
    {
        lock (gate)
        {
            if (session.Client != client) return;
            try
            {
                Need(moderation?.Banned(session.Id)!=true,"account_banned","该账号已被服主停用。");
                var now = DateTime.UtcNow;
                session.LastSeen = now;
                if ((now - session.RateSince).TotalSeconds >= 1) { session.RateSince = now; session.RateCount = 0; }
                Need(++session.RateCount <= 20, "rate_limit", "操作太快，请稍候。");
                if(command.Type is "cashTransfer" or "cashCancel" or "cashDelivered" or "goodsOffer" or "goodsCancel" or "goodsDelivered" or "tradeOffer" or "tradeCancel" or "tradeAccept")FundsRate(session);
                Need(availability.Status=="online"||!(command.Type is "create" or "join" or "addBot"||command.Type=="ready"&&command.Ready!=false),"maintenance",availability.Message);
                if (command.Type == "ping") { client.Send(new { type = "pong" }); return; }
                if (command.Type == "sync") { SendState(session); SendDirectory(session); return; }
                Need(command.Id is { Length: > 0 and <= 64 } && command.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
                    "bad_id", "操作编号无效。");
                Need(session.Used.Add(command.Id), "duplicate", "这次操作已处理。");
                session.Recent.Enqueue(command.Id);
                if (session.Recent.Count > 128) session.Used.Remove(session.Recent.Dequeue());
                switch (command.Type)
                {
                    case "tablePhrase": TablePhrase(session,command);client.Send(new{type="ack",id=command.Id,ok=true});return;
                    case "contacts":case "contactSettings":case "contactBlock":case "contactHistory":case "contactRead":case "contactSend":case "contactReceipt":case "phoneStart":case "phoneAccept":case "phoneEnd":case "phoneDial":case "phoneRead":
                        ContactCommand(session,command);client.Send(new{type="ack",id=command.Id,ok=true});return;
                    case "marketBrowse":case "marketPreview":case "marketOffer":case "marketBuy":case "marketCancel":case "marketStatus":case "marketDelivered":
                        MarketCommand(session,command);client.Send(new{type="ack",id=command.Id,ok=true});return;
                    case "playerFilter": Need(command.Filter is "all" or "steam" or "nonSteam","bad_filter","玩家筛选无效。");session.PlayerFilter=command.Filter;client.Send(new{type="ack",id=command.Id,ok=true});SendDirectory(session);return;
                    case "privacy": SetPrivacy(session,command);break;
                    case "chat": Chat(session,command.Text,command.Channel);client.Send(new{type="ack",id=command.Id,ok=true});return;
                    case "visit": Visit(session,command.Target);break;
                    case "leaveVisit": LeaveVisit(session);break;
                    case "shopHours": SetShopHours(session,command.Text);client.Send(new{type="ack",id=command.Id,ok=true});return;
                    case "shop": PublishShop(session,command);client.Send(new{type="ack",id=command.Id,ok=true});return;
                    case "shopScene": PublishSharedShop(session,command);client.Send(new{type="ack",id=command.Id,ok=true});return;
                    case "shopSync":case "shopInspect":case "shopAsk":case "shopAnswer":
                    case "shopEnter":case "shopExit":case "shopLook":case "shopKick":case "shopServe":case "shopBell":case "shopCounterPut":case "shopCounterRemove":case "shopCounterKeep":
                        SharedShopAction(session,command);client.Send(new{type="ack",id=command.Id,ok=true});return;
                    case "appearance": SetAppearance(session,command);client.Send(new{type="ack",id=command.Id,ok=true});SendDirectory(session);return;
                    case "tradeOffer": OfferTrade(session,command);break;
                    case "tradeAccept": AcceptTrade(session,command);break;
                    case "tradeReceipt": TradeReceipt(session,command);break;
                    case "tradeCancel": CancelTrade(session,command);break;
                    case "cashWallet": case "cashTransfer": case "cashReceipt": case "cashCancel": case "cashDelivered":
                        CashCommand(session,command);client.Send(new{type="ack",id=command.Id,ok=true});return;
                    case "fund": FundSeat(Revision(session,command),session,command.SaveId);break;
                    case "goodsOffer": case "goodsApprove": case "goodsReject": case "goodsCancel": case "goodsReceipt": case "goodsList": case "goodsDelivered":
                        GoodsCommand(session,command);client.Send(new{type="ack",id=command.Id,ok=true});if(Current(session) is {} goodsRoom)Broadcast(goodsRoom);return;
                    case "create": Create(session, command); break;
                    case "invite": Invite(session, command.Target); break;
                    case "acceptInvite": AcceptInvite(session, command); break;
                    case "declineInvite": DeclineInvite(session, command.Invitation); break;
                    case "join": Join(session, command); break;
                    case "ready": Ready(Revision(session, command), session, command.Ready,command.SaveId,command.Enabled==true); break;
                    case "botAdd": SetBot(Revision(session, command),session,true); break;
                    case "botRemove": SetBot(Revision(session, command),session,false); break;
                    case "techRules": SetTechRules(Revision(session,command),session,command.Enabled); break;
                    case "useTech":
                        UseTableTech(Revision(session,command),session,command.Item);
                        client.Send(new{type="ack",id=command.Id,ok=true});
                        SendState(session);
                        return;
                    case "act": Act(Revision(session, command), session, command); break;
                    case "leave": Leave(Revision(session, command), session, "leave"); break;
                    default: throw new WireFault("unknown", "未知操作。");
                }
                client.Send(new { type = "ack", id = command.Id, ok = true });
                if (Current(session) is { } current) Broadcast(current); else SendLobby(session);
                if(command.Type!="act" || Current(session)?.Game?.Done==true)BroadcastLobby();
            }
            catch (WireFault ex)
            {
                Audit("action_rejected",session,Current(session),ex.Code);
                client.Send(new { type = "ack", id = command.Id, ok = false, error = ex.Code, message = ex.Message });
                SendState(session);
            }
            catch(Microsoft.Data.Sqlite.SqliteException){
                Audit("storage_error",session,Current(session),command.Type);
                client.Send(new{type="ack",id=command.Id,ok=false,error="storage",message="暂存记录还没写好，请稍后重试。"});SendState(session);
            }
        }
    }

    Room Revision(Session session, Command command)
    {
        var room = Current(session) ?? throw new WireFault("no_room", "请先进入房间。");
        Need(command.Revision == room.Revision, "stale", "牌桌已更新，请按当前牌面操作。");
        return room;
    }

    void Create(Session session, Command command)
    {
        Need(Current(session) is null, "in_room", "请先离开当前房间。");
        Need(rooms.Count < 128, "server_full", "牌桌已满，请稍后再试。");
        Need(command.Ante is >= 1 and <= NightStud.Shared.WagerRules.MaxAnte && command.BuyIn >= command.Ante * 10 && command.BuyIn <= NightStud.Shared.WagerRules.MaxBuyIn,
            "bad_rules", "底注为 1–20000，带入筹码至少为底注的 10 倍，最多 1000000。");
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        string code;
        do { code = new string(Enumerable.Range(0, 8).Select(_ => alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray()); }
        while (rooms.ContainsKey(code));
        Need(OnlineRound.Supported(command.Game), "bad_game", "该玩法尚未开放联机。 ");
        Need(command.Game!="mahjong"||command.BuyIn>=48*command.Ante,"bad_rules","麻将每人带入至少为底分的 48 倍，覆盖五番封顶与一炮多响。");
        Need(session.Games.Contains(command.Game),"client_update_required","更新联机组件后可使用这项玩法。 ");
        string name=(command.Name??"").Trim();
        Need(name.Length<=30 && !name.Any(char.IsControl),"bad_room_name","房间名称最多 30 个字。 ");
        if(name.Length>0)moderation?.Content(name);
        Need(command.Economy is "practice" or "store","bad_economy","请选择练习筹码或游戏信用点。");
        if(command.Economy=="store")Need(cash!=null&&session.AccountId!=null&&session.StoreStakes,"cash_client","登录账号并更新客户端后可使用游戏信用点。");
        var room = new Room(code, command.Ante, command.BuyIn, command.Game, command.Public, name.Length==0?session.Name+"的牌桌":name,command.Economy) { Revision = 1,HostId=session.Id };
        room.Seats[0] = new Seat(session, room.Economy=="store"?0:command.BuyIn);
        EndShopPresence(session);
        rooms.Add(code, room);
        session.RoomCode = code;
        Audit("room_created",session,room);
    }

    void Join(Session session, Command command)
    {
        Need(Current(session) is null, "in_room", "请先离开当前房间。");
        var code = (command.Code ?? "").Trim().ToUpperInvariant();
        Need(rooms.TryGetValue(code, out var room), "not_found", "房间不存在或已关闭。");
        Need(!room!.CashFault,"cash_settling","这间房正在保存结算，请稍后再入座。");
        Need(session.Games.Contains(room!.Mode),"client_update_required","本房间使用新玩法，请先更新客户端和联机组件。 ");
        Need(!room.TechEnabled||CanPlayTech(session,room),"tech_client","这是科技练习桌，请先更新客户端。");
        if(room.Economy=="store")Need(cash!=null&&session.AccountId!=null&&session.StoreStakes,"cash_client","这间房使用游戏信用点，请登录账号并更新客户端。");
        Need(room.Game is null||room.Game.Done,"playing","本局正在进行，请等下一局再入座。");
        int seat = Array.FindIndex(room!.Seats, s => s is null);
        if(seat<0)seat=Array.FindIndex(room.Seats,s=>s?.Session.Provider=="bot");
        Need(seat >= 0, "room_full", "房间已经坐满。");
        // Filling an empty seat starts a fresh table ledger, agreed by both players on ready.
        EndShopPresence(session);
        room.Seats[seat] = new Seat(session, room.Economy=="store"?0:room.BuyIn);
        session.RoomCode = code;
        room.Game = null; room.Result = null; Array.Clear(room.Committed);
        room.Events.Clear();
        room.Events.Add(new { kind = room.Economy=="store"?"seat_join":"table_reset", amount = room.Economy=="store"?0:room.BuyIn });
        foreach (var player in room.Seats.Where(p=>p!=null)) { player!.Ready = player.Session.Provider=="bot"; if(room.Economy=="practice")player.Balance = room.BuyIn; }
        room.Revision++;
        room.Touched = DateTime.UtcNow;
    }

    void Ready(Room room, Session session, bool ready,string save="",bool acceptsTech=false)
    {
        Need(room.Game is null || room.Game.Done, "playing", "本手尚未结束。");
        Need(!ready || acceptsTech==room.TechEnabled,"tech_consent","牌桌规则已变，请看清是否启用科技后再准备。");
        var player = room.Seats[OwnSeat(room, session)]!;
        Need(!room.CashFault,"cash_settling","正在保存这手牌的结算，请稍候。");
        if(room.Economy=="store")Need(player.CashHold.Length>0&&player.SaveId==save,"need_fund","先用当前经营存档暂存入桌信用点。");
        if(room.Economy=="store"&&ready)Need(room.Seats.All(s=>s==null||s.Balance<=NightStud.Shared.WagerRules.MaxTableBalance),"cash_table_limit","有座位余额已超过四百万信用点，请离桌结算后重新入座。");
        if(ready)Need(player.Balance>=OnlineRound.Required(room.Mode,room.Ante,OwnSeat(room,session),room.Hands+1),"empty_stack","当前筹码不足以覆盖本局最高赔付，请重新组局或降低底分。");
        Need(!ready || player.Balance > 0, "empty_stack", "筹码用完了，可离桌后重新组局。");
        if (ready&&room.Seats.All(s => s is not null && (s==player||s.Ready) && Online(s.Session)))
        {
            int nextHand=room.Hands+1;
            for(int i=0;i<room.Seats.Length;i++)
                Need(room.Seats[i]!.Balance>=OnlineRound.Required(room.Mode,room.Ante,i,nextHand),"empty_stack","有座位筹码不足以覆盖本局最高赔付，请重新组局。");
            var initial=room.Seats.Select(s=>s!.Balance).ToArray();
            var game = new OnlineRound(room.Mode,room.Ante,nextHand,initial,new SecureRandom());
            if(room.TechEnabled)game.Kit=new TableTechHand();
            if(room.Economy=="store")cash!.BeginHand(room.LedgerId,initial,(db,tx)=>goods!.Begin(db,tx,room.LedgerId,nextHand,room.Seats.Select(s=>s!.Session.Id).ToArray()));
            for (int i=0;i<room.Seats.Length;i++){room.Initial[i]=initial[i];room.Seats[i]!.Ready=room.Seats[i]!.Session.Provider=="bot";}
            room.Game=game;room.Hands=nextHand;room.Result=null;
            Audit("hand_started",session,room);
            room.Events.Clear();
            room.Events.Add(new { kind = "deal", hand = room.Hands, round = 1 });
            for (int i = 0; i < room.Seats.Length; i++) room.Committed[i] = room.Initial[i] - game.Stack[i];
            room.NextBot=DateTime.UtcNow.AddMilliseconds(900);
            UpdateBalances(room);
        }
        else player.Ready=ready;
        room.Revision++;
        room.Touched = DateTime.UtcNow;
    }

    void Act(Room room, Session session, Command command)
    {
        var game = room.Game;
        Need(game is not null && !game.Done, "not_playing", "双方准备后发牌。");
        Need(room.Seats.All(s => s is not null && Online(s.Session)), "peer_offline", "对方掉线，正在保留座位。");
        int seat = OwnSeat(room, session);
        if(room.Economy=="store")Need(room.Seats[seat]!.SaveId==command.SaveId,"cash_scope","请切回入桌时的经营存档再操作。");
        Need(game!.Actor == seat, "out_of_turn", "还没轮到你。");
        Need(PendingAsset(room)==null,"asset_pending","正在核对押品，核对完成后继续出牌。");
        int round = game.Round;
        int payment = game.Play(command);
        game.Kit?.EndTurn(seat);
        Audit("player_action",session,room,command.Move);
        room.Committed[seat] += payment;
        if (room.Events.Count >= 128) room.Events.RemoveAt(0);
        room.Events.Add(new { kind = "action", seat, move = command.Move, amount = payment, round });
        UpdateBalances(room);
        room.Revision++;
        room.Touched = DateTime.UtcNow;
        room.NextBot=DateTime.UtcNow.AddMilliseconds(850);
    }

    void UpdateBalances(Room room)
    {
        var game = room.Game!;
        if(game.Done&&room.Economy=="store"){
            room.CashFault=true;
            var credit=game.CreditBalances();var winners=Enumerable.Range(0,credit.Length).Where(i=>credit[i]>room.Initial[i]).ToArray();
            try{cash!.FinishHand(room.LedgerId,credit,(db,tx)=>goods!.Finish(db,tx,room.LedgerId,room.Hands,game.Winner,room.Seats.Select(s=>s!.Session.Id).ToArray(),room.Seats.Select(s=>s!.SaveId).ToArray(),room.Seats.Length>2?winners:null));room.CashFault=false;}
            catch(Microsoft.Data.Sqlite.SqliteException){throw new WireFault("cash_storage","结算暂时无法写入，桌上信用点已保留，请稍后重试。");}
        }
        var balances=game.CreditBalances();
        for (int i = 0; i < room.Seats.Length; i++) if (room.Seats[i] is { } seat) seat.Balance = balances[i];
        if (game.Done)
            room.Result = new { winnerSeat = game.Winner, pot = game.FinalPot, showdown = game.Reveal,
                creditPot=Math.Max(0,game.FinalPot-game.AssetValue),assetValue=game.AssetValue,
                net = balances.Select((balance, seat) => balance - room.Initial[seat]).ToArray(), description=game.Description };
        if(game.Done)audit?.Invoke(new ServerEvent("hand_settled","",room.Code,room.Mode,"winner="+game.Winner));
        if (game.Stack.Sum() + game.Pot != room.Initial.Sum()+game.AssetValue) throw new InvalidOperationException("Chip ledger diverged");
    }

    void Leave(Room room, Session session, string reason)
    {
        int seat = OwnSeat(room, session);
        if (room.Game is { Done: false } game) { game.Forfeit(seat); UpdateBalances(room); }
        Need(!room.CashFault,"cash_settling","正在保存结算，请稍后离桌。");
        if(room.Economy=="store")goods!.ReturnUnplayed(room.LedgerId);
        if(room.Economy=="store"&&room.Seats[seat] is {CashHold.Length:>0} funded)cash!.Release(funded.CashHold,session.Id,funded.SaveId);
        Audit("room_left",session,room,reason);
        room.Events.Add(new { kind = reason, seat });
        room.Seats[seat] = null;
        if(room.HostId==session.Id)room.HostId=room.Seats.FirstOrDefault(s=>s!=null&&s.Session.Provider!="bot")?.Session.Id??"";
        session.RoomCode = null;
        room.Game = null;
        foreach (var other in room.Seats.Where(s => s is not null)) other!.Ready = other.Session.Provider=="bot";
        room.Revision++;
        room.Touched = DateTime.UtcNow;
        if (room.Seats.All(s => s is null||s.Session.Provider=="bot")) rooms.Remove(room.Code); else Broadcast(room);
    }

    void SendLobby(Session session) => session.Client?.Send(new { type = "state", room = (object?)null, playerId = session.Id });
    void SendState(Session session) { if (Current(session) is { } room) session.Client?.Send(View(room, session)); else SendLobby(session); }
    void Broadcast(Room room)
    {
        foreach (var seat in room.Seats) seat?.Session.Client?.Send(View(room, seat.Session));
    }

    object View(Room room, Session session)
    {
        int own = OwnSeat(room, session);
        var game = room.Game;
        bool paused = game is { Done: false } && room.Seats.Any(s => s is null || !Online(s.Session));
        var pending=PendingAsset(room);
        return new { type = "state", playerId = session.Id, room = new {
            code = room.Code, revision = room.Revision, ownSeat = own, ante = room.Ante, buyIn = room.BuyIn,tableId=room.LedgerId,
            techEnabled=room.TechEnabled,canSetTech=room.HostId==session.Id && SupportsTech(room) && (game==null||game.Done)
                && room.Seats.All(seat=>seat==null||CanPlayTech(seat.Session,room)),
            tech=game?.Kit?.View(game,own,paused),
            game=room.Mode, capacity=room.Seats.Length, hostId=room.HostId,economy=room.Economy,goods=GoodsView(room),
            assetPending=pending==null?null:new{id=pending.Id,seat=pending.Seat,expires=pending.Expires},
            assetRules=game is{Done:false,SupportsAssetBets:true}&&AssetsCompatible(room)?new{actor=game.Actor,due=game.Due,minimumBet=game.Unit,minimumRaise=game.MinimumRaise,cash=game.Stack[game.Actor],other=game.Stack[1-game.Actor]}:null,
            goodsEnabled=room.Seats.Length==2||AssetsCompatible(room),assetValue=game?.AssetValue??0,creditPot=Math.Max(0,(game?.Pot??0)-(game?.AssetValue??0)),
            detail=game?.Detail(own), name=room.Name, isPublic=room.Public, board=game?.Board??Array.Empty<int>(), dealerSeat=game?.DealerSeat??-1,
            phase = room.CashFault?"settling":paused ? "paused" : game is null ? "waiting" : game.Done ? "finished" : "playing",
            hand = room.Hands, round = game?.Round ?? 0, actor = game is { Done: false } ? game.Actor : -1,
            pot = game?.Pot ?? 0, unit = game?.Unit ?? Math.Max(1, room.Ante / 2) * 5,
            players = room.Seats.Select((s, i) => s is null ? null : new {
                id = s.Session.Id, name = s.Session.Name, seat = i, stack = s.Balance, ready = s.Ready, bot=s.Session.Provider=="bot",
                online = Online(s.Session), reconnectUntil = s.Session.Disconnected?.AddSeconds(GraceSeconds),
                funded=room.Economy=="practice"||s.CashHold.Length>0,saveId=i==own?s.SaveId:null,
                committed = room.Committed[i], street = game?.Street[i] ?? 0,
                cards = game?.VisibleCards(own,i) ?? Array.Empty<int?>()
            }).ToArray(),
            legalMoves = game is { Done: false } && game.Actor == own && !paused && pending==null
                ? game.Legal().Select(m=>new{move=m.Move,amount=m.Amount,max=m.Max,choice=m.Choice}).ToArray() : Array.Empty<object>(),
            result = room.Result, events = room.Events.ToArray()
        }};
    }

    object DirectoryView(Session viewer)=>new {type="lobby",selfId=viewer.Id,selfPhone=phones?.Number(viewer.AccountId)??"",selfAppearance=viewer.Avatar,features=FeaturesFor(viewer),totalOnline=sessions.Values.Count(Online),filter=viewer.PlayerFilter,privacy=viewer.Privacy,
        players=sessions.Values.Where(Online).Where(s=>viewer.PlayerFilter=="all"||(s.Provider=="steam")== (viewer.PlayerFilter=="steam")).Take(100).Select(s=>new{id=s.Id,name=s.Name,number=phones?.Number(s.AccountId)??"",provider=s.Provider,avatar=s.Avatar,visitsEnabled=s.Privacy.AllowVisits,allowVisits=s.Privacy.AllowVisits&&shops.ContainsKey(s.Id)&&HostHours(s)=="",shopHours=s.Privacy.AllowVisits?HostHours(s):"",allowInvites=s.Privacy.AllowInvites,status=s.RoomCode==null?"lobby":Current(s)?.Game is {Done:false}?"playing":"room"}).ToArray(),
        rooms=rooms.Values.Where(r=>r.Public).Take(64).Select(r=>new{code=r.Code,name=r.Name,game=r.Mode,economy=r.Economy,techEnabled=r.TechEnabled,seats=r.Seats.Count(s=>s!=null),capacity=r.Seats.Length,bots=r.Seats.Count(s=>s?.Session.Provider=="bot"),canJoin=(!r.TechEnabled||CanPlayTech(viewer,r))&&(r.Game is null||r.Game.Done)&&r.Seats.Any(s=>s==null||s.Session.Provider=="bot"),playing=r.Game is {Done:false},ante=r.Ante,buyIn=r.BuyIn}).ToArray(),
        invitations=invitations.Values.Where(i=>i.To==viewer.Id&&i.Expires>DateTime.UtcNow&&rooms.TryGetValue(i.Code,out var invited)&&(!invited.TechEnabled||CanPlayTech(viewer,invited))).Take(8).Select(i=>new{id=i.Id,fromId=i.From,fromName=sessions.Values.FirstOrDefault(s=>s.Id==i.From)?.Name??"桌友",code=i.Code,game=rooms[i.Code].Mode,techEnabled=rooms[i.Code].TechEnabled,name=rooms[i.Code].Name,expiresAt=i.Expires}).ToArray()};
    void SendDirectory(Session viewer)=>viewer.Client?.Send(DirectoryView(viewer));
    void BroadcastLobby(){foreach(var session in sessions.Values.Where(Online))SendDirectory(session);}
    void Invite(Session sender,string targetId){
        var room=Current(sender)??throw new WireFault("no_room","先开房间再邀请。 ");
        Need((room.Game is null||room.Game.Done)&&room.Seats.Any(s=>s==null||s.Session.Provider=="bot"),"room_full","本局结束或有空位时可以邀请。 ");
        var target=sessions.Values.FirstOrDefault(s=>s.Id==targetId&&Online(s));
        Need(target!=null&&target!=sender,"bad_target","这位玩家已离线。 ");
        Need(!room.TechEnabled||CanPlayTech(target!,room),"tech_client","对方的客户端还不支持科技桌。");
        Need(target!.Privacy.AllowInvites,"invitations_disabled","对方关闭了牌局邀请。");
        string pair=sender.Id+targetId;var now=DateTime.UtcNow;
        Need(!inviteCooldown.TryGetValue(pair,out var previous)||(now-previous).TotalSeconds>=10,"invite_rate","刚发过邀请，等对方回应。 ");
        Need(invitations.Count<1000,"server_busy","邀请暂满，请稍后再试。 ");
        inviteCooldown[pair]=now;
        foreach(var old in invitations.Values.Where(i=>i.From==sender.Id&&i.To==targetId).ToArray())invitations.Remove(old.Id);
        var invitation=new Invitation(Guid.NewGuid().ToString("N"),sender.Id,targetId,room.Code,now.AddMinutes(2));invitations.Add(invitation.Id,invitation);
        Audit("invitation_sent",sender,room);
    }
    void AcceptInvite(Session session,Command command){
        Need(invitations.TryGetValue(command.Invitation,out var invitation)&&invitation.To==session.Id&&invitation.Expires>DateTime.UtcNow,"invite_expired","邀请已过期。 ");
        Join(session,new Command{Code=invitation!.Code});invitations.Remove(invitation.Id);Audit("invitation_accepted",session,Current(session));
    }
    void DeclineInvite(Session session,string id){
        if(invitations.TryGetValue(id,out var invitation)&&invitation.To==session.Id)invitations.Remove(id);
    }

    public void Expire()
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            ExpireContacts(now);
            ExpireShopInquiries(now);
            ExpireCounters(now);
            foreach(var session in sessions.Values.Where(s=>Online(s)&&moderation?.Banned(s.Id)==true).ToArray())session.Client?.Stop();
            foreach(var owner in shops.Keys.Where(id=>FindPlayer(id) is not {} s||!Online(s)||(now-shops[id].Updated).TotalSeconds>30).ToArray()){
                shops.Remove(owner);DropSharedShop(owner);foreach(string visitor in visiting.Where(p=>p.Value==owner).Select(p=>p.Key).ToArray())if(FindPlayer(visitor) is {} guest)LeaveVisit(guest);
            }
            foreach(string visitor in visiting.Keys.Where(id=>FindPlayer(id) is not {} s||!Online(s)).ToArray())if(FindPlayer(visitor) is {} guest)LeaveVisit(guest);
            int before=rooms.Count+invitations.Count+rooms.Values.Sum(r=>r.Seats.Count(s=>s!=null));
            foreach(var invitation in invitations.Values.Where(i=>i.Expires<=now || !rooms.ContainsKey(i.Code)).ToArray())invitations.Remove(invitation.Id);
            foreach(var pair in inviteCooldown.Where(p=>(now-p.Value).TotalMinutes>5).ToArray())inviteCooldown.Remove(pair.Key);
            foreach (var room in rooms.Values.ToArray())
            {
                ExpireAsset(room,now);
                if(room.CashFault){try{UpdateBalances(room);Broadcast(room);}catch(WireFault){continue;}}
                TickBot(room,now);
                if (room.Game is { Done: false } game && room.Seats.Where(s=>s!=null&&s.Session.Provider!="bot").All(s=>!Online(s!.Session)&&s.Session.Disconnected is { } at && (now-at).TotalSeconds>=GraceSeconds))
                { game.VoidHand(); UpdateBalances(room); }
                foreach (var seat in room.Seats.ToArray())
                    if (seat is not null && !Online(seat.Session) && seat.Session.Disconnected is { } since && (now - since).TotalSeconds >= GraceSeconds)
                        Leave(room, seat.Session, "disconnect_timeout");
                if (rooms.ContainsKey(room.Code) && (now - room.Touched).TotalMinutes > 30 && (room.Game is null || room.Game.Done))
                    foreach (var seat in room.Seats.ToArray()) if (seat is not null) { Leave(room, seat.Session, "idle_timeout"); SendLobby(seat.Session); }
            }
            foreach (var session in sessions.Values.Where(s => !Online(s) && s.RoomCode is null && (now - s.LastSeen).TotalHours >= 2).ToArray())
                sessions.Remove(session.Token);
            if(before!=rooms.Count+invitations.Count+rooms.Values.Sum(r=>r.Seats.Count(s=>s!=null)))BroadcastLobby();
        }
    }
}

