using System;
using System.Collections.Generic;
using System.Linq;

namespace NightStud.Shared {
    public static class TableVoiceRules {
        public const int ManualCooldownSeconds=3;
        static readonly string[] landlord={"wait","coffee","nice","partner","show","seventeen","ready"};
        static readonly string[] cards={"wait","nice","show","ready"};
        static readonly string[] dice={"wait","show"};
        static readonly string[] none=Array.Empty<string>();
        public static IReadOnlyList<string> Keys(string mode)=>mode=="landlord"?landlord:mode=="dice"?dice:
            mode=="stud"||mode=="three"||mode=="holdem"||mode=="blackjack"||mode=="mahjong"?cards:none;
        public static bool Allowed(string mode,string phrase)=>Keys(mode).Contains(phrase);
    }
}
