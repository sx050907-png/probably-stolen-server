using System;

namespace NightStud.Shared
{
    public sealed class AssetBetPlan
    {
        public string Move {get;set;}="";
        public int Amount {get;set;}
        public int Cash {get;set;}
    }
    public static class AssetBetRules
    {
        // A whole item enters the pot. Any additional cash is shown BEFORE the
        // owner confirms; no change is created by splitting a physical item.
        public static AssetBetPlan Plan(int quote,int cash,int other,int due,int minimumBet,int minimumRaise)
        {
            if(quote<1||quote>1000000||cash<0||other<0||due<0||minimumBet<1||minimumRaise<due)throw new ArgumentException("押品或牌桌金额无效。");
            long available=(long)cash+quote,matched=(long)other+due;
            if(quote>matched)throw new ArgumentException("这件货高于对方本轮能跟的金额，请换一件。");
            int limit=(int)Math.Min(available,matched);
            long target=due>0&&quote<=due?due:Math.Max(quote,due>0?minimumRaise:minimumBet);
            int amount=(int)Math.Min(target,limit);
            if(amount<quote||amount<=0)throw new ArgumentException("当前无法用这件货下注。");
            string move=due>0&&amount==due?"call":amount==limit?"allIn":due>0?"raise":"bet";
            return new AssetBetPlan{Move=move,Amount=amount,Cash=amount-quote};
        }
    }
}
