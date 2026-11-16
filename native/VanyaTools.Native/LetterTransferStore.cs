using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal sealed class LetterTransferLibraryItem
    {
        internal string Directory;
        public string Name { get; set; }
        public override string ToString()=>Name;
    }
    internal static class LetterTransferStore
    {
        internal static string DefaultRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VanyaTools","LetterTransfers");
        internal static string Resolve(string directory,string relative)
        {
            if(String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || !relative.EndsWith(".png",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Некорректный путь рисунка в наборе.");
            string root=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string full=Path.GetFullPath(Path.Combine(root,relative));
            if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Рисунок находится вне папки набора.");
            return full;
        }
        internal static void Save(string directory,LetterTransferSet set)
        {
            LetterTransferRules.Validate(set);ValidateFiles(directory,set);
            Directory.CreateDirectory(directory);string destination=Path.Combine(directory,"set.json"),temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
            string json=new JavaScriptSerializer{MaxJsonLength=1024*1024}.Serialize(set);
            try
            {
                File.WriteAllText(temporary,json,new UTF8Encoding(false));
                if(File.Exists(destination))File.Replace(temporary,destination,destination+".bak");else File.Move(temporary,destination);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        internal static LetterTransferSet Load(string directory)
        {
            string path=Path.Combine(directory,"set.json");if(!File.Exists(path) || new FileInfo(path).Length>1024*1024)throw new InvalidDataException("Файл набора не найден или слишком велик.");
            string json=File.ReadAllText(path,Encoding.UTF8);var serializer=new JavaScriptSerializer{MaxJsonLength=1024*1024};
            var fields=serializer.DeserializeObject(json) as Dictionary<string,object>;
            if(fields==null || !fields.ContainsKey("Schema") || !fields.ContainsKey("Id") || !fields.ContainsKey("Name") || !fields.ContainsKey("Letters") || !fields.ContainsKey("Glyphs"))
                throw new InvalidDataException("Это не файл сохранённого набора букв.");
            var set=serializer.Deserialize<LetterTransferSet>(json);
            LetterTransferRules.Validate(set);ValidateFiles(directory,set);
            foreach(var glyph in set.Glyphs.Where(g=>g.BodyHeight==0 && (g.Letter=="Ё" || g.Letter=="Й")))
            {
                string image=Resolve(directory,glyph.File);if(!File.Exists(image))continue;
                try{glyph.BodyHeight=LetterTransferImages.BodyHeight(glyph.Letter,LetterTransferImages.Load(image));}
                catch(Exception error)when(error is IOException || error is NotSupportedException || error is ArgumentException){ }
            }
            return set;
        }
        private static void ValidateFiles(string directory,LetterTransferSet set)
        {
            if(!String.IsNullOrEmpty(set.Reference))Resolve(directory,set.Reference);
            foreach(var glyph in set.Glyphs){Resolve(directory,glyph.File);if(!String.IsNullOrEmpty(glyph.OriginalFile))Resolve(directory,glyph.OriginalFile);}
            foreach(var job in set.Jobs){Resolve(directory,job.Reference);Resolve(directory,job.Output);}
        }
        internal static List<LetterTransferLibraryItem> List(string root)
        {
            var result=new List<LetterTransferLibraryItem>();if(!Directory.Exists(root))return result;
            foreach(string directory in Directory.EnumerateDirectories(root).Take(1000))
            {
                try{var set=Load(directory);result.Add(new LetterTransferLibraryItem{Directory=directory,Name=set.Name});}
                catch(Exception error)when(error is IOException || error is InvalidDataException || error is ArgumentException || error is InvalidOperationException || error is UnauthorizedAccessException){ }
            }
            return result.OrderBy(item=>item.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        internal static string Create(string root,LetterTransferSet set)
        {
            string directory=Path.Combine(root,set.Id);Directory.CreateDirectory(directory);Save(directory,set);return directory;
        }
    }
}
