using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VanyaTools.Native
{
    internal sealed partial class ColorProofTab
    {
        private Window _pantoneWindow;

        private void ShowPantoneMatches()
        {
            if (_pantoneWindow != null) { _pantoneWindow.Activate(); return; }
            var window = new Window { Title = "Близкие Pantone · Vanya Tools", Width = 570, Height = 660,
                MinWidth = 420, MinHeight = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            DockerTheme.Apply(window);
            var root = new DockPanel { Margin = new Thickness(12) }; window.Content = root;
            var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            header.Children.Add(Title("Pantone Solid Coated"));
            header.Children.Add(Note("Поиск по ΔE00 в Lab среди плашечных цветов установленной палитры Corel. Меньшее ΔE — ближе к эталону."));
            var palettePath = Field(""); palettePath.IsReadOnly = true; palettePath.ToolTip = "Пусто — найти установленную палитру автоматически.";
            var paletteRow = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var browse = Button("Палитра…", (_, __) => {
                var dialog = new OpenFileDialog { Title = "Палитра Pantone Solid Coated из Corel",
                    Filter = "Палитры Corel (*.xml;*.cpl)|*.xml;*.cpl|Все файлы (*.*)|*.*", CheckFileExists = true };
                if (dialog.ShowDialog(window) == true) palettePath.Text = dialog.FileName;
            });
            DockPanel.SetDock(browse, Dock.Right); paletteRow.Children.Add(browse);
            var automatic = Button("Авто", (_, __) => palettePath.Text = "");
            DockPanel.SetDock(automatic, Dock.Right); paletteRow.Children.Add(automatic);
            paletteRow.Children.Add(palettePath); header.Children.Add(paletteRow);
            var count = Combo("3 цвета", "5 цветов", "8 цветов", "12 цветов"); count.SelectedIndex = 1;
            var search = Button("Подобрать от эталона", null);
            header.Children.Add(GridPair("Показать", count, "", search));
            var status = Note("Эталон берётся из полей CMYK/RGB вкладки «Цветопроба» при нажатии «Подобрать». Цвета печати проверяйте по пробе.");
            header.Children.Add(status);
            var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            footer.Children.Add(Note("В Corel создаётся отдельный лист: исходный эталон и выбранные плашечные заливки из палитры, с именами и ΔE00."));
            var create = DockerTheme.Primary(Button("Создать лист с выбранными цветами", null)); create.IsEnabled = false;
            footer.Children.Add(create);
            var list = new StackPanel(); root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            ColorProofPantoneResult result = null;
            var choices = new List<Tuple<CheckBox, ColorProofPantoneMatch>>();
            var cancellation = new CancellationTokenSource();
            search.Click += async (_, __) => {
                search.IsEnabled = browse.IsEnabled = automatic.IsEnabled = count.IsEnabled = create.IsEnabled = false;
                list.Children.Clear(); choices.Clear(); result = null;
                status.Text = "Ищу плашечные цвета в палитре Corel…";
                DockerTheme.UseTheme(status, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush);
                try
                {
                    bool cmyk = _model.SelectedIndex == 0;
                    string[] names = GridChannelNames(cmyk);
                    int[] values = Enumerable.Range(0, names.Length).Select(channel => GridInteger(_gridBase[channel], 0,
                        cmyk ? 100 : 255, names[channel])).ToArray();
                    int amount = new[] { 3, 5, 8, 12 }[Math.Max(0, count.SelectedIndex)];
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (!window.IsVisible) return;
                    // Corel objects stay on this UI thread. Only cloned colors are converted for comparison.
                    result = await new ColorProofPantoneService().FindAsync(cmyk, values, amount, palettePath.Text.Trim(),
                        cancellation.Token, (done, total) => status.Text = "Проверяю цвета Pantone: " + done + " / " + total + "…");
                    var reference = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 8) };
                    reference.Children.Add(PantoneChip(result.ReferenceScreenRgb));
                    reference.Children.Add(Note("Эталон · " + String.Join(" ", values.Select((value, channel) => names[channel] + ":" + value))));
                    list.Children.Add(reference);
                    foreach (ColorProofPantoneMatch match in result.Matches)
                    {
                        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                        var selected = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                        DockPanel.SetDock(selected, Dock.Left); row.Children.Add(selected);
                        var chip = PantoneChip(match.ScreenRgb); DockPanel.SetDock(chip, Dock.Left); row.Children.Add(chip);
                        row.Children.Add(new TextBlock { Text = match.Name + "\nΔE00 " + match.DeltaE.ToString("0.00", CultureInfo.CurrentCulture),
                            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                        choices.Add(Tuple.Create(selected, match)); list.Children.Add(row);
                    }
                    status.Text = result.PaletteName + " · найдено " + result.Matches.Count + ". Экранные цвета ориентировочные." +
                        (String.IsNullOrWhiteSpace(result.Warning) ? "" : "\n" + result.Warning);
                    create.IsEnabled = result.Matches.Count > 0;
                }
                catch (OperationCanceledException) { status.Text = "Поиск отменён."; }
                catch (Exception error)
                {
                    status.Text = error.GetBaseException().Message;
                    DockerTheme.UseTheme(status, TextBlock.ForegroundProperty, DockerTheme.ErrorTextBrush);
                    Log.Error("Pantone matching failed.", error);
                }
                finally { search.IsEnabled = browse.IsEnabled = automatic.IsEnabled = count.IsEnabled = true; }
            };
            create.Click += (_, __) => {
                try
                {
                    var selected = choices.Where(choice => choice.Item1.IsChecked == true).Select(choice => choice.Item2).ToArray();
                    if (selected.Length == 0) throw new InvalidOperationException("Выберите хотя бы один цвет Pantone.");
                    int created = new ColorProofPantoneService().Create(result, selected, 20);
                    status.Text = "Создан лист: эталон и " + created + " плашечных цветов.";
                    DockerTheme.UseTheme(status, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush);
                    _status(status.Text, false);
                }
                catch (Exception error)
                {
                    status.Text = error.GetBaseException().Message;
                    DockerTheme.UseTheme(status, TextBlock.ForegroundProperty, DockerTheme.ErrorTextBrush);
                    Log.Error("Pantone proof creation failed.", error);
                }
            };
            Window owner = Window.GetWindow(this);
            if (owner != null) window.Owner = owner;
            else if (PresentationSource.FromVisual(this) is HwndSource host) new WindowInteropHelper(window).Owner = host.Handle;
            _pantoneWindow = window;
            window.Closed += (_, __) => { cancellation.Cancel(); _pantoneWindow = null; };
            window.Show();
        }

        private static Border PantoneChip(int[] rgb) => new Border { Width = 44, Height = 34, Margin = new Thickness(0, 0, 9, 0),
            Background = new SolidColorBrush(Color.FromRgb((byte)rgb[0], (byte)rgb[1], (byte)rgb[2])),
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    }
}
