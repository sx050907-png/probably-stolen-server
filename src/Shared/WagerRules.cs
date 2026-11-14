using System;
using System.Linq;

namespace NightStud.Shared {
    public static class WagerRules {
        public const int MaxAnte=20000, MaxBuyIn=1000000, MaxTableBalance=4000000, MaxTableTotal=MaxTableBalance*4;
        public static readonly int[] Scales={1,2,5,10,20,50,100};
        public static int Base(int tier)=>new[]{10,100,500}[Math.Max(0,Math.Min(2,tier))];
        public static int Reserve(int tier)=>new[]{30000,150000,600000}[Math.Max(0,Math.Min(2,tier))];
        public static int[] Stakes(int tier)=>Scales.Select(s=>Math.Min(MaxAnte,Base(tier)*s)).Distinct().ToArray();
        public static int NextStake(int current,int tier){var values=Stakes(tier);int i=Array.IndexOf(values,current);return values[(i+1)%values.Length];}
        public static int NextScale(int current){int i=Array.IndexOf(Scales,current);return Scales[(i+1)%Scales.Length];}
        public static int ValidScale(int value)=>Array.IndexOf(Scales,value)>=0?value:1;
        public static int Wind(int seat,int dealer)=>(seat-dealer+4)%4;
        public static bool Room(int ante,int buyIn,bool mahjong)=>ante>=1&&ante<=MaxAnte&&buyIn>=ante*(mahjong?48:10)&&buyIn<=MaxBuyIn;
    }
}
