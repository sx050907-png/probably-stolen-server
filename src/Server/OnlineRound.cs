using NightStud;

namespace NightStud.Server;

public sealed record BetOption(string Move,int Amount=0,int Max=0,int? Choice=null);

// All draws, legal actions and payouts are owned by the server.
public sealed class OnlineRound
{
    readonly Table? stud;
    readonly ExtraRound? extra;
    readonly Random random;
    readonly Stack<int> shoe=new();
    readonly List<int>[] hands={new(),new()};
    readonly int[] stack=new int[2],street=new int[2],paid=new int[2];
    readonly int[] assetValue=new int[2];
    readonly List<int> board=new();
    int pot,round=1,actor,actions,winner=-1,finalPot,lastRaise;
    bool done,reveal;
    public string Mode { get; }
    public int Ante { get; }
    public int Number { get; }
    public int[] Stack=>extra?.Stack??stud?.Stack??stack;
    public int[] Street=>extra?.Street??stud?.Street??street;
    public List<int>[] Hand=>extra?.Hand??stud?.Hand??hands;
    public int Pot=>extra?.Pot??stud?.Pot??pot;
    public int Round=>extra?.Round??stud?.Round??round;
    public int Actor=>extra?.Actor??stud?.Actor??actor;
    public int Winner=>extra?.Winner??stud?.Winner??winner;
    public int FinalPot=>extra?.FinalPot??stud?.FinalPot??finalPot;
    public bool Done=>extra?.Done??stud?.Done??done;
    public bool Reveal=>extra?.Reveal??stud?.Reveal??reveal;
    public int Unit=>stud?.Unit??(Mode=="holdem"&&round>2?Ante*2:Ante);
    public bool SupportsAssetBets=>Mode is "stud" or "three" or "holdem";
    public int MinimumRaise=>Due+(stud!=null?stud.Unit:lastRaise);
    public int AssetValue=>assetValue.Sum();
    public NightStud.Shared.AssetBetPlan QuoteAsset(int quote){
        if(Done||!SupportsAssetBets)throw new WireFault("asset_phase","这桌当前没有加押实物的回合。");
        NightStud.Shared.AssetBetPlan plan;
        try{plan=NightStud.Shared.AssetBetRules.Plan(quote,Stack[Actor],Stack[1-Actor],Due,Unit,MinimumRaise);}
        catch(ArgumentException e){throw new WireFault("asset_amount",e.Message);}
        int old=Stack[Actor];Stack[Actor]=checked(old+quote);
        try{var legal=Legal().FirstOrDefault(o=>o.Move==plan.Move);if(legal==null||plan.Move is "bet" or "raise"&&(plan.Amount<legal.Amount||plan.Amount>legal.Max)||plan.Move is "call" or "allIn"&&plan.Amount!=legal.Amount)throw new WireFault("asset_amount","这件货与当前下注金额不匹配。");}
        finally{Stack[Actor]=old;}
        return plan;
    }
    public int PlayAsset(int seat,int quote,string move,int amount){
        if(seat!=Actor)throw new WireFault("out_of_turn","还没轮到你。");
        var plan=QuoteAsset(quote);if(plan.Move!=move||plan.Amount!=amount)throw new WireFault("asset_changed","牌桌金额已变，请重新选择押品。");
        Stack[seat]=checked(Stack[seat]+quote);assetValue[seat]=checked(assetValue[seat]+quote);
        return Play(move,move is "bet" or "raise"?amount:null);
    }
    public int[] CreditBalances(){
        var result=Stack.ToArray();if(!Done)return result;
        if(Winner<0){for(int i=0;i<assetValue.Length&&i<result.Length;i++)result[i]-=assetValue[i];}
        else result[Winner]-=AssetValue;
        if(result.Any(n=>n<0))throw new InvalidOperationException("Physical backing exceeds cash settlement");return result;
    }
    public int Due=>stud?.Due??Math.Max(0,street[1-actor]-street[actor]);
    public int Limit=>stud?.Limit??Math.Min(stack[actor],stack[1-actor]+Due);
    public int DealerSeat=>extra?.Dealer??(Mode=="blackjack"?Number%2:-1);
    public int[] Board=>board.ToArray();
    string description="";
    public string Description {get=>extra?.Description??description;private set=>description=value;}
    public object? Detail(int viewer)=>extra?.View(viewer);
    public Command Suggest()=>extra?.Suggest()??throw new InvalidOperationException("Bots are available on group tables");
    public static readonly string[] Games={"stud","three","holdem","blackjack","landlord","mahjong","dice"};
    public static bool Supported(string mode)=>Games.Contains(mode);
    public static int Capacity(string mode)=>mode=="landlord"?3:mode=="mahjong"?4:2;
    public static int Required(string mode,int ante,int seat,int number)=>mode=="mahjong"?ante*48:mode=="blackjack"&&seat==number%2?ante+ante/2:mode=="landlord"?ante*2:ante;
    public static string Title(string mode)=>mode switch{"landlord"=>"斗地主","mahjong"=>"货栈麻将","dice"=>"骰盅 · 轮庄桌","three"=>"炸金花","holdem"=>"德州扑克 · 底注桌","blackjack"=>"二十一点 · 轮庄桌",_=>"五张梭哈"};

