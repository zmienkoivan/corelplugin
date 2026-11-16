using System;
using System.Linq;

namespace VanyaTools.Native
{
    internal static class LetterTransferOrder
    {
        // Repeated designs stay on the same letter; the next unique letter
        // advances one position. Existing labels are anchors, never discarded.
        internal static string[] Assign(string[] requested,string[] labels,out string notice)
        {
            var alphabet=requested.Distinct().ToArray();int n=labels.Length,k=alphabet.Length;
            bool Fits(int i,int j)=>!LetterTransferRules.ValidLetter(labels[i]) || labels[i]==alphabet[j];
            var forward=new bool[n,k];var backward=new bool[n,k];
            if(n<k || k==0){notice="Ячеек меньше, чем запрошенных букв. Назначьте имеющиеся кнопками алфавита.";return (string[])labels.Clone();}
            forward[0,0]=Fits(0,0);
            for(int i=1;i<n;i++)for(int j=0;j<k;j++)forward[i,j]=Fits(i,j) && (forward[i-1,j] || j>0 && forward[i-1,j-1]);
            if(!forward[n-1,k-1]){notice="Обозначения не согласуются с порядком алфавита. Проверьте выбранные буквы; существующие обозначения сохранены.";return (string[])labels.Clone();}
            backward[n-1,k-1]=Fits(n-1,k-1);
            for(int i=n-2;i>=0;i--)for(int j=0;j<k;j++)backward[i,j]=Fits(i,j) && (backward[i+1,j] || j+1<k && backward[i+1,j+1]);
            var result=(string[])labels.Clone();int unknown=0;
            for(int i=0;i<n;i++)
            {
                var possible=Enumerable.Range(0,k).Where(j=>forward[i,j] && backward[i,j]).ToArray();
                if(possible.Length==1)result[i]=alphabet[possible[0]];else unknown++;
            }
            notice=unknown==0?"Порядок назначен. Повторы сохранены как варианты одной буквы.":"Осталось неоднозначных ячеек: "+unknown+". Обозначьте повторяющиеся буквы кнопками алфавита и снова нажмите «По порядку».";
            return result;
        }
    }
}
