using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed class FilmOutputTab : UserControl
    {
        private readonly Func<string> _yearFolder;
        private readonly FilmRipSettings _ripSettings;
        private readonly Action<string, bool> _status;
        private readonly StackPanel _panel;
        private readonly ComboBox _mode;
        private readonly TextBox _filmWidth, _gap, _bandWidth, _markOffset, _markStroke, _report;
        private readonly CheckBox _fit, _exportBands;
        private readonly TextBlock _folder, _ripFolder, _progress;
        private readonly FilmMarksTable _marksTable;
        private readonly FilmColorReview _colorReview;
        private bool _busy;
        private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "VanyaTools", "film-output.json");

        internal FilmOutputTab(Func<string> yearFolder, FilmRipSettings ripSettings, Action<string, bool> status)
        {
            _yearFolder = yearFolder; _ripSettings = ripSettings; _status = status;
            FilmOutputOptions saved = Load();
            _panel = new StackPanel { Margin = new Thickness(6) };
            Content = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            _panel.Children.Add(new TextBlock { Text = "Фотовывод · позитив", FontSize = 14, FontWeight = FontWeights.SemiBold });
            DockerHelp.Add(_panel, "Два режима: разметить цветной эталон целиком перед разделением или каждый уже готовый растр отдельно. " +
                "Метка: концентрические круги Ø8/Ø5 мм, горизонталь, вертикаль и диагональ вправо 45° по 10 мм через центр. " +
                "Центры стоят на общей вертикали в центре ширины рисунка, по 25 мм выше и ниже его краёв. " +
                "У нижней метки справа снизу — цвет и номер слоя, 6 пт. Все новые линии и подписи — C0 M0 Y0 K100.");
            _mode = new ComboBox { ItemsSource = new[] { "Всё выделение · эталон / один вывод", "Каждый выбранный растр отдельно" }, SelectedIndex = 0,
                MinHeight = 28, Margin = new Thickness(0, 5, 0, 4) };
            _panel.Children.Add(_mode);
            AddButton("Поставить / обновить метки и подпись…", () => {
                List<FilmTarget> targets = Capture();
                FilmOutputOptions options = ReadOptions();
                if (Captions(targets))
                {
                    FilmOutputService.Mark(targets, options);
                    _colorReview.Clear();
                    Report("Метки и подписи поставлены: " + targets.Count + ".", false);
                }
                return Task.CompletedTask;
            });
            DockerHelp.Add(_panel, "В окне подписей можно исправить цвет и номер каждого слоя. " +
                "Порядок готовых растров — сверху вниз, в одном ряду слева направо. " +
                "Рисунок, метки и подпись объединяются в комплект. Для повторной правки выделите комплект целиком. " +
                "У готового комплекта обновляется подпись, а позиции существующих меток сохраняются — это важно после разделения эталона по цветам. " +
                "Разметку можно ставить на цветной эталон: проверка цветов при этом не запускается. " +
                "Растры одинакового размера получают одинаковые позиции меток относительно изображения.");
            _marksTable = new FilmMarksTable(status);
            _panel.Children.Add(_marksTable);
            AddButton("Добавить / обновить боковые полосы", () => {
                List<FilmTarget> targets = Capture();
                FilmOutputOptions options = ReadOptions();
                var gaps = targets.Select(t => FilmOutputService.BandGap(FilmOutputService.PrintBounds(t), options)).ToList();
                FilmOutputService.AddBands(targets, options);
                _colorReview.Clear();
                Report("Полосы созданы: " + targets.Count + ". Зазор: " + String.Join(" / ", gaps.Select(g => g.ToString("0.##"))) + " мм.", false);
                return Task.CompletedTask;
            });
            DockerHelp.Add(_panel, "Сплошные полосы слева и справа идут на полную высоту вывода, включая метки и зону подписи. " +
                "На обоих концах — поперечные риски длиной 2 мм внутрь. " +
                "По умолчанию отступ от содержимого 20 мм и ширина полосы 0,3 мм. " +
                "На плёнке 605 мм можно уменьшать только зазор (минимум 2 мм), сохраняя рисунок 1:1. " +
                "Если места всё равно нет, действие останавливается с указанием ширины. Непечатаемые поля принтера учитывайте в доступной ширине.");
            AddButton("Проверить цвета выделения", async () => {
                List<FilmTarget> targets = Capture();
                FilmColorReport report = await Check(targets);
                Report(report.Errors > 0 ? "Найдены недопустимые цвета: " + report.Errors + "." :
                    report.Warnings > 0 ? "Цветных ошибок нет; есть предупреждения о полутонах." : "Цвета допустимы для фотовывода.", report.Errors > 0);
            });
            _colorReview = new FilmColorReview(Run, Report, Recheck);
            _panel.Children.Add(_colorReview);
            DockerHelp.Add(_panel, "Допустимые векторные цвета: C0 M0 Y0 K100 и белый RGB 255/255/255 либо CMYK 0/0/0/0. " +
                "RGB-чёрный, составной чёрный и цветные/плашечные цвета отмечаются как ошибки. " +
                "Нейтральные полутона, монохромные и нейтральные RGB-растры — предупреждения. " +
                "Проверяются исходные пиксели без перевода цветовой модели; полностью прозрачные пиксели пропускаются. " +
                "Неподдерживаемые заливки, эффекты и непрочитанные данные не считаются успешно проверенными. " +
                "Список ошибок позволяет выделить объекты в Corel и по кнопке заменить недопустимые векторные цвета на K100.");

            var settings = new StackPanel { Margin = new Thickness(3) };
            _filmWidth = Field(settings, "Доступная ширина плёнки, мм", saved.FilmWidth);
            _gap = Field(settings, "Отступ до боковых полос, мм", saved.BandGap);
            _bandWidth = Field(settings, "Ширина полос и рисок, мм", saved.BandWidth);
            _fit = new CheckBox { Content = "Сближать полосы до 2 мм, если не помещаются", IsChecked = saved.FitBands,
                Margin = new Thickness(0, 2, 0, 4) }; settings.Children.Add(_fit);
            _markOffset = Field(settings, "От края рисунка до центра метки, мм", saved.MarkOffset);
            _markStroke = Field(settings, "Толщина контура метки, мм", saved.MarkStroke);
            _panel.Children.Add(new Expander { Header = "Размеры меток и полос", Content = settings, IsExpanded = false,
                Margin = new Thickness(0, 3, 0, 5) });
            _panel.Children.Add(new Separator());
            _panel.Children.Add(new TextBlock { Text = "PDF фотовывода", FontSize = 13, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 4) });
            _exportBands = new CheckBox { Content = "Боковые полосы в PDF", IsChecked = saved.ExportBands,
                Margin = new Thickness(0, 0, 0, 4) }; _panel.Children.Add(_exportBands);
            DockerHelp.Add(_panel, "Настройки PDF встроены в плагин, заготовка filmgate на компьютере не требуется. " +
                "PDF 1.7, совместимый с Acrobat DC: выделение, размер по объектам, исходные цвета, без ICC-профилей, сжатия и субдискретизации, " +
                "текст кривыми, без типографских меток, полутоновых настроек и паролей. " +
                "Вывод готовится во временном документе с цветовым контекстом исходника, затем закрывается. Буфер обмена используется для переноса копии.");
            _folder = new TextBlock { FontSize = 10, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 3, 0, 4) }; _panel.Children.Add(_folder);
            AddButton("Открыть папку фотовыводов", () => {
                string path = Folder(); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return Task.CompletedTask;
            });
            AddButton("Экспортировать выделение в PDF 1:1", () => Output(false), true);
            DockerHelp.Add(_panel, "Папка: выбранная папка года → месяц → фотовывод. Для CDR заказа используется месяц из его пути; " +
                "для остальных документов — текущий месяц. Папка года общая с сохранением CDR в «Настройках». " +
                "Каждый готовый растр в пакетном режиме сохраняется отдельным PDF с цветом и номером слоя в имени. " +
                "Файлы не перезаписываются. Полутона предупреждают, но не блокируют экспорт; недопустимые цвета блокируют. " +
                "Печатайте 1:1, без вписывания в лист. Полосы обеспечивают непрерывную печатную область, но фактические проходы задаются RIP и принтером.");
            _ripFolder = new TextBlock { FontSize = 10, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 5, 0, 3) }; _panel.Children.Add(_ripFolder);
            AddButton("Отправить на печать в RIP", () => Output(true), true);
            DockerHelp.Add(_panel, "Выделение, масштаб 1:1, полосы и проверка цветов такие же, как при экспорте. " +
                "PDF передаётся прямо в hot folder из «Настройки → Фотовывод · печать в RIP». Папка архива для отправки не требуется. " +
                "В режиме отдельных растров передаётся серия PDF. Успешный статус означает передачу в папку, а не завершение печати RIP.");
            _progress = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 3), FontSize = 11 };
            _panel.Children.Add(_progress);
            _report = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 230, MinHeight = 48,
                FontSize = 10, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderBrush = Brushes.LightGray };
            _panel.Children.Add(_report);
            Loaded += (_, __) => UpdateFolder();
            _ripSettings.Changed += UpdateFolder;
        }

        private async Task Output(bool toRip)
        {
            List<FilmTarget> targets = Capture();
            FilmOutputOptions options = ReadOptions();
            string folder = toRip ? FilmRipSettings.RequireFolder(_ripSettings.HotFolder) : Folder();
            FilmColorReport colors = await Check(targets);
            if (colors.Errors > 0)
            { Report((toRip ? "В RIP не передано" : "PDF не создан") + ": откройте список ошибок цветов выше; объекты можно выделить и исправить.", true); return; }
            _progress.Text = toRip ? "Готовлю PDF для папки RIP…" : "Создаю PDF фотовывода…";
            FilmExportResult result = await FilmPdfExporter.Export(targets, options, folder, toRip);
            _report.Text = colors + "\n" + String.Join("\n", result.Notes) + (toRip ? "\nПередано в RIP:\n" : "\nPDF:\n") + String.Join("\n", result.Paths);
            Report((toRip ? "Передано в папку RIP, PDF: " : "Сохранено PDF: ") + result.Paths.Count +
                (colors.Warnings > 0 ? ". Есть предупреждения о полутонах." : "."), false);
            UpdateFolder();
        }

        private async Task<FilmColorReport> Check(List<FilmTarget> targets)
        {
            return await Recheck(new FilmColorScope(targets));
        }
        private async Task<FilmColorReport> Recheck(FilmColorScope scope)
        {
            FilmColorReport result = await FilmColorValidator.Check(scope.Roots(),
                text => _progress.Text = text);
            _report.Text = result.ToString(); _colorReview.Show(scope, result); return result;
        }
        private List<FilmTarget> Capture()
        {
            _colorReview?.RestoreIfHighlighted();
            return FilmOutputService.Capture(_mode.SelectedIndex == 1);
        }
        private void AddButton(string text, Func<Task> action, bool primary = false)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 1, 0, 3) };
            if (primary) DockerTheme.Primary(button);
            button.Click += async (_, __) => await Run(action);
            _panel.Children.Add(button);
        }
        private async Task Run(Func<Task> action)
        {
            if (_busy) return;
            try
            {
                if (!_marksTable.CommitPending()) return;
                _busy = true; _panel.IsEnabled = false;
                await action();
            }
            catch (Exception error) { Log.Error("Film output action failed.", error); Report(error.Message, true); }
            finally { _panel.IsEnabled = true; _busy = false; _marksTable.Refresh(); }
        }
        private void Report(string message, bool error)
        { _progress.Text = message; _progress.Foreground = error ? Brushes.Firebrick : Brushes.DarkSlateGray; _status(message, error); }
        private string Folder()
        {
            string path = "";
            try { path = Convert.ToString(CorelApp.Get().ActiveDocument.FullFileName); } catch { }
            return FilmPdfExporter.Folder(_yearFolder(), path);
        }
        private void UpdateFolder()
        {
            try { _folder.Text = Folder(); } catch (Exception error) { _folder.Text = error.Message; }
            _ripFolder.Text = String.IsNullOrWhiteSpace(_ripSettings.HotFolder)
                ? "RIP: укажите hot folder в настройках." : "RIP: " + _ripSettings.HotFolder;
        }
        private FilmOutputOptions ReadOptions()
        {
            var options = new FilmOutputOptions { FilmWidth = Number(_filmWidth), BandGap = Number(_gap), BandWidth = Number(_bandWidth),
                MarkOffset = Number(_markOffset), MarkStroke = Number(_markStroke), FitBands = _fit.IsChecked == true,
                ExportBands = _exportBands.IsChecked == true };
            options.Validate(); Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            File.WriteAllText(SettingsPath, new JavaScriptSerializer().Serialize(options)); return options;
        }
        private static double Number(TextBox field)
        { if (!DtfOutlineUnits.TryNumber(field.Text, out double value)) throw new InvalidOperationException("Укажите размер числом в миллиметрах."); return value; }
        private static TextBox Field(Panel panel, string label, double value)
        {
            var field = new TextBox { Text = DtfOutlineUnits.Format(value), MinHeight = 25, VerticalContentAlignment = VerticalAlignment.Center };
            panel.Children.Add(DockerLayout.FieldRow(label, field, 75)); return field;
        }
        private static FilmOutputOptions Load()
        {
            try { if (File.Exists(SettingsPath)) { var value = new JavaScriptSerializer().Deserialize<FilmOutputOptions>(File.ReadAllText(SettingsPath)); value.Validate(); return value; } }
            catch (Exception error) { Log.Error("Could not restore film output preferences.", error); }
            return new FilmOutputOptions();
        }

        private bool Captions(List<FilmTarget> targets)
        {
            var dialog = new Window { Title = "Фотовывод · подписи 6 пт", Width = 510, Height = Math.Min(570, 255 + targets.Count * 30),
                MinWidth = 390, MinHeight = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            Window owner = Window.GetWindow(this); if (owner != null) dialog.Owner = owner;
            else try { new WindowInteropHelper(dialog).Owner = new IntPtr(Convert.ToInt64(CorelApp.Get().AppWindow.Handle)); } catch { }
            var root = new DockPanel { Margin = new Thickness(10) }; dialog.Content = root; DockerTheme.Apply(root);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
            var ok = new Button { Content = "Поставить метки", IsDefault = true, Margin = new Thickness(3), MinWidth = 130 };
            var cancel = new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(3), MinWidth = 75 }; buttons.Children.Add(ok); buttons.Children.Add(cancel);
            var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            header.Children.Add(new TextBlock { Text = "Цвет и номер каждого слоя. Подпись справа снизу от нижней метки, 6 пт.", TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8) });
            var allColor = new TextBox { Text = targets[0].Caption.Color, MinWidth = 100, MinHeight = 27 };
            var allButton = new Button { Content = "Этот цвет всем", Margin = new Thickness(5, 0, 0, 4) };
            header.Children.Add(DockerLayout.Trailing(allColor, allButton));
            var rows = targets.Select(t => t.Caption).ToList();
            var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
                RowHeight = 29, Margin = new Thickness(0, 3, 0, 7), HeadersVisibility = DataGridHeadersVisibility.Column };
            grid.Columns.Add(new DataGridTextColumn { Header = "Объект", Binding = new Binding("ObjectName"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Цвет", Binding = new Binding("Color") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1.3, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "№ слоя", Binding = new Binding("Number"), Width = 65 });
            root.Children.Add(grid);
            allButton.Click += (_, __) => { if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true)) return;
                foreach (FilmCaption row in rows) row.Color = allColor.Text; grid.Items.Refresh(); };
            ok.Click += (_, __) => {
                if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true)) return;
                if (rows.Any(r => String.IsNullOrWhiteSpace(r.Color) || r.Color.Length > 100 || r.Color.Contains("\n") || r.Color.Contains("\r") || r.Number < 1 || r.Number > 9999))
                { MessageBox.Show(dialog, "Укажите цвет (до 100 символов) и номер слоя от 1 до 9999.", "Подпись фотовывода"); return; }
                dialog.DialogResult = true;
            };
            return dialog.ShowDialog() == true;
        }
    }
}
