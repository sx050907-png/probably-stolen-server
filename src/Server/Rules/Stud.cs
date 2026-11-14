using System;

using System.Collections.Generic;

using System.Linq;



namespace NightStud

{

    // Card ids are suit * 13 + rank, starting with the two.

    public static class Cards

    {

        public static int Rank(int card) => card % 13 + 2;

        public static string Face(int card) =>

            new[] { "梅花", "方块", "红桃", "黑桃" }[card / 13] +

            "  " + new[] { "2","3","4","5","6","7","8","9","10","J","Q","K","A" }[card % 13];



        public static readonly string[] Names = { "高牌","一对","两对","三条","顺子","同花","葫芦","四条","同花顺" };

        public const int Scale = 759375; // 15^5, enough room for all five kickers

        public static string Describe(IEnumerable<int> cards)
        {
            int score=Score(cards),category=score/Scale;
            int rank=(score%Scale)/(Scale/15);
            string face=rank==14?"A":rank==13?"K":rank==12?"Q":rank==11?"J":rank.ToString();
            if(category==0)return face+" 最大（高牌）";
            if(category==1)return "一对 "+face;
            if(category==3)return "三张 "+face;
            return Names[category];
        }

        public static int Score(IEnumerable<int> cards)

        {

            var hand = cards.ToArray();

            var groups = hand.GroupBy(Rank).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).ToArray();

            int top = groups[0].Key;

            bool straight = groups.Length == 5 && (top - groups[4].Key == 4 ||

                (top == 14 && groups[1].Key == 5));

            bool flush = hand.Length == 5 && hand.All(c => c / 13 == hand[0] / 13);

            int count = groups[0].Count();

            int second = groups.Length > 1 ? groups[1].Count() : 0;

            int category = straight && flush ? 8 : count == 4 ? 7 : count == 3 && second == 2 ? 6 :

                flush ? 5 : straight ? 4 : count == 3 ? 3 : second == 2 ? 2 : count == 2 ? 1 : 0;

            var ranks = straight ? new[] { top == 14 && groups[1].Key == 5 ? 5 : top } : groups.Select(g => g.Key).ToArray();

            int value = category;

            for (int i = 0; i < 5; i++) value = value * 15 + (i < ranks.Length ? ranks[i] : 0);

