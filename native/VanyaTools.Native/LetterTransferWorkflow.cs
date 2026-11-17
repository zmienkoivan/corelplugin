using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class LetterTransferBatchPlan
    {
        internal string Model;
        internal string[][] Passes;
        internal string[] Resolutions,Prompts;
        internal int Generations=>Passes.Length;
        internal int Analyses=>Passes.Length;
        internal string Description=>"До "+Generations+" платных запросов создания букв · "+LetterTransferModels.Name(Model)+"; до "+Analyses+" платных распознаваний · GPT mini.\n"+
            "Фон, край и раскладка обрабатываются локально. Затем — одна общая проверка букв. Автоматических повторных генераций после ошибки не будет.";
    }
    internal sealed class LetterTransferPrepared
    {
        internal LetterTransferJob Job;
        internal BitmapSource Source;
        internal LetterTransferSheet Sheet;
        internal LetterTransferRecognitionPlan Recognition;
        internal bool NeedsRecognition;
    }
    internal static class LetterTransferWorkflow
    {
        internal static Task<T> Work<T>(Func<T> action,CancellationToken cancel)
        {
            var done=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread=new Thread(()=>{try{cancel.ThrowIfCancellationRequested();done.TrySetResult(action());}catch(OperationCanceledException){done.TrySetCanceled();}catch(Exception error){done.TrySetException(error);}}){IsBackground=true};
            thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
        }
        internal static void EdgePreset(LetterTransferSet set,int mode)
        {
            if(mode<0 || mode>1)throw new ArgumentOutOfRangeException(nameof(mode));
            set.AlphaThreshold=130;set.EdgeTrimPx=0;set.SmoothEdges=mode==0;set.SharpContour=mode==0;set.ContourSmoothPx=.75;
        }
        internal static int EdgeMode(LetterTransferSet set)
        {
            if(set.AlphaThreshold==130 && set.EdgeTrimPx==0 && set.SmoothEdges && set.SharpContour && Math.Abs(set.ContourSmoothPx-.75)<.001)return 0;
            if(set.AlphaThreshold==130 && set.EdgeTrimPx==0 && !set.SmoothEdges && !set.SharpContour)return 1;
            return 2;
        }
        internal static BitmapSource ContactSheet(IEnumerable<BitmapSource> images)
        {
            var items=images.ToArray();if(items.Length==1)return items[0];int columns=(int)Math.Ceiling(Math.Sqrt(items.Length)),rows=(items.Length+columns-1)/columns;
            const int cell=520;var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White,null,new Rect(0,0,cell*columns,cell*rows));
                for(int i=0;i<items.Length;i++){var image=items[i];double scale=Math.Min((cell-12d)/image.PixelWidth,(cell-12d)/image.PixelHeight);double w=image.PixelWidth*scale,h=image.PixelHeight*scale;dc.DrawImage(image,new Rect(i%columns*cell+(cell-w)/2,i/columns*cell+(cell-h)/2,w,h));}
            }
            var result=new RenderTargetBitmap(cell*columns,cell*rows,96,96,PixelFormats.Pbgra32);result.Render(visual);result.Freeze();return result;
        }
        internal static LetterTransferSheet Combine(IReadOnlyList<LetterTransferPrepared> sheets)
        {
            var cells=sheets.SelectMany(p=>p.Sheet.Cells).OrderBy(c=>LetterTransferRules.ValidLetter(c.Letter)?LetterTransferRules.Alphabet.IndexOf(c.Letter,StringComparison.Ordinal):Int32.MaxValue).ToArray();
            return new LetterTransferSheet{Cells=cells,Requested=cells.Select(c=>c.Letter).ToArray(),LayoutChanged=sheets.Any(s=>s.Sheet.LayoutChanged),Notice="Листов: "+sheets.Count+". Варианты одной буквы стоят рядом. Проверьте край и обозначения; сомнительные буквы не отмечены автоматически. "+String.Join(" ",sheets.Select(s=>s.Sheet.Notice).Where(n=>!String.IsNullOrWhiteSpace(n)).Distinct())};
        }
    }
}
