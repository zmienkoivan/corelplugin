using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class PatternFillerReviewWindow : Window
    {
        private readonly PatternFillerResult _result;
        private readonly CheckBox[] _use;
        private readonly Button _add;

        internal BitmapSource[] Selected => Enumerable.Range(0, _result.Cells.Length)
            .Where(i => _result.Cells[i] != null && _use[i].IsChecked == true).Select(i => _result.Cells[i]).ToArray();

        internal PatternFillerReviewWindow(BitmapSource reference, PatternFillerResult result)
        {
            _result = result;
            _use = new CheckBox[result.Cells.Length];
            Title = "Проверка ИИ-фигур · Vanya Tools";
            Width = Math.Min(1000, SystemParameters.WorkArea.Width - 28);
            Height = Math.Min(760, SystemParameters.WorkArea.Height - 28);
            MinWidth = 650; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            DockerTheme.Apply(this);
            var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
            var footer = new DockPanel { Margin = new Thickness(0, 9, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            var back = Button("Назад", (_, __) => Close()); back.IsCancel = true;
            DockPanel.SetDock(back, Dock.Right); footer.Children.Add(back);
            _add = DockerTheme.Primary(Button("Добавить в набор", (_, __) => { if (Selected.Length > 0) DialogResult = true; }));
            DockPanel.SetDock(_add, Dock.Right); footer.Children.Add(_add);
            footer.Children.Add(Button("Сохранить лист…", (_, __) => SaveSheet()));

            var note = new TextBlock { Text = "Выделено фигур: " + result.Count + " из " + result.Cells.Length + ". Проверьте стиль и отметьте подходящие.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            if (result.Count < result.Cells.Length) DockerTheme.UseTheme(note, TextBlock.ForegroundProperty, DockerTheme.ErrorTextBrush);
            DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
            var referencePanel = new StackPanel { Width = 170, Margin = new Thickness(0, 0, 12, 0) };
            DockPanel.SetDock(referencePanel, Dock.Left); root.Children.Add(referencePanel);
            referencePanel.Children.Add(new TextBlock { Text = "Исходный стиль", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7) });
            referencePanel.Children.Add(new Image { Source = reference, Stretch = Stretch.Uniform, MaxHeight = 420 });

            var tabs = new TabControl(); root.Children.Add(tabs);
            var cards = new UniformGrid { Columns = PatternFillerBrief.Columns(result.Cells.Length) };
            tabs.Items.Add(new TabItem { Header = "Фигуры", Content = cards });
            tabs.Items.Add(new TabItem { Header = "Лист ИИ", Content = new Image { Source = result.Sheet, Stretch = Stretch.Uniform } });
            for (int i = 0; i < result.Cells.Length; i++)
            {
                var card = new DockPanel { Margin = new Thickness(8) };
                _use[i] = new CheckBox { IsChecked = result.Cells[i] != null, IsEnabled = result.Cells[i] != null,
                    Content = new TextBlock { Text = (i + 1) + ". " + result.Names[i], TextWrapping = TextWrapping.Wrap } };
                _use[i].Checked += (_, __) => Update(); _use[i].Unchecked += (_, __) => Update();
                DockPanel.SetDock(_use[i], Dock.Top); card.Children.Add(_use[i]);
                if (result.Representative[i])
                {
                    var recovered = new TextBlock { Text = "Целый элемент из повторов", FontSize = 11, Margin = new Thickness(0, 4, 0, 4) };
                    DockerTheme.UseTheme(recovered, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush);
                    DockPanel.SetDock(recovered, Dock.Top); card.Children.Add(recovered);
                }
                if (result.Cells[i] != null) card.Children.Add(new Image { Source = result.Cells[i], Stretch = Stretch.Uniform, Margin = new Thickness(0, 8, 0, 0) });
                else card.Children.Add(new TextBlock { Text = "Не удалось выделить целую фигуру. Исходник доступен во вкладке «Лист ИИ».",
                    TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                var border = new Border { Child = card, BorderThickness = new Thickness(1), Margin = new Thickness(3) };
                DockerTheme.UseTheme(border, Border.BorderBrushProperty, DockerTheme.BorderBrush);
                DockerTheme.UseTheme(border, Border.BackgroundProperty, DockerTheme.SurfaceBackground);
                cards.Children.Add(border);
            }
            Update();
        }

        private void Update()
        {
            if (_use.Any(value => value == null)) return;
            int count = Selected.Length;
            _add.Content = "Добавить в набор · " + count; _add.IsEnabled = count > 0;
        }

        private void SaveSheet()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG|*.png", DefaultExt = ".png",
                FileName = "ИИ-заполнители-лист.png", AddExtension = true };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(_result.Sheet));
                using (var stream = System.IO.File.Create(dialog.FileName)) encoder.Save(stream);
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Сохранение листа", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private static Button Button(string text, RoutedEventHandler click)
        {
            var result = new Button { Content = text, MinHeight = 34, Margin = new Thickness(2), Padding = new Thickness(7, 4, 7, 4) };
            result.Click += click; return result;
        }
    }
}
