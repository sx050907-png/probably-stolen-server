using System;
using System.Linq;
using System.Collections.Generic;

namespace NightStud
{
    // Reuses the cargo-table rules. Human seats submit claims; this path never
    // calls the single-player resolver that chooses replies for NPCs.
    internal sealed partial class Mahjong
    {
        internal bool ManualPlayers {get;}
        readonly List<int> claimSeats=new List<int>();
        readonly Dictionary<int,int> claims=new Dictionary<int,int>();
        internal int OnlineActor=>Reply?claimSeats.FirstOrDefault():Turn;
        internal int Dealer=>dealer;
        internal int[] OnlineWaits(int seat)=>Waits(seat,Hands[seat]).ToArray();
        internal int[] OnlineDiscardChoices()=>Hands[Turn].Distinct().Where(MayDiscard).ToArray();
        internal bool OnlineRon(int seat)=>Reply&&Ron(seat);
        internal bool OnlinePong(int seat)=>Reply&&Pong(seat);
        internal bool OnlineKong(int seat)=>OnlinePong(seat)&&CanKong&&Hands[seat].Count(t=>t==Last)==3;
        internal int[] OnlineChows(int seat)=>Reply?Chows(seat).ToArray():Array.Empty<int>();
        internal int[] Concealed(int seat)=>concealedKongs[seat].ToArray();
        internal int[] MeldSizes(int seat)=>groups[seat].Select(g=>kongTiles[seat].Contains(g)||concealedKongs[seat].Contains(g)?4:3).ToArray();
        void BeginOnlineClaims(){
            Reply=true;claims.Clear();claimSeats.Clear();
            for(int d=1;d<=3;d++){
                int s=(From+d)%4;
                if(Ron(s)||Pong(s)||Chows(s).Count>0)claimSeats.Add(s);
            }
            if(claimSeats.Count==0)FinishOnlineClaims();
        }
        internal bool OnlineRespond(int seat,int action){
            if(!ManualPlayers||!Reply||Done||claimSeats.Count==0||claimSeats[0]!=seat)return false;
            if(action!=Pass&&!(action==Win&&Ron(seat)||action==Pung&&Pong(seat)||action==Kong&&OnlineKong(seat)||action>=0&&Chows(seat).Contains(action)))return false;
            if(Ron(seat)&&action!=Win)missed[seat]=true;
            claims[seat]=action;claimSeats.RemoveAt(0);
            if(claimSeats.Count==0)FinishOnlineClaims();
            return true;
        }
        void FinishOnlineClaims(){
            Reply=false;
            for(int d=1;d<=3;d++){
                int s=(From+d)%4;
                if(claims.TryGetValue(s,out int action)&&action==Win)Winners.Add(s);
            }
            if(Winners.Count>0){Winner=Winners[0];Done=true;Note="胡牌。";Record("win",Winner);return;}
            if(pendingKong>=0){
                int t=pendingKong;pendingKong=-1;
                if(robbingClosed){
                    robbingClosed=false;Hands[kongOwner].RemoveAll(x=>x==t);groups[kongOwner].Add(t);concealedKongs[kongOwner].Add(t);
                    Melds[kongOwner].AddRange(Enumerable.Repeat(t,4));CompleteKong(kongOwner,"暗杠",t);return;
                }
                Hands[kongOwner].Remove(t);int index=0;
                foreach(int group in groups[kongOwner]){if(group==t)break;index+=kongTiles[kongOwner].Contains(group)||concealedKongs[kongOwner].Contains(group)?4:3;}
                Melds[kongOwner].Insert(index,t);kongTiles[kongOwner].Add(t);CompleteKong(kongOwner,"加杠",t);return;
            }
            if(kongs==4&&groups.Where((set,s)=>Melds[s].Count>set.Count*3).Count()>1){Done=true;Note="四杠散了。";Record("exhausted");return;}
            for(int d=1;d<=3;d++){
                int s=(From+d)%4;
                if(claims.TryGetValue(s,out int action)&&(action==Pung||action==Kong)){Meld(s,Last,action==Kong?4:3,true);return;}
            }
            int next=(From+1)%4;
            if(claims.TryGetValue(next,out int chow)&&chow>=0){Meld(next,chow,3,false);return;}
            Draw(next);
        }
        internal int OnlineSuggestion()=>BotDiscard(Turn);
        internal int OnlineBotClaim(){
            int seat=OnlineActor;
            if(BotPong(seat))return BotWantsOpenKong(seat)?Kong:Pung;
            return BotChow(seat,OnlineChows(seat));
        }
    }
}
