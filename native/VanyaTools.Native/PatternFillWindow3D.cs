using System.Windows.Controls;

namespace VanyaTools.Native
{
    internal sealed partial class PatternFillWindow
    {
        private PatternBic3DWindow _bic3D;
        private void InitializeBic3D(Panel tools)
        {
            if(!_session.BottleMode)return;
            var button=Button("3D · BIC",(_,__)=>
            {
                if(_rendering || _refresh.IsEnabled || Draft==null){ShowMessage("Дождитесь готового паттерна перед открытием 3D.",false);return;}
                if(_bic3D==null)
                {
                    _bic3D=new PatternBic3DWindow(_session.Hero,_preview.PreviewImage){Owner=this};
                    _bic3D.Closed+=(_,__)=>_bic3D=null;
                    _bic3D.Show();
                }
                else _bic3D.Activate();
                _bic3D.Queue(Draft);
            });
            button.ToolTip="Повернуть зажигалку с текущим паттерном и проверить стык. Корпус — по размерам развёртки; головка показана условно.";
            tools.Children.Add(button);
            _preview.MotifSelectionChanged+=()=>{if(!_rendering && !_refresh.IsEnabled && Draft!=null)_bic3D?.Queue(Draft);};
        }
    }
}
