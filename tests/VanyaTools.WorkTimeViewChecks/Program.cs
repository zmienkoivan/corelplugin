using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using VanyaTools.Native;

namespace VanyaTools.Native
{
    internal sealed class WorkTimeHistory { internal WorkTimeSession[] Sessions = new WorkTimeSession[0]; internal string Warning=""; }
    internal static class WorkTimeRuntime
    {
        internal static WorkTimeSettings Settings { get; } = new WorkTimeSettings { Enabled=true, Employee="QA" };
        internal static WorkTimeSession Current; internal static bool Paused, NoDocument, RandomUnsaved; internal static int Requests, Configures;
        internal static Func<WorkTimeSession,Task<WorkTimeHistory>> Provider;
        internal static void Configure(string name,bool enabled,int idle) { Configures++; Settings.Employee=name; Settings.Enabled=enabled; Settings.IdleMinutes=idle; }
        internal static void TogglePause() { Paused=!Paused; }
        internal static WorkTimeSession CurrentProject() { if(NoDocument) throw new InvalidOperationException("Откройте CDR"); var result=Current.Copy(); if(RandomUnsaved) result.ProjectId=Guid.NewGuid().ToString("N"); return result; }
        internal static Task<WorkTimeHistory> History(WorkTimeSession project) { Requests++; return Provider(project); }
    }
    internal static class DockerTheme { internal static void Apply(Window value) { } }
    internal static class DockerSettings { internal static FrameworkElement Section(string caption,object panel) => new Expander { Header=caption,Content=panel }; }
    internal static class DockerLayout { internal static FrameworkElement Trailing(UIElement a,UIElement b) { var row=new StackPanel();row.Children.Add(a);row.Children.Add(b);return row; } }
}
internal static class Program
{
    private static int _checks;
    private static void Check(bool value,string message) { _checks++;if(!value)throw new Exception(message); }
    private static void Pump() { var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(new Action(()=>frame.Continue=false),DispatcherPriority.ApplicationIdle);Dispatcher.PushFrame(frame);Thread.Sleep(2); }
    private static void Until(Func<bool> ready,string message) { var time=Stopwatch.StartNew();while(!ready()&&time.ElapsedMilliseconds<8000)Pump();Check(ready(),message); }
    private static WorkTimeSession Project(string title) => new WorkTimeSession { ProjectId=Guid.NewGuid().ToString("N"),Title=title,FilePath=@"D:\Synthetic\"+title+".cdr" };
    private static WorkTimeHistory History(WorkTimeSession project,string employee=null) => new WorkTimeHistory { Sessions=new[]{new WorkTimeSession {
        Employee=employee??"QA-"+project.Title,ActiveSeconds=60,BackgroundSeconds=120,Events=new System.Collections.Generic.List<WorkTimeEvent>{new WorkTimeEvent {
            AtUtc="2026-11-01T10:00:00Z",Text="Saved",Detail=project.Title+" detail",DurationSeconds=2 }},Computer="test-PC" }} };
    private static string Employee(DataGrid grid) { var row=grid.ItemsSource?.Cast<object>().FirstOrDefault(); return row==null?"":Convert.ToString(row.GetType().GetProperty("Employee").GetValue(row)); }
    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach(object child in LogicalTreeHelper.GetChildren(root)) if(child is DependencyObject obj) foreach(var item in Descendants(obj)) yield return item;
    }
    [STAThread] private static int Main()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var a=Project("A"); var b=Project("B"); var c=Project("C"); WorkTimeRuntime.Current=a;
            WorkTimeRuntime.Provider=p=>Task.FromResult(History(p));
            var root=(Grid)WorkTimeUi.CreateTab(new UserControl(),(_,__)=>{});
            Check(WorkTimeRuntime.Requests==0,"Constructing hidden tab read Corel/history");
            using(var surface=new HwndSource(new HwndSourceParameters("Vanya work-time UI checks") { Width=360,Height=700,WindowStyle=0 }))
            {
                surface.RootVisual=root;
                var grids=root.Children.OfType<DataGrid>().ToArray(); var people=grids[0];var events=grids[1];
                var refresh=Descendants(root).OfType<Button>().Single(v=>Convert.ToString(v.Content)=="Обновить");
                Until(()=>Employee(people)=="QA-A","Visible tab did not show active document");
                Check(grids.Length==2&&people.Columns.Count==4&&events.Columns.Count==2,"Compact tables missing");
                Check(Descendants(root).OfType<Expander>().Any(v=>Convert.ToString(v.Header).Contains("сотрудник")),"Employee settings not in time tab");
                var pending=new TaskCompletionSource<WorkTimeHistory>(); WorkTimeRuntime.Provider=p=>p.ProjectId==a.ProjectId?pending.Task:Task.FromResult(History(p));
                refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Check(!refresh.IsEnabled,"Concurrent refresh was not gated");
                WorkTimeRuntime.Current=b;pending.SetResult(History(a,"STALE"));
                Until(()=>Employee(people)=="QA-B"&&refresh.IsEnabled,"Late previous-file response replaced active history");
                Check(Descendants(root).OfType<TextBlock>().Any(t=>t.Text=="B"),"File title did not follow active CDR");
                events.SelectedIndex=0;
                Check(Descendants(root).OfType<TextBlock>().Any(t=>t.Text.Contains("B detail")),"Selected event details missing");
                var pause=Descendants(root).OfType<Button>().Single(v=>Convert.ToString(v.Content)=="Пауза");pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(WorkTimeRuntime.Paused&&Convert.ToString(pause.Content)=="Продолжить","Pause state not reflected");
                var save=Descendants(root).OfType<Button>().Single(v=>Convert.ToString(v.Content)=="Сохранить сотрудника и учёт");
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(WorkTimeRuntime.Configures==1,"Employee preferences not saved through tab");
                root.Visibility=Visibility.Collapsed;int hidden=WorkTimeRuntime.Requests;refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();Check(WorkTimeRuntime.Requests==hidden,"Hidden tab kept reading history");
                var late=new TaskCompletionSource<WorkTimeHistory>();WorkTimeRuntime.Provider=p=>late.Task;
                root.Visibility=Visibility.Visible;Until(()=>!refresh.IsEnabled,"Showing tab did not resume refresh");root.Visibility=Visibility.Collapsed;
                late.SetResult(History(b,"HIDDEN LATE"));Until(()=>refresh.IsEnabled,"Hidden response did not finish");
                Check(Employee(people)=="QA-B","Hidden tab applied a late result");
                WorkTimeRuntime.Current=c;WorkTimeRuntime.Provider=p=>Task.FromResult(History(p));root.Visibility=Visibility.Visible;
                Until(()=>Employee(people)=="QA-C","Reopened tab retained previous file");
                WorkTimeRuntime.NoDocument=true;refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Until(()=>people.ItemsSource==null,"No-document error left stale employee table");WorkTimeRuntime.NoDocument=false;
                WorkTimeRuntime.Settings.Enabled=false;WorkTimeRuntime.RandomUnsaved=true;WorkTimeRuntime.Current=Project("Untitled");WorkTimeRuntime.Current.FilePath="";
                WorkTimeRuntime.Provider=p=>Task.FromResult(new WorkTimeHistory());int before=WorkTimeRuntime.Requests;
                refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
                Check(refresh.IsEnabled&&WorkTimeRuntime.Requests==before+1,"Disabled unsaved file caused endless identity retries");
                surface.RootVisual=null;hidden=WorkTimeRuntime.Requests;refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
                Check(WorkTimeRuntime.Requests==hidden,"Unloaded tab kept reading history");
            }
            Console.WriteLine("Work-time view checks passed: "+_checks+". Owned hidden WPF HWND; fake document/history providers; no Corel or network.");return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error);return 1; }
    }
}