            return value;

        }

    }



    public enum Move { Check, Bet, Call, Raise, Fold, AllIn }



    public sealed class Table

    {

        readonly Random random;

        readonly int[] deck = Enumerable.Range(0,52).ToArray();

        int drawn, actions;

        public readonly List<int>[] Hand = { new List<int>(), new List<int>() };

        public readonly int[] Stack = { 100, 100 }, Street = { 0, 0 };

        public int Paid, Won, FinalPot, Winner;

        public string Opponent = "老岑";

        public double Aggression;

        public int Ante = 2, Multiplier = 1;

        public int Pot, Round, Actor, Number;

        public bool Done = true, Reveal;

        public string Receipt = "";

        public readonly List<string> History = new List<string>();

        public Table(Random rng) { random = rng; }

        public int Due => Street[1-Actor] - Street[Actor];

        // Heads-up effective stacks: an unmatched excess never leaves its owner.

        public int Limit => Math.Min(Stack[Actor], Stack[1-Actor] + Due);

        public int Unit => (Round <= 2 ? 5 : 10)*Multiplier;

        void Pay(int seat, int chips) { if (seat == 0) Paid += chips; Stack[seat] -= chips; Street[seat] += chips; Pot += chips; }

        void Deal() { foreach (var hand in Hand) hand.Add(deck[drawn++]); }

        int checkedRound;

        public int QuietRounds {get;private set;}

        public void Start()

        {

            if (!Done || Stack.Min() == 0) return;

            for (int i = 51; i > 0; i--) { int j = random.Next(i + 1); (deck[i],deck[j]) = (deck[j],deck[i]); }

            KnownHole=-1; Paid = Won = FinalPot = 0; checkedRound=QuietRounds=0; drawn = 0; Number++; Round = 1; Done = Reveal = false;

            History.Clear(); Receipt = "";

            foreach (var hand in Hand) hand.Clear();

            int ante = Math.Min(Ante, Stack.Min()); Pay(0,ante); Pay(1,ante);

            History.Add("双方各下底注 " + ante);

            Deal(); Deal(); OpenStreet();

        }

        void OpenStreet()

        {

            Street[0] = Street[1] = actions = 0;

            if (Stack.Min() == 0) { Showdown(); return; }

            int a = Cards.Score(Hand[0].Skip(1)), b = Cards.Score(Hand[1].Skip(1));

            Actor = a == b ? Number % 2 : a > b ? 0 : 1;

        }

        public bool Legal(Move move) => !Done && (move switch {

            Move.Check => Due == 0, Move.Call => Due > 0, Move.Bet => Due == 0 && Street[Actor] == 0 && Limit >= Unit,

            Move.Raise => Due > 0 && Limit >= Due + Unit, Move.AllIn => Limit > 0, Move.Fold => true, _ => false });

        public void Play(Move move,int? tender=null)

        {

            if (!Legal(move)) throw new InvalidOperationException("Illegal action");

            if(Actor==0 && move==Move.Check && checkedRound!=Round)

            {

                QuietRounds=checkedRound==Round-1 ? QuietRounds+1 : 1;checkedRound=Round;

            }

            if(Actor==0 && (move==Move.Bet || move==Move.Raise || move==Move.AllIn)){QuietRounds=checkedRound=0;}

            string who = Actor == 0 ? "你" : Opponent;

            if (move == Move.Fold) { History.Add(who + "弃牌"); Settle(1-Actor); return; }

            int due = Due;

            int paid = move switch { Move.Call => due, Move.AllIn => Limit, Move.Bet => Unit, Move.Raise => due + Unit, _ => 0 };

            if(tender.HasValue && tender.Value!=paid){

                if((move!=Move.Bet && move!=Move.Raise) || tender.Value<paid || tender.Value>Limit)throw new InvalidOperationException("Invalid wager");

                paid=tender.Value;

            }

            Pay(Actor,paid);

            string label = move switch { Move.Check => "过牌", Move.Call => "跟注", Move.Raise => "加注", Move.Bet => "下注", _ => "全押" };

            History.Add("第" + Round + "轮  " + who + label + (paid > 0 ? " +" + paid : ""));

            actions++; Actor = 1-Actor;

            if (actions < 2 || Street[0] != Street[1]) return;

            if (Round == 4 || Stack.Min() == 0) Showdown();

            else { Deal(); Round++; OpenStreet(); }

        }

        void Showdown()

        {

            while (Hand[0].Count < 5) Deal();

            Reveal = true;

            int cmp = Cards.Score(Hand[0]).CompareTo(Cards.Score(Hand[1]));

            Settle(cmp == 0 ? -1 : cmp > 0 ? 0 : 1);

        }

        void Settle(int winner)

        {

            Winner = winner; FinalPot = Pot; Won = winner < 0 ? Pot / 2 : winner == 0 ? Pot : 0;

            if (winner < 0) { Stack[0] += Pot / 2; Stack[1] += Pot / 2; Receipt = "平局，各收回 " + Pot/2; }

            else { Stack[winner] += Pot; Receipt = (winner == 0 ? "你收下" : Opponent + "收下") + "底池 " + Pot; }

            Pot = 0; Done = true;

        }

        public int KnownHole=-1;
        public void VoidHand()
        {
            if(Done)return;
            Winner=-1; FinalPot=Pot; Won=Paid;
            Stack[0]+=Paid;Stack[1]+=Pot-Paid;Pot=0;Done=true;
            Receipt="双验退场 · 各退原押";
        }
        public void Forfeit(int offender)
        {
            if(!Done)Settle(1-offender);
        }
        public int TopCard=>deck[drawn];
        public int PairCandidate(int index)
        {
            for(int i=drawn;i<deck.Length;i++)
                for(int j=0;j<Hand[0].Count;j++)
                    if(j!=index && Cards.Rank(deck[i])==Cards.Rank(Hand[0][j]))return i;
            return -1;
        }
        public void ExchangePair(int index)
        {
            int match=PairCandidate(index);
            if(match<0)throw new InvalidOperationException("没有同点牌");
            (deck[drawn],deck[match])=(deck[match],deck[drawn]);
            Exchange(0,index);
        }
        public void Exchange(int seat,int index,bool psychic=false)
        {
            if(Done || seat<0 || seat>1 || index<0 || index>=Hand[seat].Count || drawn>=deck.Length)
                throw new InvalidOperationException("非法换牌");
            int replacement=deck[drawn++];
            if(psychic && drawn<deck.Length){
                int alternate=deck[drawn++];
                var proposed=Hand[seat].ToArray();
                proposed[index]=replacement;int first=Cards.Score(proposed);
                proposed[index]=alternate;if(Cards.Score(proposed)>first)replacement=alternate;
            }
            Hand[seat][index]=replacement;
            if(seat==0 && index==0)KnownHole=-1;
        }
        public void Respond()

        {

            if (!Done && Actor == 1) Play(ChooseMove());

        }

        // Sample only from information visible to seat 1. At the river all

        // possible hole cards can be priced exactly, without inspecting the deck.

        public double EstimateEquity()

        {

            var mine = Hand[1].ToArray();

            var exposed = Hand[0].Skip(1).ToArray();

            var unseen = Enumerable.Range(0,52).Except(mine).Except(exposed).Where(c=>c!=KnownHole).ToArray();

            var myFive = new int[5];

            var theirFive = new int[5];

            mine.CopyTo(myFive,0);

            exposed.CopyTo(theirFive,1);

            bool river = mine.Length == 5;

            int samples = river ? (KnownHole>=0?1:unseen.Length) : 28;

            double equity = 0;

            for(int trial=0;trial<samples;trial++)

            {

                int cursor=0;

                int DrawUnknown()

                {

                    int j=random.Next(cursor,unseen.Length);

                    (unseen[cursor],unseen[j])=(unseen[j],unseen[cursor]);

                    return unseen[cursor++];

                }

                theirFive[0]=KnownHole>=0 ? KnownHole : river ? unseen[trial] : DrawUnknown();

                for(int n=mine.Length;n<5;n++) {myFive[n]=DrawUnknown();theirFive[n]=DrawUnknown();}

                int comparison=Cards.Score(myFive).CompareTo(Cards.Score(theirFive));

                equity += comparison>0 ? 1 : comparison==0 ? .5 : 0;

            }

            return equity/samples;

        }



        public Move ChooseMove()

        {

            double equity=EstimateEquity();

            double price=Due==0 ? 0 : (double)Due/(Pot+Due);

            double roll=random.NextDouble();

            bool strongBoard=Cards.Score(Hand[1].Skip(1))>Cards.Score(Hand[0].Skip(1));

            double tempo=Opponent=="荣叔" ? (Round<=2?-.12:Round==4?.17:0) : 0;

            if(Opponent=="扳手岚" && QuietRounds>=2 && Legal(Move.Bet) && roll<.8)return Move.Bet;

            if(Opponent=="衡叔" && Due>0 && Due>Stack[1]/2 && equity<.60)return Move.Fold;

            bool bluff=strongBoard && roll<.06+Math.Max(Aggression,0)/3;

            if(Due>0 && equity+.04+Aggression*.2<price && !bluff)return Move.Fold;

            if(Round==4 && Limit>Due && ((equity>.90 && roll<.20+Aggression/2) || (bluff && Opponent=="庄姐")))return Move.AllIn;

            Move attack=Due==0 ? Move.Bet : Move.Raise;

            if(Legal(attack) && (bluff || (equity>.53 && roll<equity-.25+Aggression+tempo)))return attack;

            return Due==0 ? Move.Check : Move.Call;

        }

    }

}

