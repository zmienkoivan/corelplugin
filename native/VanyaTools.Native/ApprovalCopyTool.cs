using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class ApprovalCopyTool
    {
        private const string DefaultCaption =
            "ПОЖАЛУЙСТА ПРОВЕРЬТЕ ВИЗУАЛИЗАЦИЮ.\n" +
            "ОБРАТИТЕ ВНИМАНИЕ НА РАЗМЕР И ПОЛОЖЕНИЕ ИЗОБРАЖЕНИЯ.\n" +
            "НА ЦВЕТ ИЗДЕЛИЯ, СТОРОНУ ПЕЧАТИ И ЕГО ТИП.\n" +
            "ОТСУТСТВИЕ ПРЕТЕНЗИЙ И КОРРЕКТИРОВОК К ИЗОБРАЖЕНИЮ " +
            "ПОДТВЕРЖДАЕТ СОГЛАСИЕ С МАКЕТОМ И ЗАПУСК В РАБОТУ.";

        private readonly Action<string, bool> _status;
        private readonly TextBox _caption;
        private bool _busy;

        internal FrameworkElement SettingsView { get; }

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VanyaTools", "approval-copy.json");

        internal ApprovalCopyTool(Action<string, bool> status)
        {
            _status = status;
            var panel = new StackPanel { Margin = new Thickness(4, 9, 4, 5) };
            panel.Children.Add(new TextBlock { Text = "Подпись согласования",
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            _caption = new TextBox { Text = DefaultCaption, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, MinHeight = 120,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 4) };
            panel.Children.Add(_caption);
            var save = new Button { Content = "Сохранить подпись", MinHeight = 29 };
            save.Click += (_, __) =>
            {
                try { SaveSettings(true); }
                catch (Exception error) { _status("Подпись: " + error.Message, true); }
            };
            panel.Children.Add(save);
            SettingsView = panel;
            LoadSettings();
        }

        internal async void CopySelection()
        {
            if (_busy) return;
            _busy = true;
            _status("Подготовка визуализации для буфера…", false);
            await Dispatcher.Yield(DispatcherPriority.Background);
            string path = null;
            try
            {
                SaveSettings(false);
                string caption = _caption.Text.Trim();
                if (caption.Length == 0)
                    throw new InvalidOperationException("Введите подпись согласования в настройках.");
                path = AiSelectionCapture.Capture(5000, 16000000, true, false);
                BitmapSource image;
                using (var stream = File.OpenRead(path))
                {
                    var loaded = new BitmapImage();
                    loaded.BeginInit();
                    loaded.CacheOption = BitmapCacheOption.OnLoad;
                    loaded.StreamSource = stream;
                    loaded.EndInit();
                    loaded.Freeze();
                    image = loaded;
                }
                BitmapSource finished = AddCaption(image, caption);
                for (int attempt = 0; ; attempt++)
                {
                    try { Clipboard.SetImage(finished); break; }
                    catch (ExternalException) when (attempt < 2) { Thread.Sleep(80); }
                }
                _status("Визуализация " + finished.PixelWidth + " × " + finished.PixelHeight +
                    " px с подписью скопирована. Вставьте её в Telegram через Ctrl+V.", false);
            }
            catch (Exception error)
            {
                Log.Error("Approval image copy failed", error);
                _status("Копирование визуализации: " + error.Message, true);
            }
            finally
            {
                if (path != null) try { File.Delete(path); } catch { }
                _busy = false;
            }
        }

        private static BitmapSource AddCaption(BitmapSource image, string caption)
        {
            int width = Math.Max(1200, image.PixelWidth);
            int height = image.PixelHeight;
            double fontSize = Math.Max(20, Math.Min(90, width * 0.018));
            double padding = Math.Max(18, fontSize * 0.7);
            var text = new FormattedText(caption.ToUpper(CultureInfo.GetCultureInfo("ru-RU")),
                CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold,
                    FontStretches.Normal), fontSize, Brushes.Red, 1.0)
            {
                MaxTextWidth = Math.Max(1, width - 2 * padding),
                LineHeight = fontSize * 1.2
            };
            int outputHeight = checked(height + (int)Math.Ceiling(text.Height + 2 * padding));
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, outputHeight));
                drawing.DrawImage(image, new Rect((width - image.PixelWidth) / 2.0,
                    0, image.PixelWidth, height));
                drawing.DrawText(text, new Point(padding, height + padding));
            }
            var bitmap = new RenderTargetBitmap(width, outputHeight, 96, 96,
                PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private void SaveSettings(bool report)
        {
            string caption = _caption.Text.Trim();
            if (caption.Length == 0)
                throw new InvalidOperationException("Подпись не может быть пустой.");
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            string json = new JavaScriptSerializer().Serialize(new Dictionary<string, string>
                { ["caption"] = caption });
            File.WriteAllText(SettingsPath, json, Encoding.UTF8);
            if (report) _status("Подпись согласования сохранена.", false);
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var settings = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(SettingsPath, Encoding.UTF8));
                if (settings != null && settings.TryGetValue("caption", out string caption) &&
                    !String.IsNullOrWhiteSpace(caption)) _caption.Text = caption;
            }
            catch (Exception error) { Log.Error("Could not load approval caption", error); }
        }
    }
}
