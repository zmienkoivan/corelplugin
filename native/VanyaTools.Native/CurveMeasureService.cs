using System;
using System.Globalization;
using System.Reflection;

namespace VanyaTools.Native
{
    internal sealed class CurveMeasurement
    {
        internal string DocumentName, ShapeName;
        internal int SubPathIndex, FirstNode, SecondNode;
        internal bool Closed;
        internal double SmallerMm, LargerMm;
    }

    internal static class CurveMeasureService
    {
        private static object Property(object target, string name, params object[] arguments) =>
            target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target,
                arguments, CultureInfo.InvariantCulture);

        private static object Method(object target, string name, params object[] arguments) =>
            target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target,
                arguments, CultureInfo.InvariantCulture);

        private static int Integer(object target, string name) => Convert.ToInt32(Property(target, name));
        private static double Number(object target, string name) => Convert.ToDouble(Property(target, name));
        private static bool Finite(double number) => !Double.IsNaN(number) && !Double.IsInfinity(number);

        private static string OptionalName(object target, string fallback)
        {
            try
            {
                string name = Convert.ToString(Property(target, "Name"));
                return String.IsNullOrWhiteSpace(name) ? fallback : name;
            }
            catch { return fallback; }
        }

        internal static CurveMeasurement MeasureSelection()
        {
            object app = CorelApp.Get();
            object document = Property(app, "ActiveDocument");
            if (document == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            object selection = Property(app, "ActiveSelectionRange");
            if (selection != null && Integer(selection, "Count") > 1)
                throw new InvalidOperationException("Выберите одну кривую и два узла на ней инструментом «Форма» (F10).");
            object shape = Property(app, "ActiveShape");
            if (shape == null)
                throw new InvalidOperationException("Выберите кривую и два её узла инструментом «Форма» (F10).");
            if (Integer(shape, "Type") != 3) // cdrCurveShape
                throw new InvalidOperationException("Выберите саму кривую. При необходимости преобразуйте объект в кривые (Ctrl+Q).");

            object curve = Property(shape, "Curve");
            object selected = Method(curve, "Selection");
            int selectedCount = selected == null ? 0 : Integer(selected, "Count");
            if (selectedCount != 2)
                throw new InvalidOperationException("Выбрано узлов: " + selectedCount +
                    ". Нужно ровно два узла одного контура. Выделите их инструментом «Форма» (F10).");
            object first = Property(selected, "Item", 1), second = Property(selected, "Item", 2);
            int pathIndex = Integer(first, "SubPathIndex");
            if (pathIndex != Integer(second, "SubPathIndex"))
                throw new InvalidOperationException("Узлы находятся на разных подконтурах. Выберите два узла одной непрерывной кривой.");
            int a = Integer(first, "Index"), b = Integer(second, "Index");
            if (a == b) throw new InvalidOperationException("Выберите два разных узла.");
            if (a > b) { int swap = a; a = b; b = swap; }
            object path = Property(first, "SubPath");
            int nodeCount = Integer(Property(path, "Nodes"), "Count");
            bool closed = Convert.ToBoolean(Property(path, "Closed"));
            object segments = Property(path, "Segments");
            int segmentCount = Integer(segments, "Count");
            if (a < 1 || b > nodeCount || segmentCount != nodeCount - (closed ? 0 : 1))
                throw new InvalidOperationException("Не удалось определить последовательность узлов контура. Повторите выделение.");

            double between = 0, compensation = 0;
            for (int index = a; index < b; index++)
            {
                object segment = Property(segments, "Item", index);
                if (Integer(Property(segment, "StartNode"), "Index") != index ||
                    Integer(Property(segment, "EndNode"), "Index") != index + 1)
                    throw new InvalidOperationException("Нарушена последовательность сегментов контура. Повторите выделение.");
                double length = Number(segment, "Length");
                if (!Finite(length) || length < 0)
                    throw new InvalidOperationException("CorelDRAW вернул некорректную длину сегмента.");
                // Compensated summation avoids losing short segments on long paths.
                double corrected = length - compensation;
                double next = between + corrected;
                compensation = (next - between) - corrected;
                between = next;
            }
            double total = Number(path, "Length");
            if (!Finite(total) || !Finite(between) || total < 0 ||
                between > total + Math.Max(1e-8, total * 1e-7))
                throw new InvalidOperationException("CorelDRAW вернул несовместимые длины контура и сегментов.");
            between = Math.Min(between, total);
            int units = Integer(document, "Unit");
            double directMm = Convert.ToDouble(Method(app, "ConvertUnits", between, units, 3));
            double otherMm = closed ? Convert.ToDouble(Method(app, "ConvertUnits", total - between, units, 3)) : 0;
            if (!Finite(directMm) || !Finite(otherMm) || directMm < 0 || otherMm < 0)
                throw new InvalidOperationException("Не удалось перевести длину в миллиметры.");
            return new CurveMeasurement {
                DocumentName = OptionalName(document, "Документ"),
                ShapeName = OptionalName(shape, "Кривая"),
                SubPathIndex = pathIndex, FirstNode = a, SecondNode = b, Closed = closed,
                SmallerMm = closed ? Math.Min(directMm, otherMm) : directMm,
                LargerMm = closed ? Math.Max(directMm, otherMm) : 0
            };
        }
    }
}
