#nullable disable
using System;
using System.Linq;
using System.Collections.Generic;

namespace NightStud
{
    internal sealed partial class Mahjong
    {
        internal readonly List<int>[] Hands=Lists(), Rivers=Lists(), Melds=Lists();
        readonly List<int>[] groups=Lists(), concealedKongs=Lists();
        static List<int>[] Lists()=>Enumerable.Range(0,4).Select(i=>new List<int>()).ToArray();
        readonly Stack<int> wall=new Stack<int>(), replacements=new Stack<int>();
        readonly bool[] missed=new bool[4];
        readonly Func<int,string> seatName;
        readonly int dealer;
        internal int PlayerWind=>27+(4-dealer)%4;
        int kongs,pendingKong=-1,kongOwner;
        bool replacement,robbingClosed;
        readonly HashSet<int> forbidden=new HashSet<int>();
        internal readonly List<int> Winners=new List<int>();
        internal int Turn,Winner=-1,From=-1,Last=-1,Drawn=-1,DiscardCount;
        internal bool Done,Reply,LastClaimed;
        internal int Left=>wall.Count;
        internal int WallCount=>Left+14;
        internal string Note="";
        internal sealed class VisualFrame {
            internal string Kind,Detail;
            internal int Actor,Turn,Drawn,From,Last,Left,DiscardCount;
            internal bool Done,LastClaimed;
            internal int[][] Hands,Rivers,Melds,Sizes;
            internal int[] Winners;
        }
        internal readonly Queue<VisualFrame> Visuals=new Queue<VisualFrame>();
        internal VisualFrame Capture(string kind,int actor=-1){
            var sizes=Enumerable.Range(0,4).Select(s=>groups[s].Select(g=>kongTiles[s].Contains(g)||concealedKongs[s].Contains(g)?4:3).ToArray()).ToArray();
            return new VisualFrame{Kind=kind,Actor=actor,Turn=Turn,Drawn=Drawn,From=From,Last=Last,Left=Left,DiscardCount=DiscardCount,
                Done=Done,LastClaimed=LastClaimed,Hands=Hands.Select(h=>h.ToArray()).ToArray(),Rivers=Rivers.Select(h=>h.ToArray()).ToArray(),
                Melds=Melds.Select(h=>h.ToArray()).ToArray(),Sizes=sizes,Winners=Winners.ToArray()};
        }
        void Record(string kind,int actor=-1)=>Visuals.Enqueue(Capture(kind,actor));
        static readonly string[] honors={"东","南","西","北","白","发","中"};
        internal static string Tile(int t)=>t>=27?honors[t-27]:("一二三四五六七八九"[t%9]+new[]{"万","筒","条"}[t/9]);
        internal static string Seat(int s)=>new[]{"你","下家","对家","上家"}[s];
        internal Mahjong(Random rng,Func<int,string> name=null,bool manualPlayers=false){
            ManualPlayers=manualPlayers;
            seatName=name??Seat;
            var tiles=Enumerable.Range(0,136).Select(i=>i/4).ToArray();
            for(int i=135;i>0;i--){int j=rng.Next(i+1);(tiles[i],tiles[j])=(tiles[j],tiles[i]);}
            foreach(int t in tiles.Take(4))replacements.Push(t);
            foreach(int t in tiles.Skip(14))wall.Push(t);
            for(int i=0;i<52;i++)Hands[i%4].Add(wall.Pop());
            Record("deal");
            foreach(var h in Hands)h.Sort();Record("sort");dealer=rng.Next(4);Draw(dealer);
        }
        static bool Sets(int[] c){
            int t=Array.FindIndex(c,n=>n>0);if(t<0)return true;
            if(c[t]>=3){c[t]-=3;bool ok=Sets(c);c[t]+=3;if(ok)return true;}
            if(RiichiHand.Sequence(t)&&c[t+1]>0&&c[t+2]>0){
                c[t]--;c[t+1]--;c[t+2]--;bool ok=Sets(c);c[t]++;c[t+1]++;c[t+2]++;return ok;
            }
            return false;
        }
        internal static bool Winning(IEnumerable<int> tiles,int open){
            var c=RiichiHand.Counts(tiles);
            if(c.Sum()!=14-open*3||c.Any(n=>n>4))return false;
            if(open==0&&(c.Count(n=>n==2)==7||Enumerable.Range(0,34).Where(RiichiHand.Outside).All(t=>c[t]>0)&&c.Sum()==c.Where((v,t)=>RiichiHand.Outside(t)).Sum()))return true;
            for(int t=0;t<34;t++)if(c[t]>=2){c[t]-=2;bool ok=Sets(c);c[t]+=2;if(ok)return true;}
            return false;
        }
        internal sealed class Bill {
            internal readonly List<(string name,int fan)> Lines=new List<(string,int)>();
            internal int Fan=>Lines.Sum(line=>line.fan);
            internal int Multiplier=>1<<(Math.Min(5,Fan)-1);
        }
        internal Bill Score(int s){
            bool self=From<0;
            var hidden=self?Hands[s].ToArray():Hands[s].Append(Last).ToArray();
            var all=hidden.Concat(Melds[s]).ToArray();
            var counts=RiichiHand.Counts(hidden);
            var bill=new Bill();
            string name="平胡";int fan=1;
            void Candidate(bool matches,string label,int value){
                if(matches && (value>fan || name=="平胡")){name=label;fan=value;}
            }
            bool suits=all.Any(t=>t<27);
            bool oneSuit=suits && all.Where(t=>t<27).Select(t=>t/9).Distinct().Count()==1;
            Candidate(groups[s].All(t=>t>=0)&&counts.Count(n=>n%3==2)==1&&counts.All(n=>n%3!=1),"碰碰胡",2);
            Candidate(oneSuit&&all.Any(t=>t>=27),"混一色",2);
            Candidate(groups[s].Count==0&&counts.Count(v=>v==2)==7,"七对",3);
            Candidate(oneSuit&&all.All(t=>t<27),"清一色",3);
            Candidate(groups[s].Count==4&&groups[s].All(t=>t>=0)&&hidden.Length==2,"金钩钓",3);
            Candidate(all.All(t=>t>=27),"字一色",5);
            Candidate(groups[s].Count==0&&hidden.All(RiichiHand.Outside)&&hidden.Distinct().Count()==13,"十三幺",5);
            bill.Lines.Add((name,fan));
            int roots=RiichiHand.Counts(all).Count(n=>n==4);
            if(roots>0)bill.Lines.Add(("根",roots));
            if(self&&replacement)bill.Lines.Add(("杠上开花",1));
            else if(!self&&pendingKong>=0)bill.Lines.Add(("抢杠胡",1));
            else if(Left==0)bill.Lines.Add((self?"海底捞月":"海底炮",1));
            return bill;
        }
        internal const int PointsPerCredit=100;
        internal int PlayerPoints(int basePoints){
            int net=0;
            foreach(int s in Winners){
                int unit=basePoints*Score(s).Multiplier;
                if(From<0)net+=s==0?3*unit:-unit;
                else if(s==0)net+=unit;
                else if(From==0)net-=unit;
            }
            return net;
        }
        internal IEnumerable<int> Waits(int s,IEnumerable<int> hand){
            var h=hand.ToArray();
            return Enumerable.Range(0,34).Where(t=>h.Count(x=>x==t)+Melds[s].Count(x=>x==t)<4&&Winning(h.Append(t),groups[s].Count));
        }
        internal sealed class WaitPlan {public int Discard {get;set;}public int[] Waits {get;set;}}
        internal WaitPlan[] WaitPlans(int seat){
            if(Done||Reply||Turn!=seat||Hands[seat].Count%3!=2)return Array.Empty<WaitPlan>();
            return Hands[seat].Distinct().Where(MayDiscard).Select(tile=>new WaitPlan{Discard=tile,Waits=Waits(seat,Without(seat,tile)).Distinct().OrderBy(t=>t).ToArray()}).Where(p=>p.Waits.Length>0).ToArray();
        }
        bool Ron(int s)=>s!=From && !robbingClosed && !missed[s] && Winning(Hands[s].Append(Last),groups[s].Count);
        internal bool PlayerRon=>Reply && Ron(0);
        internal bool SelfWin=>!Done&&!Reply&&Drawn>=0&&Winning(Hands[Turn],groups[Turn].Count);
        internal void WinSelf(){if(!SelfWin)return;Winner=Turn;Winners.Add(Turn);Done=true;From=-1;Record("win",Winner);}
        void Draw(int s,bool supplement=false){
            if(s==0)PlayerWaitText="";
            Turn=s;Reply=false;Drawn=-1;replacement=supplement;forbidden.Clear();
            missed[s]=false;
            if(Left==0){Done=true;Note="流局。";Record("exhausted");return;}
            Drawn=wall.Pop();
            if(supplement)Drawn=replacements.Pop();
            Hands[s].Add(Drawn);Hands[s].Sort();
            Record(supplement?"replacement":"draw",s);
        }
        internal List<int> Chows(int s){
            var options=new List<int>();
            if(s!=(From+1)%4||Last<0||Last>=27||Left==0||pendingKong>=0)return options;
            for(int t=Last-2;t<=Last;t++)if(t>=0&&t/9==Last/9&&RiichiHand.Sequence(t)&&Enumerable.Range(t,3).All(x=>x==Last||Hands[s].Contains(x)))options.Add(t);
            return options.Where(first=>Hands[s].Any(t=>t!=Last && !(t>=first&&t<first+3&&Hands[s].Count(k=>k==t)==1) && !(Last==first&&first%9<6&&t==first+3) && !(Last==first+2&&first%9>0&&t==first-1))).ToList();
        }
        bool Pong(int s)=>pendingKong<0 && From!=s && Left>0&&Hands[s].Count(x=>x==Last)>=2;
        internal bool PlayerPong=>Reply && Pong(0);
        internal bool PlayerKong=>PlayerPong&&CanKong&&Hands[0].Count(x=>x==Last)==3;
        internal bool PlayerChow=>Reply && Chows(0).Count>0 && !Enumerable.Range(1,3).Any(Pong);
        bool CanKong=>kongs<4&&Left>0;
        internal int[] OwnKongs(int s){
            if(!CanKong||Drawn<0||Reply||Done||Turn!=s)return Array.Empty<int>();
            return Hands[s].Distinct().Where(t=>Hands[s].Count(x=>x==t)==4||Added(t)).ToArray();
        }
        internal bool Added(int t)=>groups[Turn].Contains(t);
        internal void OwnKong(int t){
            if(!OwnKongs(Turn).Contains(t))return;
            robbingClosed=!Added(t);pendingKong=t;kongOwner=Turn;From=Turn;Last=t;LastClaimed=true;Drawn=-1;
            Resolve(true);
        }
        void CompleteKong(int s,string kind,int t){kongs++;var frame=Capture("kong",s);frame.Detail=kind;Visuals.Enqueue(frame);Draw(s,true);Note=seatName(s)+kind+Tile(t)+"，补一张。";}
        void Meld(int s,int first,int length,bool same){
            if(s==0)PlayerWaitText="";
            var tiles=(same?Enumerable.Repeat(Last,length):Enumerable.Range(first,3)).ToList();
            var own=tiles.ToList();own.Remove(Last);foreach(int t in own)Hands[s].Remove(t);
            Melds[s].AddRange(tiles);groups[s].Add(same?Last:~first);
            forbidden.Clear();forbidden.Add(Last);
            if(!same && Last==first && first%9<6)forbidden.Add(first+3);
            if(!same && Last==first+2 && first%9>0)forbidden.Add(first-1);
            LastClaimed=true;Rivers[From].RemoveAt(Rivers[From].Count-1);
            Turn=s;Drawn=-1;Reply=false;replacement=false;
            if(length==4){kongTiles[s].Add(Last);CompleteKong(s,"明杠",Last);return;}
            Note=seatName(s)+(same?"碰":"吃")+Tile(Last)+"。";
            Record(same?"pung":"chow",s);
        }
        internal const int Pass=-1, Pung=-2, Kong=-3, Win=-4;
        internal void Respond(int action){
            if(!Reply||Done)return;
            bool canWin=PlayerRon;
            if(action==Win && canWin){Resolve(false,true);return;}
            if(action!=Pass && !(action==Pung&&PlayerPong || action==Kong&&PlayerKong || action>=0&&PlayerChow&&Chows(0).Contains(action)))return;
            if(canWin)missed[0]=true;
            if(Enumerable.Range(1,3).Any(s=>s!=From&&Ron(s))||action==Pass){Resolve(false);return;}
            Meld(0,action,action==Kong?4:3,action<0);
        }
        void Resolve(bool ask,bool accept=false){
            if(ManualPlayers){BeginOnlineClaims();return;}
            Reply=false;
            if(ask&&Ron(0)){Reply=true;return;}
            for(int d=1;d<=3;d++){
                int s=(From+d)%4;if((s!=0||accept)&&Ron(s))Winners.Add(s);
            }
            if(Winners.Count>0){Winner=Winners[0];Done=true;Record("win",Winner);return;}
            if(pendingKong>=0){
                int t=pendingKong;pendingKong=-1;
                if(robbingClosed){
                    robbingClosed=false;Hands[kongOwner].RemoveAll(x=>x==t);groups[kongOwner].Add(t);concealedKongs[kongOwner].Add(t);
                    Melds[kongOwner].AddRange(Enumerable.Repeat(t,4));CompleteKong(kongOwner,"暗杠",t);return;
                }
                Hands[kongOwner].Remove(t);
                int index=0;
                foreach(int group in groups[kongOwner]){
                    if(group==t)break;
                    bool quad=kongTiles[kongOwner].Contains(group)||concealedKongs[kongOwner].Contains(group);
                    index+=quad?4:3;
                }
                Melds[kongOwner].Insert(index,t);kongTiles[kongOwner].Add(t);CompleteKong(kongOwner,"加杠",t);return;
            }
            if(kongs==4 && groups.Where((set,s)=>Melds[s].Count>set.Count*3).Count()>1){
                Done=true;Note="四杠散了。";Record("exhausted");return;
            }
            for(int d=1;d<=3;d++){
                int s=(From+d)%4;if(!Pong(s))continue;
                if(s==0){if(ask){Reply=true;return;}continue;}
                bool kong=CanKong&&Hands[s].Count(x=>x==Last)==3;
                Meld(s,Last,kong?4:3,true);return;
            }
            int next=(From+1)%4;var chows=Chows(next);
            if(chows.Count>0){if(next==0){if(ask){Reply=true;return;}}else{Meld(next,chows[0],3,false);return;}}
            Draw(next);
        }
        readonly List<int>[] kongTiles=Lists();
        List<int> Without(int s,int t){var h=new List<int>(Hands[s]);h.Remove(t);return h;}
        internal string PlayerWaitText {get;private set;}="";
        internal bool MayDiscard(int t)=>!forbidden.Contains(t);
        internal bool Discard(int t){
            if(Done||Reply||!MayDiscard(t)||!Hands[Turn].Remove(t))return false;
            if(Turn==0){
                var waits=Waits(0,Hands[0]).Select(Tile).ToArray();
                PlayerWaitText=waits.Length==0?"":"听 "+string.Join("、",waits);
            }
            DiscardCount++;LastClaimed=false;replacement=false;From=Turn;Last=t;Drawn=-1;Rivers[Turn].Add(t);
            Note=seatName(Turn)+"打出"+Tile(t)+"。";Record("discard",Turn);Resolve(true);return true;
        }
        internal void Bot(){
            if(Turn==0||Done||Reply)return;
            if(SelfWin){WinSelf();return;}
            var ks=OwnKongs(Turn);if(ks.Length>0){OwnKong(ks[0]);return;}
            var h=Hands[Turn];int s=Turn;
            int Value(int t)=>h.Sum(x=>x==t?6:x>=27||t>=27||x/9!=t/9?0:Math.Abs(x-t)==1?3:Math.Abs(x-t)==2?1:0);
            int best=h.Distinct().Where(MayDiscard).OrderBy(t=>RiichiHand.Distance(RiichiHand.Counts(Without(s,t)),groups[s].Count)).ThenBy(Value).First();
            Discard(best);
        }
    }
}
