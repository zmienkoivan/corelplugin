using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal sealed class LetterTransferReviewDraft
    {
        public string Fingerprint { get; set; }
        public string CellsFingerprint { get; set; }
        public string[] Labels { get; set; }
        public bool[] Manual { get; set; }
        public bool[] Excluded { get; set; }
        private static string FingerprintOf(string file)
        {using(var sha=SHA256.Create())using(var stream=File.OpenRead(file))return Convert.ToBase64String(sha.ComputeHash(stream));}
        private static string CellsOf(LetterTransferSheet sheet)
        {
            using(var sha=SHA256.Create())using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream))
            {
                writer.Write(sheet.Cells.Length);
                foreach(var cell in sheet.Cells)
                {
                    var image=cell.Original??cell.Image??cell.Preview;writer.Write(image!=null);
                    if(image==null)continue;writer.Write(image.PixelWidth);writer.Write(image.PixelHeight);writer.Write(LetterTransferImages.Pixels(image));
                }
                writer.Flush();return Convert.ToBase64String(sha.ComputeHash(stream.ToArray()));
            }
        }
        internal static void Restore(string output,LetterTransferSheet sheet)
        {
            string path=output+".review.json";if(!File.Exists(path))return;
            try
            {
                if(new FileInfo(path).Length>65536)return;
                var draft=new JavaScriptSerializer().Deserialize<LetterTransferReviewDraft>(File.ReadAllText(path));
                int n=sheet.Cells.Length;
                if(draft?.Fingerprint!=FingerprintOf(output) || draft.Labels?.Length!=n || draft.Manual?.Length!=n || draft.Excluded?.Length!=n)return;
                if(draft.CellsFingerprint!=null?draft.CellsFingerprint!=CellsOf(sheet):sheet.ContentLayout)return;
                for(int i=0;i<n;i++)
                {
                    var cell=sheet.Cells[i];cell.Excluded=draft.Excluded[i];
                    if(!draft.Manual[i])continue;
                    cell.Letter=LetterTransferRules.ValidLetter(draft.Labels[i])?draft.Labels[i]:null;cell.Manual=true;cell.RecognitionUncertain=false;
                }
            }
            catch(Exception error)when(error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException){Log.Error("Could not restore letter review draft.",error);}
        }
        internal static void Save(string output,LetterTransferSheet sheet)
        {
            if(!sheet.Cells.Any(c=>c.Manual || c.Excluded))return;
            var draft=new LetterTransferReviewDraft{Fingerprint=FingerprintOf(output),CellsFingerprint=CellsOf(sheet),Labels=sheet.Cells.Select(c=>c.Letter).ToArray(),Manual=sheet.Cells.Select(c=>c.Manual).ToArray(),Excluded=sheet.Cells.Select(c=>c.Excluded).ToArray()};
            string path=output+".review.json",temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllText(temporary,new JavaScriptSerializer().Serialize(draft));if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
}
