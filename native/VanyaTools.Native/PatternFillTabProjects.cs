using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace VanyaTools.Native
{
    internal sealed partial class PatternFillTab
    {
        private bool _openingProject;
        private async void OpenProject()
        {
            if(_openingProject)return;
            var dialog=new Microsoft.Win32.OpenFileDialog{Title="Открыть проект паттерна",Filter="Проект паттерна (*.vpattern)|*.vpattern",CheckFileExists=true};
            if(dialog.ShowDialog()!=true)return;
            _openingProject=true;string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VanyaTools","PatternProjects",Guid.NewGuid().ToString("N"));
            bool opened=false;
            try
            {
                Report("Открываю проект паттерна…",false);
                var package=await Task.Run(()=>PatternProjectArchive.Read(dialog.FileName,directory));
                PatternProjectOpened project;
                using(DockerUiWatchdog.Trace(Dispatcher,"Open pattern project sources"))project=PatternProjectNative.Open((object)CorelApp.Get(),package,dialog.FileName);
                opened=true;_service.UseProjectSources(project.Session);RefreshMotifs();
                _hero.Text=project.Session.Hero==null?"Главный принт не выбран":"Главный элемент из проекта";
                _forgetHero.Visibility=project.Session.Hero==null?Visibility.Collapsed:Visibility.Visible;
                ShowEditor(project.Session,project.Settings,project);
            }
            catch(Exception error){Log.Error("Could not open pattern project.",error);Report("Проект не открыт: "+error.GetBaseException().Message,true);}
            finally{_openingProject=false;if(!opened)TryRemove(directory);}
        }
        private void ShowEditor(PatternEditorSession session,PatternEditorSettings initial,PatternProjectOpened project=null)
        {
            var window=new PatternFillWindow(session,initial,project);
            var owner=PresentationSource.FromVisual(this) as HwndSource;if(owner!=null)new WindowInteropHelper(window).Owner=owner.Handle;
            window.ApplyRequested+=draft=>{using(DockerUiWatchdog.Trace(Dispatcher,"Pattern insertion"))Report("Вариант вставлен · объектов: "+_service.Apply(session,draft,keepEditor:true),false);};
            window.SaveProjectRequested=async(draft,settings,edits,existing)=>
            {
                string path=existing;
                if(String.IsNullOrWhiteSpace(path))
                {
                    var save=new Microsoft.Win32.SaveFileDialog{Title="Сохранить проект паттерна",Filter="Проект паттерна (*.vpattern)|*.vpattern",DefaultExt=".vpattern",AddExtension=true,FileName=session.BottleMode?"Паттерн BIC.vpattern":"Паттерн.vpattern"};
                    if(save.ShowDialog(window)!=true)return null;path=save.FileName;
                }
                if(!String.Equals(Path.GetExtension(path),".vpattern",StringComparison.OrdinalIgnoreCase))path+=".vpattern";
                string workspace=Path.Combine(Path.GetTempPath(),"Vanya-pattern-project-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(workspace);
                try
                {
                    var data=PatternProjectArchive.Capture(session,settings,draft,edits,out var sources);
                    string native;
                    using(DockerUiWatchdog.Trace(Dispatcher,"Save pattern project native sources"))native=PatternProjectNative.Capture(session,sources,workspace);
                    await Task.Run(()=>PatternProjectArchive.Save(path,data,sources,native));return path;
                }
                finally{TryRemove(workspace);}
            };
            window.ShowDialog();if(window.Settings!=null){if(session.BottleMode)_bottleSettings=window.Settings;else _settings=window.Settings;}
        }
        private static void TryRemove(string path)
        {try{if(Directory.Exists(path))Directory.Delete(path,true);}catch(Exception error){Log.Error("Could not remove pattern project workspace.",error);}}
    }
}
