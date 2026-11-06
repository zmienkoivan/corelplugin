using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace VanyaTools.Native
{
    internal sealed partial class DtfCorrectionWindow
    {
        private Button _savePreset, _loadPreset;
        private bool _presetBusy;
        private void CreatePresetControls(Panel controls)
        {
            var row = new UniformGrid { Columns=2, Margin=new Thickness(0,4,0,6) };
            _savePreset = Button("Сохранить пресет…", async (_,__) => await SavePresetAsync());
            _loadPreset = Button("Загрузить пресет…", async (_,__) => await LoadPresetAsync());
            _savePreset.FontSize = _loadPreset.FontSize = 10;
            _savePreset.ToolTip = "Сохранить коррекцию, обводку, нокаут и халфтонинг в переносимый файл. Размеры — в миллиметрах.";
            _loadPreset.ToolTip = "Загрузить параметры из файла. Предпросмотр пересчитается автоматически.";
            row.Children.Add(_savePreset); row.Children.Add(_loadPreset); controls.Children.Add(row);
        }
        private DtfCorrectionPreset CapturePreset()
        {
            if (!ReadOptions(out DtfCorrectionOptions options)) throw new InvalidOperationException("Исправьте параметры перед сохранением пресета.");
            var preset = DtfCorrectionPreset.FromOptions(options); preset.ToOptions(); return preset;
        }
        private void ApplyPreset(DtfCorrectionPreset preset)
        {
            var options = preset.ToOptions();
            // Validate source-DPI limits before changing a single control.
            if (options.Outline)
            {
                long pixels = (long)_originalPixels.Width * _originalPixels.Height;
                int scale = !options.SmoothStairs ? 1 : pixels<=2000000 ? 4 : pixels<=3500000 ? 3 : pixels<=8000000 ? 2 : 1;
                DtfOutlineUnits.Pixels(options.RadiusMm,true,_originalPixels.DpiX*scale,DtfOutlineUnits.MaxRadiusPixels,"Сглаживание пресета");
                DtfOutlineUnits.Pixels(options.RadiusMm,true,_originalPixels.DpiY*scale,DtfOutlineUnits.MaxRadiusPixels,"Сглаживание пресета");
                DtfOutlineUnits.Pixels(options.ExpansionMm,true,_originalPixels.DpiX*scale,DtfOutlineUnits.MaxExpansionPixels,"Обводка пресета");
                DtfOutlineUnits.Pixels(options.ExpansionMm,true,_originalPixels.DpiY*scale,DtfOutlineUnits.MaxExpansionPixels,"Обводка пресета");
            }
            _syncing=true; _preparationSync=true;
            try
            {
                Options=options.Copy(); _knockoutPick.IsChecked=false;
                _units.SelectedIndex=0; _previousUnit=0;
                _remove.IsChecked=options.RemoveSemiTransparent; _threshold.Text=options.Threshold.ToString(); _thresholdSlider.Value=options.Threshold;
                _stairs.IsChecked=options.SmoothStairs; _outline.IsChecked=options.Outline;
                _radius.Text=DtfOutlineUnits.Format(options.RadiusMm); _expansion.Text=DtfOutlineUnits.Format(options.ExpansionMm); _hex.Text=Hex(options.Color);
                _whiten.IsChecked=options.WhitenNearWhite; _nearWhiteTolerance.Value=options.NearWhiteTolerance;
                _knockout.IsChecked=options.Knockout; _knockoutHex.Text=Hex(options.KnockoutColor); _knockoutTolerance.Value=options.KnockoutTolerance;
                _knockoutSoft.IsChecked=options.KnockoutSoft; _screenShape.SelectedIndex=(int)options.HalftoneShape;
                _halftone.IsChecked=options.Halftone; _screenMode.SelectedIndex=options.HalftoneAlphaOnly?0:1;
                _screenLpi.Text=DtfOutlineUnits.Format(options.HalftoneLpi); _screenAngle.Text=options.HalftoneAngle.ToString(System.Globalization.CultureInfo.CurrentCulture);
                _screenDot.Text=DtfOutlineUnits.Format(options.HalftoneDotMm); _automaticDot=options.HalftoneAutoDot;
                _screenBoost.Text=options.HalftoneColorBoost.ToString();
                _screenAuto.Content=_automaticDot?"Авто ✓":"Авто";
            }
            finally { _syncing=false; _preparationSync=false; }
            UpdateAutomaticDot(); QueuePreview();
        }
        private async Task SavePresetAsync()
        {
            if (_presetBusy || !_loaded || _closed || _applying) return;
            try
            {
                var preset=CapturePreset();
                var dialog=new SaveFileDialog { Title="Сохранить пресет коррекции", FileName="ДТФ — коррекция.vanya-correction.json",
                    Filter="Пресет Vanya Tools (*.vanya-correction.json)|*.vanya-correction.json|JSON (*.json)|*.json", DefaultExt="vanya-correction.json", AddExtension=true, OverwritePrompt=true };
                if (dialog.ShowDialog(this)!=true) return;
                _presetBusy=true; UpdatePresetAvailability(); string path=dialog.FileName;
                await Task.Run(()=>DtfCorrectionPreset.Save(path,preset));
                if (!_closed) ShowMessage("Пресет сохранён: "+Path.GetFileName(path),false);
            }
            catch(Exception error) { if(!_closed) ShowMessage("Пресет не сохранён: "+error.GetBaseException().Message,true); }
            finally { _presetBusy=false; if(!_closed) UpdatePresetAvailability(); }
        }
        private async Task LoadPresetAsync()
        {
            if (_presetBusy || !_loaded || _closed || _applying) return;
            try
            {
                var dialog=new OpenFileDialog { Title="Загрузить пресет коррекции",
                    Filter="Пресет Vanya Tools (*.vanya-correction.json)|*.vanya-correction.json|JSON (*.json)|*.json", CheckFileExists=true };
                if(dialog.ShowDialog(this)!=true) return;
                _presetBusy=true; UpdatePresetAvailability(); string path=dialog.FileName;
                var preset=await Task.Run(()=>DtfCorrectionPreset.Load(path));
                if(!_closed && !_applying) { ApplyPreset(preset); ShowMessage("Пресет загружен: "+Path.GetFileName(path),false); }
            }
            catch(Exception error) { if(!_closed) ShowMessage("Пресет не загружен: "+error.GetBaseException().Message,true); }
            finally { _presetBusy=false; if(!_closed) UpdatePresetAvailability(); }
        }
        private void UpdatePresetAvailability()
        {
            if(_savePreset==null) return;
            _savePreset.IsEnabled=_loadPreset.IsEnabled=_loaded && !_closed && !_applying && !_normalizing && !_presetBusy;
        }
    }
}
