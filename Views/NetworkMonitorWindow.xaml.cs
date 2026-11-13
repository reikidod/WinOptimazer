using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using WinOptimizer.Infrastructure;
using WinOptimizer.Services;
namespace WinOptimizer.Views;
public partial class NetworkMonitorWindow:Window,INotifyPropertyChanged
{
 private readonly DispatcherTimer timer=new(DispatcherPriority.Background){Interval=TimeSpan.FromSeconds(1)};
 private readonly SemaphoreSlim gate=new(1,1);private string download="Измерение…",upload="Измерение…",received="—",sent="—",status="Сбор статистики…";
 public NetworkMonitorWindow(){InitializeComponent();DataContext=this;timer.Tick+=OnTick;Loaded+=OnLoaded;Closed+=(_,_)=>timer.Stop();}
 public ObservableCollection<NetworkAdapterSnapshot> Adapters{get;}=new();
 public string DownloadRate{get=>download;private set=>Set(ref download,value);}
 public string UploadRate{get=>upload;private set=>Set(ref upload,value);}
 public string ReceivedTotal{get=>received;private set=>Set(ref received,value);}
 public string SentTotal{get=>sent;private set=>Set(ref sent,value);}
 public string Status{get=>status;private set=>Set(ref status,value);}
 public Visibility NoAdaptersVisibility=>Adapters.Count==0?Visibility.Visible:Visibility.Collapsed;
 public event PropertyChangedEventHandler? PropertyChanged;
 private async void OnLoaded(object s,RoutedEventArgs e){Loaded-=OnLoaded;timer.Start();await RefreshAsync();}
 private async void OnTick(object? s,EventArgs e)=>await RefreshAsync();
 private async void OnRefreshClick(object s,RoutedEventArgs e)=>await RefreshAsync();
 private async Task RefreshAsync(){if(!await gate.WaitAsync(0))return;try{var x=await Task.Run(NetworkMonitorService.Collect);DownloadRate=x.DownloadRate;UploadRate=x.UploadRate;ReceivedTotal=x.ReceivedTotal;SentTotal=x.SentTotal;Status=x.Status;Adapters.Clear();foreach(var a in x.Adapters)Adapters.Add(a);PropertyChanged?.Invoke(this,new(nameof(NoAdaptersVisibility)));}catch(Exception ex){Logger.Error("Не удалось обновить статистику сети",ex);Status="Не удалось получить статистику сети";}finally{gate.Release();}}
 private void Set<T>(ref T f,T v,[CallerMemberName]string? n=null){if(EqualityComparer<T>.Default.Equals(f,v))return;f=v;PropertyChanged?.Invoke(this,new(n));}
}