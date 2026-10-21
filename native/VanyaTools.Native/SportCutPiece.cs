using System;
using System.Collections.Generic;

namespace VanyaTools.Native
{
    internal static class SportCutPiece
    {
        internal const string LabelPrefix = "VT_CUT_TAG_LABEL_";

        private static void Read(object item, IList<object> contours, IList<object> labels, int depth = 0)
        {
            if (depth > 12) throw new InvalidOperationException("Слишком глубокая группа детали резки.");
            dynamic shape = item;
            if (Convert.ToInt32(shape.Type) == CorelConstants.CdrGroupShape)
            {
                foreach (dynamic child in shape.Shapes) Read((object)child, contours, labels, depth + 1);
                return;
            }
            if ((Convert.ToString(shape.Name) ?? "").StartsWith(LabelPrefix, StringComparison.Ordinal)) labels.Add(item);
            else contours.Add(item);
        }

        // Rebuild only a cutting copy. AppendCurve keeps disconnected subpaths;
        // it neither welds bridges shut nor draws links between notches.
        internal static object Combine(object application, object rootObject)
        {
            var contours = new List<object>(); var labels = new List<object>();
            Read(rootObject, contours, labels);
            if (contours.Count == 0) throw new InvalidOperationException("В детали нет контура резки.");
            if (contours.Count == 1) return rootObject;
            dynamic app = application, root = rootObject, layer = root.Layer;
            string name = Convert.ToString(root.Name) ?? "";
            double left = Convert.ToDouble(root.LeftX), bottom = Convert.ToDouble(root.BottomY),
                width = Convert.ToDouble(root.SizeWidth), height = Convert.ToDouble(root.SizeHeight);
            dynamic curve = app.ActiveDocument.CreateCurve();
            dynamic style = contours[0];
            foreach (object item in contours)
            {
                dynamic shape = item;
                if (String.Equals(Convert.ToString(shape.Name), "silhouette", StringComparison.OrdinalIgnoreCase)) style = shape;
                curve.AppendCurve(shape.DisplayCurve.GetCopy());
            }
            object color = style.Outline.Color.GetCopy();
            double outlineWidth = Convert.ToDouble(style.Outline.Width);
            var created = new List<object>();
            object result = null;
            try
            {
                dynamic contour = layer.CreateCurve(curve);
                created.Add((object)contour);
                contour.Name = "VT_CUT_CONTOUR";
                contour.Fill.ApplyNoFill(); contour.Outline.Width = outlineWidth;
                contour.Outline.Color.CopyAssign(color);
                dynamic range = app.CreateShapeRange(); range.Add((object)contour);
                foreach (object item in labels)
                {
                    dynamic label = ((dynamic)item).CopyToLayer(layer);
                    created.Add((object)label); range.Add((object)label);
                }
                if (labels.Count == 0) result = (object)contour;
                else
                {
                    dynamic previousLayer = app.ActiveLayer;
                    try
                    {
                        layer.Activate();
                        result = (object)range.Group();
                        dynamic grouped = result;
                        if (!String.Equals(Convert.ToString(grouped.Layer.Name), Convert.ToString(layer.Name), StringComparison.Ordinal))
                            grouped.MoveToLayer((object)layer);
                    }
                    finally { previousLayer.Activate(); }
                }
                dynamic combined = result;
                if (Math.Abs(Convert.ToDouble(combined.SizeWidth) - width) > .01 ||
                    Math.Abs(Convert.ToDouble(combined.SizeHeight) - height) > .01 ||
                    Math.Abs(Convert.ToDouble(combined.LeftX) - left) > .01 ||
                    Math.Abs(Convert.ToDouble(combined.BottomY) - bottom) > .01)
                    throw new InvalidOperationException("При объединении изменилась геометрия детали; исходная копия сохранена.");
                combined.Name = name;
                root.Delete();
                return result;
            }
            catch
            {
                if (result != null) try { ((dynamic)result).Delete(); } catch { }
                else foreach (object item in created) try { ((dynamic)item).Delete(); } catch { }
                throw;
            }
        }

        internal static object Root(object item)
        {
            dynamic shape = item;
            for (int depth = 0; depth <= 12; depth++)
            {
                string name = Convert.ToString(shape.Name) ?? "";
                if (name.StartsWith("VT_CUT_", StringComparison.Ordinal) && name.Length > 7 && Char.IsDigit(name[7]))
                    return (object)shape;
                dynamic parent = shape.ParentGroup;
                if (parent == null) return (object)shape;
                shape = parent;
            }
            throw new InvalidOperationException("Слишком глубокая группа детали резки.");
        }

        internal static int CombineCurrentPage()
        {
            dynamic app = CorelApp.Get(), document = app.ActiveDocument;
            if (document == null) throw new InvalidOperationException("Откройте страницу раскладки резки.");
            var roots = new Dictionary<long, object>();
            foreach (object curve in SportLayoutService.CuttingPageCurves((object)document.ActivePage))
            {
                dynamic root = Root(curve);
                long id = Convert.ToInt64(root.StaticID);
                if (!roots.ContainsKey(id)) roots.Add(id, (object)root);
            }
            int oldUnit = Convert.ToInt32(document.Unit), count = 0;
            bool optimization = Convert.ToBoolean(app.Optimization), dirty = Convert.ToBoolean(document.Dirty);
            bool attempted = false;
            document.BeginCommandGroup("Vanya Tools — объединить контуры деталей резки");
            try
            {
                document.Unit = CorelConstants.CdrMillimeter; app.Optimization = true;
                foreach (object root in roots.Values)
                {
                    var contours = new List<object>(); var labels = new List<object>();
                    Read(root, contours, labels);
                    if (contours.Count <= 1) continue;
                    attempted = true;
                    Combine((object)app, root); count++;
                }
                return count;
            }
            finally
            {
                try { document.EndCommandGroup(); }
                finally
                {
                    try { document.Unit = oldUnit; if (!attempted) document.Dirty = dirty; }
                    finally { app.Optimization = optimization; }
                }
            }
        }
    }
}
