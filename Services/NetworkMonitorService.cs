using System.Net.NetworkInformation;
namespace WinOptimizer.Services;
public sealed record NetworkAdapterSnapshot(string Name,string Description,string DownloadRate,string UploadRate,string ReceivedTotal,string SentTotal);
public sealed record NetworkMonitorSnapshot(IReadOnlyList<NetworkAdapterSnapshot> Adapters,string DownloadRate,string UploadRate,string ReceivedTotal,string SentTotal,string Status);
public static class NetworkMonitorService
{
 private sealed class State{public NetworkTrafficRateTracker Tracker{get;}=new();}
 private static readonly object Sync=new();
 private static readonly Dictionary<string,State> States=new(StringComparer.OrdinalIgnoreCase);
 public static NetworkMonitorSnapshot Collect()
 {
  lock(Sync){var now=DateTime.UtcNow;var active=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var rows=new List<NetworkAdapterSnapshot>();
   long rx=0,tx=0;double rxs=0,txs=0;var ready=true;
   try{foreach(var nic in NetworkInterface.GetAllNetworkInterfaces()){
    if(nic.OperationalStatus!=OperationalStatus.Up||nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)continue;
    try{var st=nic.GetIPv4Statistics();var r=Math.Max(0,st.BytesReceived);var t=Math.Max(0,st.BytesSent);active.Add(nic.Id);
     if(!States.TryGetValue(nic.Id,out var state))States[nic.Id]=state=new State();
     var rate=state.Tracker.Update(r,t,now);rx=Add(rx,r);tx=Add(tx,t);
     if(rate.IsAvailable){rxs+=rate.ReceivedBytesPerSecond!.Value;txs+=rate.SentBytesPerSecond!.Value;}else ready=false;
     rows.Add(new(nic.Name,nic.Description,rate.DownloadText,rate.UploadText,Bytes(r),Bytes(t)));
    }catch(Exception ex)when(ex is NetworkInformationException or InvalidOperationException or OverflowException){}
   }}catch(NetworkInformationException){ready=false;}
   foreach(var id in States.Keys.Where(x=>!active.Contains(x)).ToArray())States.Remove(id);
   return new(rows.OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).ToArray(),ready&&rows.Count>0?Rate(rxs):"Измерение…",ready&&rows.Count>0?Rate(txs):"Измерение…",Bytes(rx),Bytes(tx),rows.Count==0?"Активные сетевые адаптеры не обнаружены":$"Адаптеров: {rows.Count} · {now.ToLocalTime():HH:mm:ss}");
  }
 }
 private static long Add(long a,long b)=>a>long.MaxValue-b?long.MaxValue:a+b;
 private static string Bytes(long n){string[] u=["Б","КБ","МБ","ГБ","ТБ"];double v=Math.Max(0,n);int i=0;while(v>=1024&&i<u.Length-1){v/=1024;i++;}return $"{v:0.#} {u[i]}";}
 private static string Rate(double n){string[] u=["Б/с","КБ/с","МБ/с","ГБ/с"];double v=Math.Max(0,n);int i=0;while(v>=1024&&i<u.Length-1){v/=1024;i++;}return $"{v:0.#} {u[i]}";}
}