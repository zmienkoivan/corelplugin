using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    internal sealed class LetterTransferSet
    {
        public int Schema { get; set; }=1;
        public string Id { get; set; }=Guid.NewGuid().ToString("N");
        public string Name { get; set; }="Новый набор букв";
        public string Reference { get; set; }="";
        public string Notes { get; set; }="";
        public string Style { get; set; }="По референсу";
        public double HeightMm { get; set; }=20;
        public double SheetWidthMm { get; set; }=300;
        public double SheetHeightMm { get; set; }=450;
        public bool AutoLength { get; set; }=true;
        public int AlphaThreshold { get; set; }=130;
        public double GapMm { get; set; }=3;
        public double MarginMm { get; set; }=5;
        public int VariantsPerLetter { get; set; }=1;
        public int TargetCopies { get; set; }=100;
        public List<LetterTransferRow> Letters { get; set; }=LetterTransferRules.DefaultRows();
        public List<LetterTransferGlyph> Glyphs { get; set; }=new List<LetterTransferGlyph>();
        public List<LetterTransferJob> Jobs { get; set; }=new List<LetterTransferJob>();
    }
    internal sealed class LetterTransferRow
    {
        public string Letter { get; set; }
        public double Weight { get; set; }
        public int Copies { get; set; }
        public string Ready { get; set; }
    }
    internal sealed class LetterTransferGlyph
    {
        public string Id { get; set; }=Guid.NewGuid().ToString("N");
        public string Letter { get; set; }
        public string File { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Source { get; set; }
    }
    internal sealed class LetterTransferJob
    {
        public string Id { get; set; }=Guid.NewGuid().ToString("N");
        public string[] Letters { get; set; }
        public string Model { get; set; }="google/nano-banana-2";
        public string Resolution { get; set; }="4K";
        public string Reference { get; set; }
        public string Output { get; set; }
        public string Prompt { get; set; }
        public string Key { get; set; }="#FF00FF";
        public bool Reviewed { get; set; }
    }
    internal static class LetterTransferRules
    {
        internal const string Alphabet="АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ";
        // Editable starting weights for ordinary Russian text, not a measured
        // forecast for a customer's names or a particular transfer collection.
        private static readonly double[] Weights={8,1.6,4.5,1.7,3,8.5,.1,.9,1.6,7.4,1.2,3.5,4.4,3.2,6.7,11,2.8,4.7,5.5,6.3,2.6,.3,1,.5,1.4,.7,.4,.05,1.9,1.7,.3,.6,2};
        internal static List<LetterTransferRow> DefaultRows()
        {
            var rows=Alphabet.Select((letter,index)=>new LetterTransferRow{Letter=letter.ToString(),Weight=Weights[index]}).ToList();
            Allocate(rows,100);return rows;
        }
        internal static bool ValidLetter(string letter)=>letter!=null && letter.Length==1 && Alphabet.Contains(letter);
        internal static void Allocate(IList<LetterTransferRow> rows,int total)
        {
            if(total<rows.Count || total>5000)throw new ArgumentException("Для полного алфавита нужно от "+rows.Count+" до 5000 копий.");
            if(rows.Any(row=>!ValidLetter(row.Letter) || !Finite(row.Weight) || row.Weight<0 || row.Weight>1000) || rows.Select(row=>row.Letter).Distinct().Count()!=rows.Count)
                throw new ArgumentException("Проверьте буквы и частотные веса.");
            double sum=rows.Sum(row=>row.Weight);if(sum<=0)throw new ArgumentException("Укажите хотя бы один положительный вес.");
            int remaining=total-rows.Count;var fractions=new List<Tuple<int,double>>();
            for(int i=0;i<rows.Count;i++)
            {
                double ideal=remaining*rows[i].Weight/sum;int whole=(int)Math.Floor(ideal);
                rows[i].Copies=1+whole;fractions.Add(Tuple.Create(i,ideal-whole));
            }
            int missing=total-rows.Sum(row=>row.Copies);
            foreach(var fraction in fractions.OrderByDescending(value=>value.Item2).ThenBy(value=>value.Item1).Take(missing))rows[fraction.Item1].Copies++;
        }
        internal static void Validate(LetterTransferSet set)
        {
            if(set==null || set.Schema!=1 || !Guid.TryParseExact(set.Id,"N",out _) || String.IsNullOrWhiteSpace(set.Name) || set.Name.Length>120 || (set.Notes??"").Length>4000 || (set.Style??"").Length>4000)
                throw new ArgumentException("Некорректный файл набора букв.");
            if(set.Letters==null || set.Letters.Count!=33 || set.Letters.Any(row=>row==null || !ValidLetter(row.Letter) || row.Copies<0 || row.Copies>5000 || !Finite(row.Weight) || row.Weight<0 || row.Weight>1000) ||
                set.Letters.Select(row=>row.Letter).Distinct().Count()!=33 || set.Letters.Sum(row=>row.Copies)>5000)
                throw new ArgumentException("Проверьте таблицу русских заглавных букв и количество копий (до 5000).");
            if(!In(set.HeightMm,1,500) || !In(set.SheetWidthMm,20,2000) || !In(set.SheetHeightMm,20,7000) || !In(set.GapMm,0,50) || !In(set.MarginMm,0,100) ||
                set.SheetWidthMm<=2*set.MarginMm || (!set.AutoLength && set.SheetHeightMm<=2*set.MarginMm) || set.VariantsPerLetter<1 || set.VariantsPerLetter>4 || set.TargetCopies<33 || set.TargetCopies>5000 || set.AlphaThreshold<1 || set.AlphaThreshold>255)
                throw new ArgumentException("Проверьте размер букв, листа, поля и количество вариантов.");
            if(set.Glyphs==null || set.Glyphs.Count>264 || set.Glyphs.Any(g=>g==null || !Guid.TryParseExact(g.Id,"N",out _) || !ValidLetter(g.Letter) || g.Width<1 || g.Height<1 || g.Width>10000 || g.Height>10000 || (long)g.Width*g.Height>25000000) ||
                set.Glyphs.Select(g=>g.Id).Distinct().Count()!=set.Glyphs.Count || set.Jobs==null || set.Jobs.Count>200 || set.Jobs.Select(j=>j?.Id).Distinct().Count()!=set.Jobs.Count)
                throw new ArgumentException("Некорректные рисунки или история генераций набора.");
            foreach(var job in set.Jobs)if(job==null || !Guid.TryParseExact(job.Id,"N",out _) || job.Letters==null || job.Letters.Length<1 || job.Letters.Length>4 || job.Letters.Any(letter=>!ValidLetter(letter)) ||
                job.Letters.Distinct().Count()!=job.Letters.Length || (job.Model!="google/nano-banana-2" && job.Model!="google/nano-banana-pro") || (job.Resolution!="2K" && job.Resolution!="4K") || String.IsNullOrWhiteSpace(job.Prompt) || job.Prompt.Length>16000 || !LetterTransferImages.ValidKey(job.Key))
                throw new ArgumentException("Некорректное сохранённое задание ИИ.");
        }
        internal static bool Finite(double value)=>!Double.IsNaN(value) && !Double.IsInfinity(value);
        private static bool In(double value,double min,double max)=>Finite(value) && value>=min && value<=max;
    }
    internal sealed class LetterTransferPlacement
    {
        internal LetterTransferGlyph Glyph;
        internal int Page;
        internal double X,Y,Width,Height;
    }
    internal static class LetterTransferLayout
    {
        internal static double PageLimit(LetterTransferSet set)=>set.AutoLength?7000:set.SheetHeightMm;
        internal static double PageHeight(LetterTransferSet set,IEnumerable<LetterTransferPlacement> page)=>set.AutoLength?Math.Max(20,page.Max(p=>p.Y+p.Height)+set.MarginMm):set.SheetHeightMm;
        internal static List<LetterTransferPlacement> Build(LetterTransferSet set,bool readyOnly)
        {
            LetterTransferRules.Validate(set);var placements=new List<LetterTransferPlacement>();
            double x=set.MarginMm,y=set.MarginMm,rowHeight=0,length=PageLimit(set);int page=0;
            foreach(var row in set.Letters.OrderBy(row=>LetterTransferRules.Alphabet.IndexOf(row.Letter,StringComparison.Ordinal)))
            {
                var variants=set.Glyphs.Where(g=>g.Letter==row.Letter).ToArray();
                if(row.Copies>0 && variants.Length==0 && !readyOnly)throw new InvalidOperationException("Нет готовой буквы «"+row.Letter+"». Догенерируйте её, установите количество 0 или включите «Только готовые».");
                if(variants.Length==0)continue;
                for(int copy=0;copy<row.Copies;copy++)
                {
                    var glyph=variants[copy%variants.Length];double h=set.HeightMm,w=h*glyph.Width/glyph.Height;
                    if(w>set.SheetWidthMm-2*set.MarginMm || h>length-2*set.MarginMm)throw new InvalidOperationException("Буква «"+row.Letter+"» не помещается на лист при выбранной высоте.");
                    if(x+w>set.SheetWidthMm-set.MarginMm+.00001){x=set.MarginMm;y+=rowHeight+set.GapMm;rowHeight=0;}
                    if(y+h>length-set.MarginMm+.00001){page++;if(page>=200)throw new InvalidOperationException("Больше 200 листов. Уменьшите количество или размер букв либо увеличьте лист.");x=set.MarginMm;y=set.MarginMm;rowHeight=0;}
                    placements.Add(new LetterTransferPlacement{Glyph=glyph,Page=page,X=x,Y=y,Width=w,Height=h});x+=w+set.GapMm;rowHeight=Math.Max(rowHeight,h);
                }
            }
            return placements;
        }
    }
}
