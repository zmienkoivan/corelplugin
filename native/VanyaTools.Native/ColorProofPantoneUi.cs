using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
            try
            {
                if (_pantoneWindow != null) { _pantoneWindow.Activate(); return; }
                Window window = BuildPantoneWindow();
                _pantoneWindow = window;
                window.Show();
            }
            catch (Exception error)
            {
                _pantoneWindow = null;
                Fail("Could not open Pantone matching window.", error);
            }
        }

        // Construct the actual content independently of Show and of Corel COM.
        // This also lets the opening path be verified without starting Corel.
        private Window BuildPantoneWindow() => BuildPantoneWindowCore(null);

        private Window BuildPantoneWindowCore(Func<bool, int[], int, string, CancellationToken,
            Action<int, int>, Task<ColorProofPantoneResult>> findOverride)
        {
            var window = new Window { Title = "Близкие Pantone · Vanya Tools", Width = 570, Height = 660,
                MinWidth = 420, MinHeight = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            DockerTheme.Apply(window);
            var root = new DockPanel { Margin = new Thickness(12) }; window.Content = root;
            var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            header.Children.Add(Title("Pantone Solid Coated"));
            var referenceRow = new DockPanel { Margin = new Thickness(0, 5, 0, 4) };
            var search = Button("Обновить", null);
            search.ToolTip = "Повторить поиск по текущему эталону вкладки «Цветопроба».";
            DockPanel.SetDock(search, Dock.Right); referenceRow.Children.Add(search);
            var referenceChip = PantoneChip(new[] { 255, 255, 255 });
            DockPanel.SetDock(referenceChip, Dock.Left); referenceRow.Children.Add(referenceChip);
            var referenceLabel = Note("Эталон из вкладки «Цветопроба»");
            referenceLabel.VerticalAlignment = VerticalAlignment.Center; referenceLabel.Margin = new Thickness(0, 0, 8, 0);
            referenceRow.Children.Add(referenceLabel); header.Children.Add(referenceRow);
            int[] ReadReference(bool cmyk)
            {
                string[] names = GridChannelNames(cmyk);
                return Enumerable.Range(0, names.Length).Select(channel => GridInteger(_gridBase[channel], 0,
                    cmyk ? 100 : 255, names[channel])).ToArray();
            }
            void ShowReference(bool cmyk, int[] values, int[] rgb = null)
            {
                string[] names = GridChannelNames(cmyk);
                referenceLabel.Text = (cmyk ? "CMYK" : "RGB") + " · " +
                    String.Join(" ", values.Select((value, channel) => names[channel] + ":" + value));
                referenceChip.Background = new SolidColorBrush(rgb == null ? GetDisplayColor(values, cmyk)
                    : Color.FromRgb((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]));
            }
            try { bool cmyk = _model.SelectedIndex == 0; ShowReference(cmyk, ReadReference(cmyk)); }
            catch { referenceLabel.Text = "Проверьте эталон на вкладке «Цветопроба»."; }
            var parametersContent = new StackPanel();
            var palettePath = Field(""); palettePath.IsReadOnly = true; palettePath.ToolTip = "Пусто — найти установленную палитру автоматически.";
            var paletteRow = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var browse = Button("Палитра…", (_, __) => {
                try
                {
                    var dialog = new OpenFileDialog { Title = "Палитра Pantone Solid Coated из Corel",
                        Filter = "Палитры Corel (*.xml;*.cpl)|*.xml;*.cpl|Все файлы (*.*)|*.*", CheckFileExists = true };
                    if (dialog.ShowDialog(window) == true) palettePath.Text = dialog.FileName;
                }
                catch (Exception error) { Fail("Could not choose Pantone palette file.", error); }
            });
            DockPanel.SetDock(browse, Dock.Right); paletteRow.Children.Add(browse);
            var automatic = Button("Авто", (_, __) => palettePath.Text = "");
            DockPanel.SetDock(automatic, Dock.Right); paletteRow.Children.Add(automatic);
            paletteRow.Children.Add(palettePath); parametersContent.Children.Add(paletteRow);
            var count = Combo("3 цвета", "5 цветов", "8 цветов", "12 цветов"); count.SelectedIndex = 1;
            parametersContent.Children.Add(Row("Показать", count));
            parametersContent.Children.Add(Note("Изменения параметров применяются по кнопке «Обновить»."));
            var parameters = new Expander { Header = "Параметры", Content = parametersContent,
                IsExpanded = false, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 2, 0, 5) };
            header.Children.Add(parameters);
            var status = Note("Ищу ближайшие плашечные цвета…");
            header.Children.Add(status);
            var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            footer.Children.Add(Note("Выберите цвета для пробного листа. Заливки сохраняются плашечными; меньшее ΔE00 — ближе к эталону."));
            var create = DockerTheme.Primary(Button("Создать плашки", null)); create.IsEnabled = false;
            footer.Children.Add(create);
            var list = new StackPanel(); root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            ColorProofPantoneResult result = null;
            var choices = new List<Tuple<CheckBox, ColorProofPantoneMatch>>();
            var cancellation = new CancellationTokenSource();
            bool searching = false, closed = false, loadedSearchStarted = false;
            async Task SearchAsync()
            {
                if (searching || closed) return;
                searching = true;
                search.IsEnabled = browse.IsEnabled = automatic.IsEnabled = count.IsEnabled = create.IsEnabled = false;
                list.Children.Clear(); choices.Clear(); result = null;
                status.Text = "Ищу плашечные цвета в палитре Corel…";
                DockerTheme.UseTheme(status, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush);
                try
                {
                    bool cmyk = _model.SelectedIndex == 0;
                    int[] values = ReadReference(cmyk);
                    int amount = new[] { 3, 5, 8, 12 }[Math.Max(0, count.SelectedIndex)];
                    string selectedPalette = palettePath.Text.Trim();
                    ShowReference(cmyk, values);
                    // Capture every input before yielding. Editing the source
                    // tab cannot silently change this window's active search.
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (closed || cancellation.IsCancellationRequested || (findOverride == null && !window.IsVisible)) return;
                    Action<int, int> progress = (done, total) => {
                        if (!closed) status.Text = "Проверяю цвета Pantone: " + done + " / " + total + "…";
                    };
                    // Corel objects stay on this UI thread. Only cloned colors are converted for comparison.
                    result = findOverride == null
                        ? await new ColorProofPantoneService().FindAsync(cmyk, values, amount, selectedPalette, cancellation.Token, progress)
                        : await findOverride(cmyk, values, amount, selectedPalette, cancellation.Token, progress);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (closed) return;
                    ShowReference(cmyk, values, result.ReferenceScreenRgb);
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
                catch (OperationCanceledException) { if (!closed) status.Text = "Поиск отменён."; }
                catch (Exception error)
                {
                    if (closed) return;
                    status.Text = error.GetBaseException().Message;
                    parameters.IsExpanded = true;
                    DockerTheme.UseTheme(status, TextBlock.ForegroundProperty, DockerTheme.ErrorTextBrush);
                    Log.Error("Pantone matching failed.", error);
                }
                finally
                {
                    searching = false;
                    if (closed) cancellation.Dispose();
                    else search.IsEnabled = browse.IsEnabled = automatic.IsEnabled = count.IsEnabled = true;
                }
            }
            search.Click += async (_, __) => await SearchAsync();
            window.Loaded += async (_, __) => {
                if (loadedSearchStarted || closed) return;
                loadedSearchStarted = true;
                await SearchAsync();
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
            window.Closed += (_, __) => {
                closed = true; cancellation.Cancel();
                if (!searching) cancellation.Dispose();
                if (ReferenceEquals(_pantoneWindow, window)) _pantoneWindow = null;
            };
            return window;
        }

        private static Border PantoneChip(int[] rgb) => new Border { Width = 44, Height = 34, Margin = new Thickness(0, 0, 9, 0),
            Background = new SolidColorBrush(Color.FromRgb((byte)rgb[0], (byte)rgb[1], (byte)rgb[2])),
            BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    }
}
