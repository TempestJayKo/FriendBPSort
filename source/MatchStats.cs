// Mew, MatchInfo owns these records. We read complete snapshots by master ID, never edit its file or count matches twice.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
namespace FriendBPSort;
public sealed class MatchRecord
{
    public long Wins {get;init;}
    public long Losses {get;init;}
    public long Total=>Wins+Losses;
}
public sealed class MatchStats
{
    public Dictionary<string,MatchRecord> Records {get;private set;}=new(StringComparer.OrdinalIgnoreCase);
    private DateTime stamp;
    private long length=-1;
    // Mew, reject truncated, duplicate, negative or overflowing records before replacing the last good snapshot.
    public static Dictionary<string,MatchRecord> Parse(string[] lines)
    {
        var result=new Dictionary<string,MatchRecord>(StringComparer.OrdinalIgnoreCase);
        int count=lines.Length;
        while(count>0 && string.IsNullOrWhiteSpace(lines[count-1]))count--;
        if(count%3!=0)throw new FormatException("Incomplete MatchInfo record.");
        for(int i=0;i<count;i+=3)
        {
            string header=lines[i].Trim().TrimStart('\uFEFF');
            int separator=header.IndexOf(" - ",StringComparison.Ordinal);
            string id=(separator<0?header:header.Substring(0,separator)).Trim();
            if(id.Length==0 || !long.TryParse(lines[i+1],NumberStyles.Integer,CultureInfo.InvariantCulture,out long wins) ||
               !long.TryParse(lines[i+2],NumberStyles.Integer,CultureInfo.InvariantCulture,out long losses) ||
               wins<0 || losses<0 || wins>long.MaxValue-losses || result.ContainsKey(id))
                throw new FormatException("Invalid or duplicate MatchInfo record.");
            result.Add(id,new MatchRecord{Wins=wins,Losses=losses});
        }
        return result;
    }
    // UwU, metadata skips unchanged files; parse first, then swap the dictionary atomically on the game thread.
    public bool Reload(string path)
    {
        if(!File.Exists(path))
        {bool changed=Records.Count>0;Records.Clear();length=-1;return changed;}
        var info=new FileInfo(path);
        if(info.LastWriteTimeUtc==stamp && info.Length==length)return false;
        // 🐱 Keep the last valid snapshot if MatchInfo is writing or the file is invalid.
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        using var reader=new StreamReader(stream);
        var lines=new List<string>();string line;
        while((line=reader.ReadLine())!=null)lines.Add(line);
        var parsed=Parse(lines.ToArray());
        Records=parsed;stamp=info.LastWriteTimeUtc;length=info.Length;return true;
    }
    public MatchRecord Find(string master)=>master!=null && Records.TryGetValue(master,out var record)?record:null;
    public long Value(SortMode mode,string master)
    {var r=Find(master);return r==null?-1:mode==SortMode.Wins?r.Wins:mode==SortMode.Losses?r.Losses:r.Total;}
}

