using System;
using System.Windows;

namespace VanyaTools.Native
{
    // Coordinates are millimetres, with the preview's origin at the top left.
    internal sealed class PatternViewport
    {
        private Rect _bounds;
        private Size _size;
        private bool _fit = true;
        internal Point Center { get; private set; }
        internal double Scale { get; private set; }
        internal double FitScale { get; private set; }
        internal double Percent => FitScale > 0 ? Scale / FitScale * 100 : 100;
        internal Point Origin => new Point(_size.Width / 2 - Center.X * Scale, _size.Height / 2 - Center.Y * Scale);

        internal void Update(Rect bounds, Size size)
        {
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 || size.Width <= 0 || size.Height <= 0) return;
            _bounds = bounds; _size = size;
            FitScale = Math.Min(Math.Max(1, size.Width - 40) / bounds.Width, Math.Max(1, size.Height - 40) / bounds.Height);
            if (_fit || Scale <= 0)
            {
                Scale = FitScale;
                Center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            }
        }

        internal Point World(Point screen) => new Point(Center.X + (screen.X - _size.Width / 2) / Scale,
            Center.Y + (screen.Y - _size.Height / 2) / Scale);

        internal void ZoomAt(double factor, Point screen)
        {
            if (Scale <= 0 || factor <= 0 || Double.IsNaN(factor) || Double.IsInfinity(factor)) return;
            Point anchor = World(screen);
            Scale = Math.Max(FitScale * .1, Math.Min(Math.Max(FitScale * 64, 20), Scale * factor));
            Center = new Point(anchor.X - (screen.X - _size.Width / 2) / Scale,
                anchor.Y - (screen.Y - _size.Height / 2) / Scale);
            _fit = false;
        }

        internal void Pan(Vector delta)
        {
            if (Scale <= 0) return;
            Center = new Point(Center.X - delta.X / Scale, Center.Y - delta.Y / Scale);
            _fit = false;
        }

        internal void Fit() { _fit = true; Update(_bounds, _size); }
    }
}
