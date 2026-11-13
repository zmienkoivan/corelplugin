using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VanyaTools.Native
{
    internal static class FrameBagDxf
    {
        // Closed 2D polylines for outer contours and zipper openings. Guides/text are
        // absent from the laser file. INSUNITS=4 and coordinates are millimetres.
        internal static string Create(FrameBagPlan plan)
        {
            if (plan == null || plan.Parts == null || plan.Parts.Length < 3) throw new ArgumentException("Сначала постройте выкройку.");
            var text = new StringBuilder();
            void Pair(int code, object value) { text.Append(code.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                .Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append("\r\n"); }
            Pair(0, "SECTION"); Pair(2, "HEADER");
            Pair(9, "$ACADVER"); Pair(1, "AC1015");
            Pair(9, "$INSUNITS"); Pair(70, 4); Pair(9, "$MEASUREMENT"); Pair(70, 1); Pair(0, "ENDSEC");
            Pair(0, "SECTION"); Pair(2, "TABLES");
            Pair(0, "TABLE"); Pair(2, "LTYPE"); Pair(5, "10"); Pair(330, "0"); Pair(100, "AcDbSymbolTable"); Pair(70, 1);
            Pair(0, "LTYPE"); Pair(5, "11"); Pair(330, "10"); Pair(100, "AcDbSymbolTableRecord"); Pair(100, "AcDbLinetypeTableRecord");
            Pair(2, "CONTINUOUS"); Pair(70, 0); Pair(3, "Solid line"); Pair(72, 65); Pair(73, 0); Pair(40, 0);
            Pair(0, "ENDTAB"); Pair(0, "TABLE"); Pair(2, "LAYER"); Pair(5, "12"); Pair(330, "0"); Pair(100, "AcDbSymbolTable"); Pair(70, 1);
            Pair(0, "LAYER"); Pair(5, "13"); Pair(330, "12"); Pair(100, "AcDbSymbolTableRecord"); Pair(100, "AcDbLayerTableRecord"); Pair(2, "CUT"); Pair(70, 0); Pair(62, 1); Pair(6, "CONTINUOUS");
            Pair(0, "ENDTAB"); Pair(0, "ENDSEC");
            Pair(0, "SECTION"); Pair(2, "ENTITIES");
            int handle = 256;
            foreach (var part in plan.Parts)
            {
                foreach (var nodes in new[] { part.Cut }.Concat(part.Cutouts))
                {
                Pair(0, "LWPOLYLINE"); Pair(5, (handle++).ToString("X", CultureInfo.InvariantCulture));
                Pair(100, "AcDbEntity"); Pair(8, "CUT"); Pair(62, 1); Pair(100, "AcDbPolyline");
                Pair(90, nodes.Length); Pair(70, 1);
                foreach (var point in nodes)
                {
                    Pair(10, (part.Left + point.X).ToString("0.######", CultureInfo.InvariantCulture));
                    Pair(20, (plan.KitHeight - part.Top - point.Y).ToString("0.######", CultureInfo.InvariantCulture));
                }
                }
            }
            Pair(0, "ENDSEC"); Pair(0, "EOF");
            return text.ToString();
        }
    }
}
