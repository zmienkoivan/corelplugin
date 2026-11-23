using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class PatternProjectPackage
    {
        internal PatternProjectData Data;
        internal BitmapSource[] Previews;
        internal string NativePath,Directory;
    }
    internal static class PatternProjectArchive
    {
        internal const long MaximumNativeBytes=512L*1024*1024;
        private const int MaximumJson=64*1024*1024;
        internal static PatternProjectData Capture(PatternEditorSession session,PatternEditorSettings settings,PatternDraft draft,PatternProjectEdits edits,out PatternAsset[] sources)
        {
            sources=session.Motifs.Concat(session.Hero==null?Array.Empty<PatternAsset>():new[]{session.Hero}).GroupBy(a=>a.Id).Select(g=>g.First()).ToArray();
            var index=sources.Select((a,i)=>new{a.Id,Index=i}).ToDictionary(a=>a.Id,a=>a.Index);
            return new PatternProjectData{
                Bottle=session.BottleMode,Whole=draft.BottleWhole,Width=draft.Frame.Width,Height=draft.Frame.Height,
                Settings=PatternProjectData.Values(settings),Layout=PatternProjectData.Values(settings.Layout),
                Sources=sources.Select(a=>new PatternProjectSource{Id=a.Id,Label=a.Label,Width=a.Size.X,Height=a.Size.Y,Filler=a.IsFiller,
                    Hull=(a.Footprint??new PatternFootprint(a.Size)).Hull.Select(p=>new[]{p.X,p.Y}).ToArray(),Silhouette=PatternProjectGeometry.From(a.Silhouette),OutlineFallback=a.OutlineFallback}).ToArray(),
                Motifs=session.Motifs.Select(a=>index[a.Id]).ToArray(),Hero=session.Hero==null?-1:index[session.Hero.Id],
                DraftSources=draft.Motifs.Select(a=>index[a.Id]).ToArray(),DraftMirrors=draft.Motifs.Select(a=>a.Mirrored).ToArray(),Mirrors=draft.MirroredIds?.ToArray()??session.MirroredIds,
                Items=draft.Items.Select(PatternProjectPose.From).ToArray(),Main=PatternProjectPose.From(draft.Hero),Edits=edits,
                Reserve=PatternProjectGeometry.From(draft.Reserve),CellGuides=PatternProjectGeometry.From(draft.CellGuides),RepeatGuides=PatternProjectGeometry.From(draft.RepeatGuides),
                CenterMarks=PatternProjectGeometry.From(draft.CenterMarks),CutView=PatternProjectGeometry.From(draft.BottleCutView),SeamGuide=PatternProjectGeometry.From(draft.BottleSeamGuide)};
        }
        internal static void Save(string path,PatternProjectData data,PatternAsset[] sources,string nativePath)
        {
            Validate(data);
            var native=new FileInfo(nativePath);if(!native.Exists || native.Length==0 || native.Length>MaximumNativeBytes)throw new InvalidDataException("Не удалось сохранить исходники Corel проекта.");
            string target=Path.GetFullPath(path),temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))
                {
                    var json=Encoding.UTF8.GetBytes(new JavaScriptSerializer{MaxJsonLength=MaximumJson}.Serialize(data));
                    if(json.Length>MaximumJson)throw new InvalidDataException("Слишком много данных в проекте паттерна.");
                    using(var output=zip.CreateEntry("manifest.json",CompressionLevel.Fastest).Open())output.Write(json,0,json.Length);
                    using(var input=File.OpenRead(nativePath))using(var output=zip.CreateEntry("sources.cdr",CompressionLevel.NoCompression).Open())input.CopyTo(output);
                    for(int i=0;i<sources.Length;i++)
                    {
                        var image=sources[i].Preview;if(image==null)continue;
                        double scale=Math.Min(1,1024d/Math.Max(image.PixelWidth,image.PixelHeight));
                        if(scale<1){var reduced=new TransformedBitmap(image,new ScaleTransform(scale,scale));reduced.Freeze();image=reduced;}
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
                        // WIC encoders require a seekable stream; a ZIP entry is forward-only.
                        using(var memory=new MemoryStream())
                        {encoder.Save(memory);memory.Position=0;using(var output=zip.CreateEntry("preview/"+i+".png",CompressionLevel.NoCompression).Open())memory.CopyTo(output);}
                    }
                }
                if(File.Exists(target))File.Replace(temporary,target,null);else File.Move(temporary,target);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        internal static PatternProjectPackage Read(string path,string directory)
        {
            Directory.CreateDirectory(directory);
            using(var input=File.OpenRead(path))using(var zip=new ZipArchive(input,ZipArchiveMode.Read))
            {
                if(zip.Entries.Count>70 || zip.Entries.GroupBy(e=>e.FullName,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new InvalidDataException("Повреждён архив проекта паттерна.");
                var manifest=zip.GetEntry("manifest.json");if(manifest==null || manifest.Length>MaximumJson)throw new InvalidDataException("Не найдены настройки проекта паттерна.");
                PatternProjectData data;
                using(var reader=new StreamReader(manifest.Open(),Encoding.UTF8))data=new JavaScriptSerializer{MaxJsonLength=MaximumJson}.Deserialize<PatternProjectData>(reader.ReadToEnd());
                Validate(data);
                var native=zip.GetEntry("sources.cdr");if(native==null || native.Length==0 || native.Length>MaximumNativeBytes)throw new InvalidDataException("Не найдены исходные рисунки Corel.");
                string name=Path.GetFileNameWithoutExtension(path);foreach(char c in Path.GetInvalidFileNameChars())name=name.Replace(c,'_');if(name.Length>70)name=name.Substring(0,70);
                string nativePath=Path.Combine(directory,name+".cdr");
                using(var source=native.Open())using(var output=new FileStream(nativePath,FileMode.CreateNew,FileAccess.Write,FileShare.None))source.CopyTo(output);
                var previews=new BitmapSource[data.Sources.Length];
                for(int i=0;i<previews.Length;i++)
                {
                    var entry=zip.GetEntry("preview/"+i+".png");if(entry==null)continue;
                    if(entry.Length>16*1024*1024)throw new InvalidDataException("Слишком большое превью в проекте.");
                    using(var stream=entry.Open())using(var memory=new MemoryStream())
                    {
                        stream.CopyTo(memory);var bytes=memory.ToArray();
                        // Check the PNG dimensions before WIC allocates the decoded bitmap.
                        if(bytes.Length<24 || !bytes.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}) ||
                            Encoding.ASCII.GetString(bytes,12,4)!="IHDR")throw new InvalidDataException("Повреждено превью проекта.");
                        long Dimension(int offset)=>((long)bytes[offset]<<24)|((long)bytes[offset+1]<<16)|((long)bytes[offset+2]<<8)|bytes[offset+3];
                        long w=Dimension(16),h=Dimension(20);
                        if(w<=0 || h<=0 || w>4096 || h>4096 || w*h>16000000)throw new InvalidDataException("Превью проекта слишком велико.");
                        memory.Position=0;var bitmap=BitmapFrame.Create(memory,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);bitmap.Freeze();previews[i]=bitmap;
                    }
                }
                return new PatternProjectPackage{Data=data,Previews=previews,NativePath=nativePath,Directory=directory};
            }
        }
        internal static void Validate(PatternProjectData p)
        {
            if(p==null || p.Schema!=1)throw new InvalidDataException("Версия проекта паттерна не поддерживается.");
            if(p.Sources==null || p.Sources.Length>65 || p.Sources.Any(s=>s==null) || p.Sources.Select(s=>s.Id).Distinct().Count()!=p.Sources.Length || p.Motifs==null || p.Motifs.Length>64 ||
                p.DraftSources==null || p.DraftSources.Length>128 || p.DraftMirrors==null || p.DraftMirrors.Length!=p.DraftSources.Length || p.Items==null || p.Items.Length>PatternFillLayout.MaximumElements ||
                p.Hero< -1 || p.Hero>=p.Sources.Length || p.Mirrors==null || p.Mirrors.Length>64 || !PatternProjectPose.Valid(p.Width) || !PatternProjectPose.Valid(p.Height) || p.Width<=0 || p.Height<=0 ||
                p.Motifs.Concat(p.DraftSources).Any(i=>i<0 || i>=p.Sources.Length) || p.Items.Length>0 && p.DraftSources.Length==0 || p.Main!=null && p.Hero<0)
                throw new InvalidDataException("Повреждён состав проекта паттерна.");
            foreach(var source in p.Sources)
            {
                if(source==null || !PatternProjectPose.Valid(source.Width) || !PatternProjectPose.Valid(source.Height) || source.Width<=0 || source.Height<=0 || source.Hull==null || source.Hull.Length<3 || source.Hull.Length>10000 ||
                    source.Hull.Any(v=>v==null || v.Length!=2 || !PatternProjectPose.Valid(v[0]) || !PatternProjectPose.Valid(v[1])))throw new InvalidDataException("Повреждён исходный рисунок проекта.");
                source.Silhouette?.Read();
            }
            var settings=new PatternEditorSettings();PatternProjectData.ReadValues(settings,p.Settings);PatternProjectData.ReadValues(settings.Layout,p.Layout);
            if(settings.Allowance<0 || settings.Allowance>100 || settings.Layout.SizeMm<=0 || settings.Layout.GapMm<0 || settings.Layout.MinScale<=0 || settings.Layout.MaxScale<=0)
                throw new InvalidDataException("Некорректные размеры паттерна в проекте.");
            if(p.Bottle)new PatternBottle(settings.BottleTop,settings.BottleBottom,settings.BottleHeight);
            if(settings.Layout.Texture)p.Edits?.Validate(p.DraftSources.Length,p.Items.Length);
            foreach(var pose in p.Items){if(pose==null)throw new InvalidDataException("Пустое положение рисунка в проекте.");pose.Read(p.DraftSources.Length);}p.Main?.Read(1);
            p.Reserve?.Read();p.CellGuides?.Read();p.RepeatGuides?.Read();p.CenterMarks?.Read();p.CutView?.Read();p.SeamGuide?.Read();
        }
    }
}
