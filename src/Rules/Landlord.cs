using System;
using System.Collections.Generic;
using System.Linq;

namespace NightStud
{
    internal sealed class Landlord
    {
        internal readonly List<int>[] Hands={new List<int>(),new List<int>(),new List<int>()};
        internal readonly int[] Bottom=new int[3];
        internal int Turn, Boss=-1, Winner=-1, Leader=-1;
        internal int[] Last=Array.Empty<int>();
        internal string Note="";
        int bids,passes;
        internal static int Rank(int c)=>c>=52?16+c-52:(c%13==0?15:c%13+2);
        internal static string Face(int c)=>c==52?"小王":c==53?"大王":Cards.Face(c).Trim();
        internal Landlord(Random rng)
        {
            var deck=Enumerable.Range(0,54).ToArray();
            for(int i=53;i>0;i--){int j=rng.Next(i+1);(deck[i],deck[j])=(deck[j],deck[i]);}
            for(int i=0;i<51;i++)Hands[i%3].Add(deck[i]);
            Array.Copy(deck,51,Bottom,0,3);Turn=rng.Next(3);Sort();
        }
        void Sort(){foreach(var h in Hands)h.Sort((a,b)=>Rank(a)!=Rank(b)?Rank(a)-Rank(b):a-b);}
        internal void Bid(bool take)
        {
            if(Boss>=0)return;
            int seat=Turn;bids++;
            if(take || bids==3){Boss=take?seat:(seat+1)%3;Turn=Boss;Hands[Boss].AddRange(Bottom);Sort();Note="地主拿走三张底牌。";}
            else{Turn=(Turn+1)%3;Note="不叫地主。";}
        }
        internal static (string kind,int high,int count) Shape(IEnumerable<int> cards)
        {
            var ranks=cards.Select(Rank).GroupBy(r=>r).ToDictionary(g=>g.Key,g=>g.Count());
            int n=ranks.Values.Sum();if(n==0)return default;
            int high=ranks.Keys.Max();
            if(n==2 && ranks.ContainsKey(16)&&ranks.ContainsKey(17))return ("王炸",17,2);
            if(ranks.Count==1)return (new[]{"","单张","对子","三张","炸弹"}[n],high,n);
            if(ranks.ContainsValue(4) && (n==6 || n==8 && ranks.Values.Count(k=>k==2)==2))
                return (n==6?"四带二":"四带两对",ranks.First(g=>g.Value==4).Key,n);
            if(ranks.ContainsValue(3) && (n==4 || n==5 && ranks.ContainsValue(2)))
                return (n==4?"三带一":"三带一对",ranks.First(g=>g.Value==3).Key,n);
            int lo=ranks.Keys.Min();
            if(high<=14 && high-lo+1==ranks.Count){
                if(n>=5 && ranks.Values.All(k=>k==1))return ("顺子",high,n);
                if(n>=6 && ranks.Values.All(k=>k==2))return ("连对",high,n);
            }
            for(int len=Math.Min(6,n/3);len>=2;len--)for(int end=14;end>=len+2;end--){
                var body=Enumerable.Range(end-len+1,len);
                if(!body.All(r=>ranks.TryGetValue(r,out int k)&&k==3))continue;
                var rest=ranks.Where(g=>!body.Contains(g.Key)).Select(g=>g.Value).ToArray();
                if(n==3*len)return ("飞机",end,n);
                if(n==4*len)return ("飞机带单",end,n);
                if(n==5*len && rest.All(k=>k==2))return ("飞机带对",end,n);
            }
            return default;
        }
        bool Beats(int[] play)
        {
            var p=Shape(play);if(p.kind==null)return false;
            if(Last.Length==0)return true;
            var last=Shape(Last);
            return p.kind=="王炸" || p.kind=="炸弹" && last.kind!="王炸" && last.kind!="炸弹" ||
                p.kind==last.kind && p.count==last.count && p.high>last.high;
        }
        internal bool Play(int[] cards)
        {
            if(Winner>=0||Boss<0)return false;
            if(cards.Length==0){
                if(Last.Length==0)return false;
                Note="不要。";if(++passes==2){Last=Array.Empty<int>();passes=0;Note="两家都过，重新领出。";}
            }else{
                if(cards.Distinct().Count()!=cards.Length||!cards.All(Hands[Turn].Contains)||!Beats(cards))return false;
                foreach(int c in cards)Hands[Turn].Remove(c);
                Last=cards.OrderBy(Rank).ToArray();Leader=Turn;passes=0;Note=Shape(cards).kind;
                if(Hands[Turn].Count==0){Winner=Turn;return true;}
            }
            Turn=(Turn+1)%3;return true;
        }
        internal int[] Suggest()=>Suggestions().FirstOrDefault()??Array.Empty<int>();
        internal int[][] Suggestions()
        {
            var h=Hands[Turn];var g=h.GroupBy(Rank).OrderBy(x=>x.Key).Select(x=>x.ToArray()).ToArray();
            var options=new List<int[]>();
            void Offer(IEnumerable<int> set){var p=set.ToArray();if(Beats(p))options.Add(p);}
            IEnumerable<int[]> Wings(int[][] groups,int needed,int unit=1,int index=0){
                if(needed==0){yield return Array.Empty<int>();yield break;}
                if(index>=groups.Length)yield break;
                int max=unit==2?Math.Min(1,groups[index].Length/2):Math.Min(needed,groups[index].Length);
                for(int take=0;take<=Math.Min(max,needed);take++)
                    foreach(var tail in Wings(groups,needed-take,unit,index+1))yield return groups[index].Take(take*unit).Concat(tail).ToArray();
            }
            void OfferWithWings(IEnumerable<int> body,int[][] groups,int count,int unit=1){
                foreach(var wings in Wings(groups,count,unit))Offer(body.Concat(wings));
            }
            Offer(h);
            foreach(var a in g){
                for(int k=1;k<=a.Length;k++)Offer(a.Take(k));
                var others=g.Where(x=>x!=a).ToArray();
                if(a.Length>=3)foreach(var b in others){Offer(a.Take(3).Concat(b.Take(1)));if(b.Length>=2)Offer(a.Take(3).Concat(b.Take(2)));}
                if(a.Length==4){OfferWithWings(a,others,2);OfferWithWings(a,others,2,2);}
            }
            if(h.Contains(52)&&h.Contains(53))Offer(new[]{52,53});
            for(int k=1;k<=3;k++)for(int i=0;i<g.Length;i++){
                var seq=new List<int>();
                for(int j=i;j<g.Length && Rank(g[j][0])==Rank(g[i][0])+j-i && Rank(g[j][0])<=14 && g[j].Length>=k;j++){
                    seq.AddRange(g[j].Take(k));int len=j-i+1;
                    if(len<(k==1?5:k==2?3:2))continue;
                    Offer(seq);
                    if(k==3){var wings=g.Where((x,index)=>index<i||index>j).ToArray();
                        OfferWithWings(seq,wings,len);OfferWithWings(seq,wings,len,2);}
                }
            }
            int SplitCost(int[] play)=>g.Sum(group=>{int used=play.Count(c=>Rank(c)==Rank(group[0]));return used>0&&used<group.Length?(group.Length==4?12:group.Length==3?6:2):0;});
            return options.GroupBy(x=>string.Join(",",x.Select(Rank).OrderBy(r=>r))).Select(group=>group.First())
                .OrderByDescending(x=>x.Length==h.Count).ThenBy(x=>Shape(x).kind=="王炸"||Shape(x).kind=="炸弹")
                .ThenBy(x=>SplitCost(x)).ThenByDescending(x=>x.Length).ThenBy(x=>Shape(x).high).ToArray();
        }
        internal void Bot()
        {
            if(Boss<0){Bid(Hands[Turn].Count(c=>Rank(c)>=14)>=4);return;}
            var move=Suggest();
            if(Last.Length>0 && Leader!=Boss && Turn!=Boss && move.Length!=Hands[Turn].Count)move=Array.Empty<int>();
            Play(move);
        }
    }
}
