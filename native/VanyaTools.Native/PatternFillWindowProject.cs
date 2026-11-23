using System;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace VanyaTools.Native
{
    internal sealed partial class PatternFillWindow
    {
        internal Func<PatternDraft,PatternEditorSettings,PatternProjectEdits,string,Task<string>> SaveProjectRequested;
        private Button _saveProject;
        private string _projectPath;
        private bool _projectBusy;
        private void InitializeProject(Panel tools)
        {
            _saveProject=Button("Сохранить проект…",async(_,__)=>
            {
                if(_projectBusy || _rendering || _refresh.IsEnabled || Draft==null || Settings==null)return;
                if(SaveProjectRequested==null){ShowMessage("Сохранение проекта недоступно в этом окне.",true);return;}
                _projectBusy=true;_saveProject.IsEnabled=false;
                try
                {
                    string path=await SaveProjectRequested(Draft,Settings,_preview.SaveProjectEdits(),_projectPath);
                    if(path!=null){_projectPath=path;if(_ready)ShowMessage("Проект сохранён: "+System.IO.Path.GetFileName(path),false);}
                }
                catch(Exception error){Log.Error("Could not save editable pattern project.",error);if(_ready)ShowMessage("Проект не сохранён: "+error.Message,true);}
                finally{_projectBusy=false;if(_ready)_saveProject.IsEnabled=!_rendering && !_refresh.IsEnabled && Draft!=null;}
            });
            _saveProject.ToolTip="Сохранить исходные рисунки, размеры, раскладку, повороты и опоры в один файл .vpattern для продолжения работы.";
            tools.Children.Add(_saveProject);
        }
    }
}
