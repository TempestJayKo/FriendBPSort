using System;
using System.Text.Json;
using FriendBPSort;
class Checks
{
    static int count;
    static void Check(bool pass,string message){count++;if(!pass)throw new Exception(message);}
    static int C(SortMode mode,int a,string na,long da,int b,string nb,long db)=>Ranking.Compare(mode,a,na,na,da,b,nb,nb,db);
    static void Main()
    {
        Check(C(SortMode.Alphabetical,1,"<color=red>Alice</color>",0,500,"bob",999)<0,"Alphabetical ignores BP, dates and color markup");
        Check(C(SortMode.BattlePoints,500,"z",0,1,"a",0)<0,"BP descending");
        Check(C(SortMode.BattlePoints,-1,"a",0,0,"z",0)>0,"Unknown BP last");
        foreach(var mode in new[]{SortMode.FriendAdded,SortMode.RecentlyPlayed})
        {Check(C(mode,1,"z",200,900,"a",100)<0,"Newest first");Check(C(mode,1,"a",0,1,"z",100)>0,"Unknown dates last");}
        foreach(SortMode mode in Enum.GetValues(typeof(SortMode)))
        for(int a=0;a<5;a++)for(int b=0;b<5;b++)
        {int x=C(mode,a-1,"n"+a,a*10,b-1,"n"+b,b*10);int y=C(mode,b-1,"n"+b,b*10,a-1,"n"+a,a*10);Check(Math.Sign(x)==-Math.Sign(y),"Comparator symmetry");}
        var history=new SortHistory();
        Check(history.ObserveFriends(new[]{"old"},100),"First baseline saved");
        Check(history.Date(SortMode.FriendAdded,"old",null)==0,"Historical date not invented");
        Check(history.ObserveFriends(new[]{"old","new"},200),"New friend observed");
        Check(history.Date(SortMode.FriendAdded,"new",null)==200,"New friend timestamp");
        Check(!history.ObserveFriends(new[]{"old","new"},300),"Repeated observation stable");
        history.RecordPlayed("title",400);
        Check(history.Date(SortMode.RecentlyPlayed,"master","title")==400,"Title ID fallback");
        history.RecordPlayed("master",500);
        Check(history.Date(SortMode.RecentlyPlayed,"master","title")==500,"Newest identity timestamp");
        var restored=JsonSerializer.Deserialize<SortHistory>(JsonSerializer.Serialize(history));
        Check(restored.HasBaseline && restored.Date(SortMode.FriendAdded,"new",null)==200 && restored.Date(SortMode.RecentlyPlayed,"master","title")==500,"History roundtrip");
        Check(!restored.ObserveFriends(new[]{"old","new"},900),"Restart does not reset friend dates");
        Console.WriteLine("PASS "+count+" sorting/history checks");
    }
}
