using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
namespace FriendBPSort;
public enum SortMode { Alphabetical, BattlePoints, FriendAdded, RecentlyPlayed, Wins, TotalMatches, Losses }
public static class Ranking
{
    public static string PlainName(string name)=>Regex.Replace(name??"", "<[^>]*>", "");
    public static int Compare(SortMode mode,int bpA,string nameA,string idA,long dateA,int bpB,string nameB,string idB,long dateB,bool inverse=false)
    {
        bool numeric=mode!=SortMode.Alphabetical;
        long a=mode==SortMode.BattlePoints?Math.Max(-1,bpA):dateA;
        long b=mode==SortMode.BattlePoints?Math.Max(-1,bpB):dateB;
        bool date=mode==SortMode.FriendAdded || mode==SortMode.RecentlyPlayed;
        bool unknownA=date?a<=0:a<0,unknownB=date?b<=0:b<0;
        if(numeric && unknownA!=unknownB)return unknownA?1:-1;
        int result=numeric?(inverse?a.CompareTo(b):b.CompareTo(a)):0;
        if(result!=0)return result;
        result=StringComparer.OrdinalIgnoreCase.Compare(PlainName(nameA),PlainName(nameB));
        if(mode==SortMode.Alphabetical && inverse)result=-result;
        return result!=0?result:StringComparer.Ordinal.Compare(idA??"",idB??"");
    }
}
public sealed class SortHistory
{
    public HashSet<string> KnownFriends {get;set;}=new();
    public Dictionary<string,long> Added {get;set;}=new();
    public Dictionary<string,long> Played {get;set;}=new();
    public bool HasBaseline {get;set;}
    public bool ObserveFriends(IEnumerable<string> ids,long now)
    {
        var current=new HashSet<string>(ids.Where(id=>!string.IsNullOrEmpty(id)));
        if(current.Count==0)return false;
        if(!HasBaseline){KnownFriends=current;HasBaseline=true;return true;}
        bool changed=false;
        foreach(string id in current)if(KnownFriends.Add(id)){Added[id]=now;changed=true;}
        return changed;
    }
    public void RecordPlayed(string id,long now){if(!string.IsNullOrEmpty(id))Played[id]=now;}
    public long Date(SortMode mode,string master,string title)
    {
        var dates=mode==SortMode.FriendAdded?Added:Played;
        long a=0,b=0;
        if(master!=null)dates.TryGetValue(master,out a);
        if(title!=null)dates.TryGetValue(title,out b);
        return Math.Max(a,b);
    }
}
