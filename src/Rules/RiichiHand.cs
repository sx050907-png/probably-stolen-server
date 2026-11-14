using System;
using System.Linq;
using System.Collections.Generic;

namespace NightStud
{
    internal static class RiichiHand
    {
        internal static bool Outside(int t)=>t>=27 || t%9==0 || t%9==8;
        internal static bool Sequence(int t)=>t<27 && t%9<7;
        internal static int[] Counts(IEnumerable<int> tiles){
            var c=new int[34];foreach(int t in tiles)c[t]++;return c;
        }
        internal static int Distance(int[] c,int open){
            int best=8;
            if(open==0){
                best=Math.Min(6-c.Count(v=>v>=2)+Math.Max(0,7-c.Count(v=>v>0)),
                    13-Enumerable.Range(0,34).Count(t=>Outside(t)&&c[t]>0)-(Enumerable.Range(0,34).Any(t=>Outside(t)&&c[t]>1)?1:0));
            }
            void Search(int start,int sets,int parts,int pair){
                while(start<34 && c[start]==0)start++;
                best=Math.Min(best,8-2*sets-Math.Min(parts,4-sets)-pair);
                if(start==34||sets==4&&pair==1||best<0)return;
                int t=start;
                if(c[t]>=3){c[t]-=3;Search(t,sets+1,parts,pair);c[t]+=3;}
                if(Sequence(t)&&c[t+1]>0&&c[t+2]>0){
                    c[t]--;c[t+1]--;c[t+2]--;Search(t,sets+1,parts,pair);c[t]++;c[t+1]++;c[t+2]++;
                }
                if(c[t]>=2){
                    c[t]-=2;if(pair==0)Search(t,sets,parts,1);
                    if(parts+sets<4)Search(t,sets,parts+1,pair);c[t]+=2;
                }
                if(t<27&&parts+sets<4)for(int d=1;d<=2 && t%9+d<9;d++){
                    if(c[t+d]==0)continue;
                    c[t]--;c[t+d]--;Search(t,sets,parts+1,pair);c[t]++;c[t+d]++;
                }
                int saved=c[t];c[t]=0;Search(t+1,sets,parts,pair);c[t]=saved;
            }
            Search(0,open,0,0);return best;
        }
    }
}
