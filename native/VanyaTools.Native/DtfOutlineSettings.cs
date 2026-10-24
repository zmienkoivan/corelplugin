using System;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace VanyaTools.Native
{
    internal sealed class DtfOutlineSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "dtf-outline.json");
        public double ThinMm { get; set; } = 0.175;
        public double ThickMm { get; set; } = 0.3;
        internal event Action Changed;

        internal static DtfOutlineSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var saved = new JavaScriptSerializer().Deserialize<DtfOutlineSettings>(File.ReadAllText(SettingsPath));
                    if (saved != null && ValidPreset(saved.ThinMm) && ValidPreset(saved.ThickMm)) return saved;
                }
            }
            catch (Exception error) { Log.Error("Could not load DTF outline presets.", error); }
            return new DtfOutlineSettings();
        }

        private static bool ValidPreset(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0.001 && value <= 5;

        internal FrameworkElement CreateSettingsView(Action<string, bool> status)
        {
            var panel = new StackPanel { Margin = new Thickness(4) };
            var thin = AddField(panel, "Тонкая обводка, мм", ThinMm);
            var thick = AddField(panel, "Толстая обводка, мм", ThickMm);
            var save = new Button { Content = "Сохранить пресеты обводки", Margin = new Thickness(0, 3, 0, 2) };
            save.Click += (_, __) =>
            {
                if (!DtfOutlineUnits.TryNumber(thin.Text, out double first) || !ValidPreset(first) ||
                    !DtfOutlineUnits.TryNumber(thick.Text, out double second) || !ValidPreset(second))
                { status("Значения пресетов обводки: от 0,001 до 5 мм.", true); return; }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                    File.WriteAllText(SettingsPath, new JavaScriptSerializer().Serialize(new DtfOutlineSettings {
                        ThinMm = first, ThickMm = second }));
                    ThinMm = first; ThickMm = second; Changed?.Invoke();
                    status("Пресеты обводки сохранены.", false);
                }
                catch (Exception error)
                { Log.Error("Could not save DTF outline presets.", error); status("Пресеты не сохранены: " + error.Message, true); }
            };
            panel.Children.Add(save);
            DockerHelp.Add(panel, "Значения задают наружное расширение с каждой стороны детали. " +
                "Иконки тонкого и толстого контура в окне «Коррекция и обводка» задают размер обводки; результат вставляется по кнопке «Применить в Corel». " +
                "Миллиметры пересчитываются по DPI захваченного изображения и округляются до целого пикселя. " +
                "Положительное значение занимает хотя бы один пиксель. Радиус сглаживания задаётся отдельно.");
            return new Expander { Header = "Пресеты обводки ДТФ", Content = panel, IsExpanded = false,
                Margin = new Thickness(0, 0, 0, 6) };
        }

        private static TextBox AddField(Panel panel, string title, double value)
        {
            var field = new TextBox { Text = DtfOutlineUnits.Format(value), FontSize = 11,
                MinHeight = 25, VerticalContentAlignment = VerticalAlignment.Center };
            panel.Children.Add(DockerLayout.FieldRow(title, field, 85)); return field;
        }
    }

    internal static class DtfOutlineUnits
    {
        internal const int MaxRadiusPixels = 12, MaxExpansionPixels = 100;
        internal static string Format(double value) => value.ToString("0.######", CultureInfo.CurrentCulture);
        internal static bool TryNumber(string text, out double value) =>
            Double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= 0;

        internal static int Pixels(double value, bool millimeters, double dpi, int maximum, string name)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 0 ||
                Double.IsNaN(dpi) || Double.IsInfinity(dpi) || dpi <= 0)
                throw new InvalidOperationException("Не удалось рассчитать размер обводки и DPI.");
            double pixels = millimeters ? value * dpi / 25.4 : value;
            if (pixels > maximum)
                throw new InvalidOperationException(name + ": не более " + maximum + " px (" +
                    Format(maximum * 25.4 / dpi) + " мм при " + Format(dpi) + " DPI).");
            return value == 0 ? 0 : Math.Max(1, (int)Math.Round(pixels, MidpointRounding.AwayFromZero));
        }
    }
}
