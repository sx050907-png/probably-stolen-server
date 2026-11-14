using System;

namespace NightStudOnline {
    public sealed class ReconnectPolicy {
        static readonly int[] Delays={2,4,8,15,30};
        public int Failures {get;private set;}
        public bool Paused=>Failures>Delays.Length;
        public int Failed(int serverDelay=0){
            Failures++;return Paused?0:Math.Max(Delays[Failures-1],Math.Min(300,Math.Max(0,serverDelay)));
        }
        public void Reset()=>Failures=0;
    }
}
