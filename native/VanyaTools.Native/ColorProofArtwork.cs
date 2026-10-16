using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VanyaTools.Native
{
    internal sealed class ColorProofArtwork
    {
        internal sealed class Bounds
        {
            public double Left, Bottom, Width, Height;
            public double Top => Bottom + Height;
        }

        private object _document;
        private readonly List<object> _shapes = new List<object>();
        private readonly List<int> _ids = new List<int>();
        public ColorProofReference Reference { get; private set; }
        public double WidthMm { get; private set; }
        public double HeightMm { get; private set; }

        public static ColorProofArtwork Capture()
        {
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте CDR и выделите одноцветный логотип.");
            dynamic selected = app.ActiveSelectionRange;
            if ((int)selected.Count == 0) throw new InvalidOperationException("Выделите логотип, текст или группу объектов.");
            var artwork = new ColorProofArtwork { _document = (object)doc };
            object firstColor = null;
            foreach (dynamic shape in selected)
            {
                Visit((object)shape, leaf => {
                    dynamic item = leaf;
                    if ((int)item.Type == CorelConstants.CdrBitmapShape)
                        throw new InvalidOperationException("Для окраски нужен векторный логотип или текст. Растровое изображение сначала преобразуйте в кривые.");
                    int fill = (int)item.Fill.Type;
                    if (fill != 0 && fill != 1)
                        throw new InvalidOperationException("Для цветопробы логотипа нужны однородные заливки без градиентов.");
                    if (firstColor == null && fill == 1) firstColor = (object)item.Fill.UniformColor;
                    if (firstColor == null && (int)item.Outline.Type != 0) firstColor = (object)item.Outline.Color;
                });
                artwork._shapes.Add((object)shape);
                artwork._ids.Add((int)shape.StaticID);
            }
            if (firstColor == null) throw new InvalidOperationException("В выделении нет видимой заливки или обводки для окраски.");
            dynamic color = app.CreateColor(); color.CopyAssign(firstColor);
            artwork.Reference = new ColorProofReference { Color = color, Name = "Цвет логотипа", IsSpot = Convert.ToBoolean(color.IsSpot) };
            int oldUnit = (int)doc.Unit;
            bool oldDirty = Convert.ToBoolean(doc.Dirty);
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                Bounds bounds = artwork.Measure((object)doc);
                artwork.WidthMm = bounds.Width; artwork.HeightMm = bounds.Height;
            }
            finally { try { doc.Unit = oldUnit; } finally { doc.Dirty = oldDirty; } }
            return artwork;
        }

        public Bounds Measure(object document)
        {
            object range = Sources(document);
            Bounds bounds = ReadBounds(range);
            if (bounds.Width <= 0 || bounds.Height <= 0 || Double.IsNaN(bounds.Width + bounds.Height) ||
                Double.IsInfinity(bounds.Width + bounds.Height))
                throw new InvalidOperationException("Не удалось измерить логотип. Проверьте выделенные объекты.");
            return bounds;
        }

        public object Copy(object document, object layer, object color, double left, double top)
        {
            dynamic copies = ((dynamic)Sources(document)).CopyToLayer(layer);
            dynamic shape = (int)copies.Count == 1 ? copies[1] : copies.Group();
            if (color != null)
                Visit((object)shape, leaf => {
                    dynamic item = leaf;
                    if ((int)item.Fill.Type == 1) item.Fill.ApplyUniformFill(color);
                    if ((int)item.Outline.Type != 0) item.Outline.Color.CopyAssign(color);
                });
            Bounds bounds = ReadBounds((object)shape);
            shape.Move(left - bounds.Left, top - bounds.Top);
            return (object)shape;
        }

        private object Sources(object document)
        {
            if (!SameObject(document, _document))
                throw new InvalidOperationException("Откройте CDR с выбранным логотипом или возьмите образец из выделения заново.");
            try
            {
                dynamic range = CorelApp.Get().CreateShapeRange();
                for (int i = 0; i < _shapes.Count; i++)
                {
                    dynamic shape = _shapes[i];
                    if ((int)shape.StaticID != _ids[i]) throw new InvalidOperationException();
                    range.Add(shape);
                }
                return (object)range;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Логотип был удалён или документ закрыт. Выделите его и возьмите образец заново.", error);
            }
        }

        private static Bounds ReadBounds(object shapeOrRange)
        {
            double left, bottom, width, height;
            ((dynamic)shapeOrRange).GetBoundingBox(out left, out bottom, out width, out height, true);
            return new Bounds { Left = left, Bottom = bottom, Width = width, Height = height };
        }

        private static bool SameObject(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            IntPtr first = IntPtr.Zero, second = IntPtr.Zero;
            try { first = Marshal.GetIUnknownForObject(a); second = Marshal.GetIUnknownForObject(b); return first == second; }
            finally { if (first != IntPtr.Zero) Marshal.Release(first); if (second != IntPtr.Zero) Marshal.Release(second); }
        }

        private static void Visit(object shape, Action<object> action)
        {
            dynamic item = shape;
            if ((int)item.Type == CorelConstants.CdrGroupShape)
            {
                foreach (dynamic child in item.Shapes) Visit((object)child, action);
            }
            else action(shape);
            object clip = null;
            try { clip = (object)item.PowerClip; } catch (COMException) { }
            if (clip != null)
                foreach (dynamic child in ((dynamic)clip).Shapes) Visit((object)child, action);
        }
    }

    internal sealed class ColorProofSheetOptions
    {
        public double WidthMm = 570;
        public ColorProofArtwork Artwork;
    }
}
