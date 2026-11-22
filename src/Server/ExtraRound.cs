using NightStud;

namespace NightStud.Server;

// State and legal actions stay on the server; only the recipient's hand is sent.
internal sealed class ExtraRound
{
    readonly string mode;
    readonly int ante,number;
    readonly int[] initial;
    readonly Random rng;
    readonly Landlord? landlord;
    readonly Mahjong? mahjong;
    readonly int[] dice=new int[3];
    bool wagered,odd;
    int actor;
    public int[] Stack {get;}
    public int[] Street {get;}
    public List<int>[] Hand {get;}
    public int Pot {get;private set;}
    public int FinalPot {get;private set;}
    public int Winner {get;private set;}=-1;
    public bool Done {get;private set;}
    public bool Reveal=>Done;
    public string Description {get;private set;}="";
    public int Round=>landlord!=null?(landlord.Boss<0?1:2):mahjong!=null?mahjong.DiscardCount+1:1;
    public int Actor=>Done?-1:landlord?.Turn??mahjong?.OnlineActor??actor;
    public int Dealer=>mode=="dice"?number%2:mahjong?.Dealer??-1;
    public ExtraRound(string mode,int ante,int number,int[] balances,Random rng){
        this.mode=mode;this.ante=ante;this.number=number;this.rng=rng;
        initial=balances.ToArray();Stack=balances.ToArray();Street=new int[balances.Length];
        if(mode=="landlord"){landlord=new Landlord(rng);Hand=landlord.Hands;}
        else if(mode=="mahjong"){mahjong=new Mahjong(rng,seat=>"座位 "+(seat+1),true);Hand=mahjong.Hands;mahjong.Visuals.Clear();}
        else{Hand=new[]{new List<int>(),new List<int>()};actor=1-Dealer;}
    }
    public BetOption[] Legal(){
        if(Done)return Array.Empty<BetOption>();
        if(landlord!=null)return landlord.Boss<0?new[]{new BetOption("bid"),new BetOption("decline")}:
            landlord.Last.Length==0?new[]{new BetOption("play")}:new[]{new BetOption("play"),new BetOption("pass")};
        if(mahjong!=null){
            var m=mahjong;int s=Actor;var options=new List<BetOption>();
            if(m.Reply){
                if(m.OnlineRon(s))options.Add(new("win"));
                if(m.OnlinePong(s))options.Add(new("pung"));
                if(m.OnlineKong(s))options.Add(new("kong",Choice:m.Last));
                foreach(int chow in m.OnlineChows(s))options.Add(new("chow",Choice:chow));
                options.Add(new("pass"));
            }else{
                if(m.SelfWin)options.Add(new("selfWin"));
                foreach(int tile in m.OwnKongs(s))options.Add(new("kong",Choice:tile));
                options.Add(new("discard"));
            }
            return options.ToArray();
        }
        return wagered?new[]{new BetOption("reveal")}:new[]{new BetOption("odd",ante),new BetOption("even",ante)};
    }
    public int Play(Command command){
        if(command.Amount.HasValue)throw new WireFault("bad_amount","这桌按约定底分结算，无需填写下注金额。");
        var legal=Legal().FirstOrDefault(o=>o.Move==command.Move&&(o.Move=="discard"||o.Choice==command.Choice))
            ??throw new WireFault("illegal_move","当前无法这样操作。");
        if(landlord!=null){
            if(legal.Move is "bid" or "decline")landlord.Bid(legal.Move=="bid");
            else{
                int[] cards=command.Cards??Array.Empty<int>();
                if(legal.Move=="pass")cards=Array.Empty<int>();
                if(cards.Length>20||legal.Move=="play"&&cards.Length==0||!landlord.Play(cards))throw new WireFault("bad_cards","所选牌型无效，或压不过上一手。");
            }
            if(landlord.Winner>=0)FinishLandlord(landlord.Winner);
        }else if(mahjong!=null){
            var m=mahjong;bool valid=true;
            if(m.Reply){
                int action=legal.Move switch{"win"=>Mahjong.Win,"pung"=>Mahjong.Pung,"kong"=>Mahjong.Kong,"chow"=>legal.Choice!.Value,_=>Mahjong.Pass};
                valid=m.OnlineRespond(Actor,action);
            }else if(legal.Move=="selfWin")m.WinSelf();
            else if(legal.Move=="kong")m.OwnKong(legal.Choice!.Value);
            else valid=command.Choice is >=0 and <34&&m.Discard(command.Choice.Value);
            if(!valid)throw new WireFault("bad_tile","请选择自己手中可打出的牌。");
            m.Visuals.Clear();
            if(m.Done)FinishMahjong();
        }else if(!wagered){
            odd=legal.Move=="odd";wagered=true;
            for(int i=0;i<2;i++){Stack[i]-=ante;Street[i]=ante;Pot+=ante;}
            for(int i=0;i<3;i++)dice[i]=rng.Next(1,7);actor=Dealer;return ante;
        }else{
            int winner=(dice.Sum()%2==1)==odd?1-Dealer:Dealer;
            Winner=winner;FinalPot=Pot;Stack[winner]+=Pot;Pot=0;Done=true;
            Description="骰子 "+string.Join("、",dice)+"，合计 "+dice.Sum()+" 点（"+(dice.Sum()%2==1?"单":"双")+"）。猜中收下双方押注。";
        }
        return 0;
    }
    void FinishLandlord(int winner){
        int boss=landlord!.Boss;Winner=winner;bool bossWins=winner==boss;
        for(int i=0;i<3;i++)Stack[i]+=i==boss?(bossWins?2:-2)*ante:(bossWins?-1:1)*ante;
        FinalPot=4*ante;Done=true;
        Description=(bossWins?"地主胜":"农民胜")+"；固定底分 "+ante+"，地主两份、农民各一份。炸弹不翻倍。";
    }
    void FinishMahjong(){
        var m=mahjong!;Winner=m.Winner;Done=true;
        if(m.Winners.Count==0){Description="流局，本局不收付。";return;}
        var lines=new List<string>();
        foreach(int s in m.Winners){
            var bill=m.Score(s);int unit=ante*bill.Multiplier;
            if(m.From<0)for(int i=0;i<4;i++){if(i==s)continue;Stack[i]-=unit;Stack[s]+=unit;}
            else{Stack[m.From]-=unit;Stack[s]+=unit;}
            lines.Add("座位 "+(s+1)+"："+string.Join("、",bill.Lines.Select(x=>x.name+" "+x.fan+"番"))+"；"+Math.Min(5,bill.Fan)+"番，×"+bill.Multiplier);
        }
        FinalPot=Stack.Select((n,i)=>Math.Max(0,n-initial[i])).Sum();
        Description=string.Join("\n",lines)+(m.From<0?"\n自摸三家各付。":"\n点炮者付；可一炮多响。");
    }
    public int?[] Visible(int viewer,int seat)=>Hand[seat].Select(c=>seat==viewer||Done?(int?)c:null).ToArray();
    public object View(int viewer){
        if(landlord is {} l)return new{boss=l.Boss,leader=l.Leader,last=l.Last,bottom=l.Boss>=0?l.Bottom.Select(c=>(int?)c).ToArray():new int?[3],note=l.Note};
        if(mahjong is {} m)return new{left=m.Left,dealer=m.Dealer,from=m.From,last=m.Last,lastClaimed=m.LastClaimed,reply=m.Reply,
            drawn=m.Turn==viewer&&!m.Reply?m.Drawn:-1,rivers=m.Rivers.Select(r=>r.ToArray()).ToArray(),
            melds=m.Melds.Select((set,s)=>set.Select(t=>s!=viewer&&!Done&&m.Concealed(s).Contains(t)?-1:t).ToArray()).ToArray(),
            sizes=Enumerable.Range(0,4).Select(m.MeldSizes).ToArray(),
            waits=m.Hands[viewer].Count%3==1?m.OnlineWaits(viewer):Array.Empty<int>(),
            waitPlans=m.WaitPlans(viewer),
            discardChoices=!Done&&!m.Reply&&m.Turn==viewer?m.OnlineDiscardChoices():Array.Empty<int>(),
            winners=m.Winners.ToArray(),note=m.Note};
        return new{dealer=Dealer,wagered,bet=wagered?(odd?"odd":"even"):"",faces=Done?dice:Array.Empty<int>()};
    }
    public Command Suggest(double style=0){
        if(landlord!=null)landlord.SeatStyle[Actor]=style;
        if(mahjong!=null)mahjong.SeatStyle[Actor]=style;
        if(landlord is {} l){
            if(l.Boss<0)return new(){Move=l.BotWantsBid()?"bid":"decline"};
            int[] move=l.BotPlay();
            return new(){Move=move.Length>0?"play":"pass",Cards=move};
        }
        var m=mahjong!;var legal=Legal();
        var win=legal.FirstOrDefault(o=>o.Move is "win" or "selfWin");if(win!=null)return new(){Move=win.Move};
        if(m.Reply){
            int claim=m.OnlineBotClaim();
            return claim switch{
                Mahjong.Kong=>new(){Move="kong",Choice=m.Last},
                Mahjong.Pung=>new(){Move="pung"},
                >=0=>new(){Move="chow",Choice=claim},
                _=>new(){Move="pass"}
            };
        }
        int discard=m.OnlineSuggestion(),kong=m.BotOwnKong(Actor,discard);
        return kong>=0?new(){Move="kong",Choice=kong}:new(){Move="discard",Choice=discard};
    }
    public void Forfeit(int seat){
        if(Done)return;
        if(landlord is {} l&&l.Boss>=0)FinishLandlord(seat==l.Boss?(seat+1)%3:l.Boss);
        else if(mode=="dice"&&wagered){Winner=1-seat;FinalPot=Pot;Stack[Winner]+=Pot;Pot=0;Done=true;Description="对方离桌，本手判负。";}
        else VoidHand();
    }
    public void VoidHand(){if(Done)return;Array.Copy(initial,Stack,Stack.Length);Pot=0;Done=true;Winner=-1;Description="本局中止，退回本局预留。";}
}
