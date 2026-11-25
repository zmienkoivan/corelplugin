using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace VanyaTools.Native
{
    internal sealed class EmbroideryTab : UserControl
    {
        private readonly Func<string> _token, _capture;
        private readonly Action<string, bool> _status;
        private readonly Action<EmbroideryFrame> _insert;
        private readonly Func<EmbroideryRequest, BitmapSource, bool> _confirm;
        private readonly EmbroideryStore _store;
        private readonly Func<EmbroideryJob, string, bool, Action<string>, CancellationToken, EmbroideryResult> _run;
        private readonly Dictionary<string, TextBox> _fields = new Dictionary<string, TextBox>();
        private readonly StackPanel _settings, _cards;
        private readonly TextBox _description, _exact, _color;
        private readonly ComboBox _model, _fabric, _thread;
        private readonly Image _preview;
        private readonly TextBlock _source, _progress, _notes, _limits;
        private readonly Button _generate, _cancel, _fresh, _last;
        private readonly ProgressBar _bar;
        private BitmapSource _reference;
        private EmbroideryJob _job;
        private CancellationTokenSource _cancelSource;
        private bool _busy, _restored;
        internal EmbroideryTab(Func<string> token, Func<string> capture, Action<string, bool> status,
            Action<EmbroideryFrame> insert = null, EmbroideryStore store = null,
            Func<EmbroideryRequest, BitmapSource, bool> confirm = null,
            Func<EmbroideryJob, string, bool, Action<string>, CancellationToken, EmbroideryResult> run = null)
        {
            _token = token; _capture = capture; _status = status; _insert = insert ?? EmbroideryCorel.Insert;
            _store = store ?? new EmbroideryStore(); _confirm = confirm ?? Confirm;
            _run = run ?? ((job, key, resume, progress, cancel) => EmbroideryService.Run(job, key, resume, progress, cancel));
            var root = new StackPanel { Margin = new Thickness(7) };
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            root.Children.Add(Note("Два эскиза и два мокапа · один запрос 4K. Для нового логотипа достаточно описания; для адаптации добавьте референс."));
            _settings = new StackPanel(); root.Children.Add(_settings);
            var sources = new UniformGrid { Columns = 2 };
            sources.Children.Add(Button("Из выделения Corel", (_, __) => Capture(), "EmbroideryCapture"));
            sources.Children.Add(Button("Референс из файла…", (_, __) => FileReference())); _settings.Children.Add(sources);
            _preview = new Image { Height = 105, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
            _settings.Children.Add(_preview); _source = Note("Без референса — создание по описанию."); _settings.Children.Add(_source);
            _settings.Children.Add(Button("Убрать референс", (_, __) => SetReference(null)));
            _settings.Children.Add(Note("Описание и пожелания к адаптации:"));
            _description = new TextBox { Name = "EmbroideryDescription", MinHeight = 56, MaxHeight = 110, MaxLength = 3000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _settings.Children.Add(_description);
            _settings.Children.Add(Note("Точная надпись (если есть):")); _exact = new TextBox { MaxLength = 500 }; _settings.Children.Add(_exact);
            _model = new ComboBox { Margin = new Thickness(0, 5, 0, 4) }; _model.Items.Add("Nano Banana Pro · 4K"); _model.Items.Add("Nano Banana 2 · 4K"); _model.SelectedIndex = 0; _settings.Children.Add(_model);
            _settings.Children.Add(Note("Максимальное поле логотипа, мм · пропорции сохраняются:"));
            Fields(_settings, new[] { "WidthMm", "HeightMm" }, new[] { "Ширина", "Высота" });
            _settings.Children.Add(Note("Ткань:")); _fabric = new ComboBox { ItemsSource = EmbroideryFabric.All }; _fabric.SelectedIndex = 0; _settings.Children.Add(_fabric);
            _settings.Children.Add(Note("Цвет ткани · #RRGGBB:")); _color = new TextBox { Text = "#E6E0D5", MaxLength = 7 }; _settings.Children.Add(_color);
            Fields(_settings, new[] { "Colors" }, new[] { "Не более цветов ниток" });
            var advanced = new StackPanel();
            advanced.Children.Add(Note("Консервативные начальные значения. Уточняются под ткань, нитки, иглу и вашу технологию; размер проверяется в рабочем поле пялец."));
            advanced.Children.Add(Note("Нить:")); _thread = new ComboBox(); _thread.Items.Add("№40 · стандартная"); _thread.Items.Add("№60 · тонкая"); _thread.SelectedIndex = 0; advanced.Children.Add(_thread);
            Fields(advanced, new[] { "MinStrokeMm", "MinGapMm", "MinTextMm" }, new[] { "Мин. штрих, мм", "Мин. просвет, мм", "Мин. высота букв, мм" });
            Fields(advanced, new[] { "HoopWidthMm", "HoopHeightMm", "HoopMarginMm" }, new[] { "Поле пялец: ширина, мм", "Поле пялец: высота, мм", "Запас с каждой стороны, мм" });
            _limits = Note(""); _settings.Children.Add(_limits);
            foreach (string field in new[] { "MinStrokeMm", "MinGapMm", "MinTextMm" }) _fields[field].TextChanged += (_, __) => Limits();
            _settings.Children.Add(new Expander { Header = "Технологические ограничения", Content = advanced, IsExpanded = false, Margin = new Thickness(0, 6, 0, 4) });
            _settings.Children.Add(Note("Сатин для узких элементов, татами для широких; без градиентов и микродеталей. Ворсовая ткань требует укрупнения деталей, подложки и подходящего стабилизатора."));
            _fabric.SelectionChanged += (_, __) => FabricDefaults();
            Apply(new EmbroideryRequest());
            _generate = DockerTheme.Primary(Button("Создать 4 кадра · 4K", async (_, __) => await Generate(), "EmbroideryGenerate")); root.Children.Add(_generate);
            var actions = new UniformGrid { Columns = 2 };
            _fresh = Button("Новое задание", (_, __) => Fresh(), "EmbroideryNew"); _cancel = Button("Отменить", (_, __) => _cancelSource?.Cancel());
            actions.Children.Add(_fresh); actions.Children.Add(_cancel); root.Children.Add(actions);
            _last = Button("Открыть последнее задание", async (_, __) => await Restore()); root.Children.Add(_last);
            root.Children.Add(Button("Папка результатов", (_, __) => OpenFolder()));
            _progress = Note(""); root.Children.Add(_progress);
            _bar = new ProgressBar { Height = 6, IsIndeterminate = true, Visibility = Visibility.Collapsed }; root.Children.Add(_bar);
            _notes = Note("Мокап показывает внешний вид. Для производства нужны оцифровка стежков и пробная вышивка; PNG не заменяет DST/EMB."); root.Children.Add(_notes);
            _cards = new StackPanel(); root.Children.Add(_cards);
            Loaded += async (_, __) => { if (!_restored) { _restored = true; await Restore(); } };
            State();
        }
        private static TextBlock Note(string text) => new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(1, 3, 1, 4) };
        private static Button Button(string text, RoutedEventHandler handler, string name = null)
        { var button = new Button { Content = text, Margin = new Thickness(1, 3, 1, 3), MinHeight = 29 }; if (name != null) button.Name = name; button.Click += handler; return button; }
        private void Fields(Panel panel, string[] keys, string[] labels)
        {
            var grid = new UniformGrid { Columns = 2 };
            for (int i = 0; i < keys.Length; i++)
            {
                var field = new StackPanel { Margin = new Thickness(1, 2, 4, 3) }; field.Children.Add(Note(labels[i]));
                var box = new TextBox { Name = "Embroidery" + keys[i], MinWidth = 45 }; _fields.Add(keys[i], box); field.Children.Add(box); grid.Children.Add(field);
            }
            panel.Children.Add(grid);
        }
        private double Number(string key)
        {
            if (!Double.TryParse(_fields[key].Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) throw new ArgumentException("Введите число: " + key);
            return value;
        }
        private EmbroideryRequest Read()
        {
            double colors = Number("Colors");
            if (colors != Math.Truncate(colors)) throw new ArgumentException("Число цветов должно быть целым.");
            return new EmbroideryRequest { Model = _model.SelectedIndex == 1 ? "google/nano-banana-2" : "google/nano-banana-pro", Description = _description.Text.Trim(), ExactText = _exact.Text.Trim(),
                WidthMm = Number("WidthMm"), HeightMm = Number("HeightMm"), Fabric = _fabric.SelectedIndex, FabricColor = _color.Text.Trim(), Colors = checked((int)colors), Thread = _thread.SelectedIndex == 1 ? 60 : 40,
                MinStrokeMm = Number("MinStrokeMm"), MinGapMm = Number("MinGapMm"), MinTextMm = Number("MinTextMm"), HoopWidthMm = Number("HoopWidthMm"), HoopHeightMm = Number("HoopHeightMm"), HoopMarginMm = Number("HoopMarginMm") };
        }
        private void Apply(EmbroideryRequest request)
        {
            _description.Text = request.Description; _exact.Text = request.ExactText; _color.Text = request.FabricColor;
            _model.SelectedIndex = request.Model == "google/nano-banana-2" ? 1 : 0; _fabric.SelectedIndex = request.Fabric; _thread.SelectedIndex = request.Thread == 60 ? 1 : 0;
            foreach (var field in _fields) field.Value.Text = Convert.ToDouble(typeof(EmbroideryRequest).GetProperty(field.Key).GetValue(request)).ToString("0.##", CultureInfo.CurrentCulture);
        }
        private void FabricDefaults()
        {
            if (_fabric.SelectedItem is EmbroideryFabric fabric)
            { _fields["MinStrokeMm"].Text = EmbroideryRequest.F(fabric.Stroke); _fields["MinGapMm"].Text = EmbroideryRequest.F(fabric.Gap); _fields["MinTextMm"].Text = EmbroideryRequest.F(fabric.Text); }
        }
        private void Limits()
        { _limits.Text = "Минимум: штрих " + _fields["MinStrokeMm"].Text + " мм · просвет " + _fields["MinGapMm"].Text + " мм · буквы " + _fields["MinTextMm"].Text + " мм."; }
        private void Capture()
        { try { SetReference(EmbroideryImages.Load(_capture())); } catch (Exception error) { Report(error.GetBaseException().Message, true); } }
        private void FileReference()
        {
            var dialog = new OpenFileDialog { Title = "Референс вышивки", Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff" };
            if (dialog.ShowDialog() == true) try { SetReference(EmbroideryImages.Load(dialog.FileName)); } catch (Exception error) { Report(error.GetBaseException().Message, true); }
        }
        private void SetReference(BitmapSource image)
        { _reference = image; _preview.Source = image; _preview.Visibility = image == null ? Visibility.Collapsed : Visibility.Visible; _source.Text = image == null ? "Без референса — создание по описанию." : "Референс: " + image.PixelWidth + " × " + image.PixelHeight + " px. Исходник Corel сохранится."; }
        private bool Confirm(EmbroideryRequest request, BitmapSource reference)
        {
            string text = request.Summary + "\nМинимум: штрих " + EmbroideryRequest.F(request.MinStrokeMm) + " мм; просвет " + EmbroideryRequest.F(request.MinGapMm) + " мм; буквы " + EmbroideryRequest.F(request.MinTextMm) + " мм.\n\n" +
                "Один платный запрос Replicate: " + (_model.SelectedItem as string) + ". Коллаж 2 × 2: цветной эскиз + мокап, одноцветный эскиз + мокап.\nПоказанный референс" + (reference == null ? " отсутствует: используется описание." : " будет отправлен для адаптации.") + "\n\nОтправить?";
            return MessageBox.Show(text, "Вышивка · один запрос 4K", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes;
        }
        private async Task Generate()
        {
            if (_busy) return;
            try
            {
                string key = File.Exists(_job?.RawPath ?? "") ? "" : _token();
                if (_job == null)
                {
                    var request = Read(); request.Validate(_reference != null);
                    if (String.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Введите ключ Replicate в настройках ИИ.");
                    if (!_confirm(request, _reference)) return;
                    _job = _store.Create(request, _reference);
                    _notes.Text = _job.Request.Summary + "\nПараметры и референс этого запроса сохранены. После сбоя продолжится этот же запуск.";
                }
                await Execute();
                async Task Execute()
                {
                    _busy = true; _cancelSource = new CancellationTokenSource(); State();
                    bool resume = File.Exists(_job.StartedPath); var job = _job; var cancel = _cancelSource.Token;
                    var result = await Task.Run(() => _run(job, key, resume, Progress, cancel), cancel);
                    Show(result); Report("Четыре кадра сохранены. Проверьте эскизы и мокапы перед вставкой.", false);
                }
            }
            catch (OperationCanceledException) { Report("Запрос отменён. Если ответ успел сохраниться, он остаётся в истории.", false); }
            catch (Exception error) { Log.Error("Embroidery job failed.", error); Report(error.GetBaseException().Message + (_job != null ? " Сохранённое задание доступно кнопкой продолжения." : ""), true); }
            finally { _busy = false; _cancelSource?.Dispose(); _cancelSource = null; State(); }
        }
        private async Task Restore()
        {
            if (_busy) return;
            try
            {
                var job = _store.Last(); if (job == null) return;
                _job = job; Apply(job.Request); SetReference(job.HasReference ? EmbroideryImages.Load(job.ReferencePath) : null);
                _notes.Text = job.Request.Summary + "\nПараметры и референс сохранены в задании.";
                if (File.Exists(job.RawPath))
                {
                    _busy = true; State(); var result = await Task.Run(() => EmbroideryImages.Prepare(job, CancellationToken.None)); Show(result);
                    Report("Открыт сохранённый результат без нового запроса.", false);
                }
                else Report("Есть сохранённое задание: продолжите его без создания нового запуска.", false);
            }
            catch (Exception error) { Report(error.GetBaseException().Message, true); }
            finally { _busy = false; State(); }
        }
        private void Fresh()
        { if (_busy) return; _job = null; _cards.Children.Clear(); _notes.Text = "Предыдущие задания остаются в папке результатов. Новая генерация отправляется только по кнопке и после подтверждения."; Report("Новое задание: проверьте описание, размер и ткань.", false); State(); }
        private void Show(EmbroideryResult result)
        {
            _cards.Children.Clear(); _notes.Text = result.Job.Request.Summary + "\n" + String.Join("\n", result.Notes);
            foreach (var frame in result.Frames)
            {
                _cards.Children.Add(Note(frame.Title + " · " + EmbroideryRequest.F(frame.WidthMm) + " × " + EmbroideryRequest.F(frame.HeightMm) + " мм"));
                _cards.Children.Add(new Image { Source = frame.Preview, Height = 190, Stretch = Stretch.Uniform });
                var buttons = new UniformGrid { Columns = 2 };
                var insert = Button("Вставить в Corel", (_, __) => { try { _insert(frame); Report("Вставлено: " + frame.Title + ". Исходные объекты сохранены.", false); } catch (Exception error) { Report(error.GetBaseException().Message, true); } }); insert.IsEnabled = frame.CanInsert;
                buttons.Children.Add(insert); buttons.Children.Add(Button("Сохранить PNG…", (_, __) => Save(frame))); _cards.Children.Add(buttons);
            }
        }
        private void Save(EmbroideryFrame frame)
        {
            var dialog = new SaveFileDialog { Filter = "PNG|*.png", FileName = frame.Title + ".png" };
            if (dialog.ShowDialog() == true) try { if (!String.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(frame.Path), StringComparison.OrdinalIgnoreCase)) File.Copy(frame.Path, dialog.FileName, true); Report("PNG сохранён.", false); } catch (Exception error) { Report(error.Message, true); }
        }
        private void OpenFolder()
        { try { Directory.CreateDirectory(_store.Root); Process.Start(new ProcessStartInfo { FileName = _job?.DirectoryPath ?? _store.Root, UseShellExecute = true }); } catch (Exception error) { Report(error.Message, true); } }
        private void Progress(string text)
        { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => _progress.Text = text)); }
        private void Report(string text, bool error) { _progress.Text = text; _status?.Invoke(text, error); }
        private void State()
        {
            _settings.IsEnabled = !_busy && _job == null; _generate.IsEnabled = !_busy; _fresh.IsEnabled = !_busy; _last.IsEnabled = !_busy;
            _settings.Visibility = _job == null ? Visibility.Visible : Visibility.Collapsed;
            _cancel.IsEnabled = _busy && _cancelSource != null; _cards.IsEnabled = !_busy;
            _bar.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
            _generate.Content = _job == null ? "Создать 4 кадра · 4K" : File.Exists(_job.RawPath) ? "Открыть сохранённый результат" : File.Exists(_job.StartedPath) ? "Продолжить запрос" : "Отправить сохранённое задание";
        }
    }
}