    public OnlineRound(string mode,int ante,int number,int[] balances,Random rng)
    {
        if(!Supported(mode)||balances.Length!=Capacity(mode)||ante<1||balances.Where((n,s)=>n<Required(mode,ante,s,number)).Any())throw new ArgumentException("Invalid table rules");
        Mode=mode;Ante=ante;Number=number;random=rng;
        if(mode is "landlord" or "mahjong" or "dice"){extra=new ExtraRound(mode,ante,number,balances,rng);return;}
        if(mode=="stud"){
            stud=new Table(rng){Ante=ante,Number=number-1,Multiplier=Math.Max(1,ante/2)};
            Array.Copy(balances,stud.Stack,2);stud.Start();return;
        }
        Array.Copy(balances,stack,2);
        var cards=Enumerable.Range(0,52).ToArray();
        for(int i=51;i>0;i--){int j=random.Next(i+1);(cards[i],cards[j])=(cards[j],cards[i]);}
        foreach(int card in cards)shoe.Push(card);
        for(int i=0;i<(mode=="three"?3:2);i++)for(int s=0;s<2;s++)hands[s].Add(shoe.Pop());
        if(mode=="blackjack"){
            int dealer=DealerSeat,player=1-dealer,reserve=ante+ante/2;
            if(stack[player]<ante||stack[dealer]<reserve)throw new ArgumentException("Blackjack reserve unavailable");
            Pay(player,ante);Pay(dealer,reserve);Array.Clear(street);actor=player;
            bool pn=Points(hands[player])==21,dn=Points(hands[dealer])==21;
            if(pn||dn)FinishBlackjack(pn&&dn?-1:pn?player:dealer,pn&&!dn);
        }else{
            int amount=Math.Min(ante,stack.Min());Pay(0,amount);Pay(1,amount);Array.Clear(street);actor=number%2;lastRaise=ante;
            if(stack.Min()==0)Showdown();
        }
    }
    void Pay(int seat,int amount){if(amount<0||amount>stack[seat])throw new InvalidOperationException("Invalid payment");stack[seat]-=amount;street[seat]+=amount;paid[seat]+=amount;pot+=amount;}
    public BetOption[] Legal()
    {
        if(Done)return Array.Empty<BetOption>();
        if(extra!=null)return extra.Legal();
        if(stud!=null)return Enum.GetValues<Move>().Where(stud.Legal).Select(m=>{
            int amount=m switch{Move.Call=>stud.Due,Move.AllIn=>stud.Limit,Move.Bet=>stud.Unit,Move.Raise=>stud.Due+stud.Unit,_=>0};
            return new BetOption(m switch{Move.AllIn=>"allIn",_=>m.ToString().ToLowerInvariant()},amount,m is Move.Bet or Move.Raise?stud.Limit:0);
        }).ToArray();
        if(Mode=="blackjack")return new[]{new BetOption("hit"),new BetOption("stand")};
        var list=new List<BetOption>();
        if(Due==0 && Mode!="three")list.Add(new("check"));
        if(Due>0)list.Add(new("call",Due));
        if(Due==0 && street[actor]==0 && Limit>=Unit)list.Add(new("bet",Unit,Limit));
        if(Due>0 && Limit>=Due+lastRaise)list.Add(new("raise",Due+lastRaise,Limit));
        if(Mode=="three")list.Add(new("showdown",Due));
        if(Limit>0)list.Add(new("allIn",Limit));list.Add(new("fold"));return list.ToArray();
    }
    public int Play(string move,int? amount)
    {
        var option=Legal().SingleOrDefault(o=>o.Move==move)??throw new WireFault("illegal_move","当前无法这样操作。");
        if(amount.HasValue && (move is not ("bet" or "raise") || amount<option.Amount || amount>option.Max))throw new WireFault("bad_amount","下注金额超出当前范围。");
        int payment=amount??option.Amount;
        if(stud!=null){stud.Play(move switch{"check"=>Move.Check,"bet"=>Move.Bet,"call"=>Move.Call,"raise"=>Move.Raise,"fold"=>Move.Fold,_=>Move.AllIn},amount);return payment;}
        if(Mode=="blackjack"){
            if(move=="hit")hands[actor].Add(shoe.Pop());
            if(Points(hands[actor])>21)FinishBlackjack(DealerSeat,false);
            else if(move=="stand" || Points(hands[actor])==21){
                while(Points(hands[DealerSeat])<17)hands[DealerSeat].Add(shoe.Pop());
                int mine=Points(hands[actor]),theirs=Points(hands[DealerSeat]);
                FinishBlackjack(theirs>21?actor:mine==theirs?-1:mine>theirs?actor:DealerSeat,false);
            }
            return 0;
        }
        if(move=="fold"){Settle(1-actor,false);return 0;}
        int due=Due;Pay(actor,payment);
        if(move=="showdown"){Showdown();return payment;}
        if(payment>due)lastRaise=payment-due;
        actions++;actor=1-actor;
        if(actions<2 || street[0]!=street[1])return payment;
        if(stack.Min()==0 || round==(Mode=="three"?3:4))Showdown();
        else{round++;Array.Clear(street);actions=0;actor=Number%2;lastRaise=Unit;if(Mode=="holdem")DealBoard();}
        return payment;
    }
    public int Play(Command command)=>extra!=null?extra.Play(command):Play(command.Move,command.Amount);
    void DealBoard(){int target=round==1?0:round==2?3:round==3?4:5;while(board.Count<target)board.Add(shoe.Pop());}
    void Showdown(){
        if(Mode=="holdem"){round=4;DealBoard();}
        int a=Mode=="three"?ThreeScore(hands[0]):BestFive(hands[0].Concat(board));
        int b=Mode=="three"?ThreeScore(hands[1]):BestFive(hands[1].Concat(board));
        Description=Mode=="three"?"三条＞同花顺＞同花＞顺子＞对子＞散牌；同型比较点数。":"从两张底牌与五张公共牌中选最大的五张比较。";
        Settle(a==b?-1:a>b?0:1,true);
    }
    void FinishBlackjack(int winnerSeat,bool natural){
        int dealer=DealerSeat;
        if(winnerSeat<0){Description="平局，退回双方本手押注与庄家预留。";VoidHand();reveal=true;return;}
        if(!natural){int refund=paid[dealer]-Ante;stack[dealer]+=refund;paid[dealer]-=refund;pot-=refund;}
        Description=natural?"闲家起手两张 21 点，按 3:2 赔付。":"闲家 "+Points(hands[1-dealer])+" 点，庄家 "+Points(hands[dealer])+" 点；庄家未满 17 点补牌，软 17 停牌。";
        Settle(winnerSeat,true);
    }
    void Settle(int seat,bool show){winner=seat;finalPot=pot;reveal=show;done=true;if(seat<0){stack[0]+=pot/2;stack[1]+=pot-pot/2;}else stack[seat]+=pot;pot=0;}
    public void Forfeit(int seat){if(Done)return;if(extra!=null){extra.Forfeit(seat);return;}if(stud!=null){stud.Forfeit(seat);return;}if(Mode=="blackjack"){FinishBlackjack(1-seat,false);Description="对方离桌，本手按判负结算。";}else Settle(1-seat,false);}
    public void VoidHand(){if(Done)return;if(extra!=null){extra.VoidHand();return;}if(stud!=null){stud.VoidHand();return;}winner=-1;finalPot=pot;for(int i=0;i<2;i++)stack[i]+=paid[i];pot=0;done=true;}
    public int?[] VisibleCards(int viewer,int seat)=>extra?.Visible(viewer,seat)??Hand[seat].Select((card,index)=>(int?)(seat==viewer||Reveal||Mode=="stud"&&index>0||Mode=="blackjack"&&(seat!=DealerSeat||index>0)?card:null)).ToArray();
    public static int Points(IEnumerable<int> cards){int total=0,aces=0;foreach(int c in cards){int r=Cards.Rank(c);if(r==14){aces++;total+=11;}else total+=Math.Min(10,r);}while(total>21&&aces-->0)total-=10;return total;}
    public static int ThreeScore(IEnumerable<int> hand){
        var cards=hand.ToArray();var ranks=cards.Select(Cards.Rank).OrderDescending().ToArray();bool flush=cards.All(c=>c/13==cards[0]/13);
        bool wheel=ranks.SequenceEqual(new[]{14,3,2}),straight=ranks.Distinct().Count()==3&&(ranks[0]-ranks[2]==2||wheel);
        var groups=ranks.GroupBy(r=>r).OrderByDescending(g=>g.Count()).ThenByDescending(g=>g.Key).ToArray();
        int category=groups[0].Count()==3?5:flush&&straight?4:flush?3:straight?2:groups[0].Count()==2?1:0;
        int[] scoreRanks=straight?new[]{wheel?3:ranks[0],0,0}:groups.SelectMany(g=>Enumerable.Repeat(g.Key,g.Count())).ToArray();
        return ((category*15+scoreRanks[0])*15+scoreRanks[1])*15+scoreRanks[2];
    }
    public static int BestFive(IEnumerable<int> source){var c=source.ToArray();int best=-1;for(int a=0;a<c.Length-4;a++)for(int b=a+1;b<c.Length-3;b++)for(int d=b+1;d<c.Length-2;d++)for(int e=d+1;e<c.Length-1;e++)for(int f=e+1;f<c.Length;f++)best=Math.Max(best,Cards.Score(new[]{c[a],c[b],c[d],c[e],c[f]}));return best;}
}
