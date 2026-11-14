using System;
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
    internal sealed class PuffySleeveTab : UserControl
    {
        private readonly Action<string, bool> _status;
        private readonly ComboBox _mode, _aspect, _quilting;
        private readonly TextBox _width, _height, _thickness, _diagonal, _bezel;
        private readonly TextBox _ease, _loft, _foam, _foamInset, _lining, _depthEase;
        private readonly TextBox _allowance, _radius, _zipDrop, _zipWidth, _zipTail;
        private readonly TextBox _step, _quiltInset, _shrinkX, _shrinkY, _bleed;
        private readonly TextBox _trimMargin, _fixingOffset;
        private readonly TextBox _commonWidth, _commonHeight;
        private readonly TextBox _guideDiameter, _guideHeight, _plasticThickness, _jigClearance, _jigFrame, _jigBridge, _jigHole, _jigKerf;
        private readonly StackPanel _bodySize, _screenSize;
        private readonly Image _preview;
        private readonly TextBlock _summary, _artInfo, _frontLabel, _backLabel;
        private readonly TextBlock _jigSummary;
        private readonly CheckBox _mirror, _same;
        private readonly CheckBox _guideCentered, _includeJigs;
        private readonly StackPanel _backPanel;
        private readonly Button _create, _quilt, _shape, _save, _open;
        private readonly Button _jigQuilt, _jigShape, _saveJigs;
        private string _front, _back, _folder;
        private BitmapSource _frontBitmap, _backBitmap;
        private bool _ready, _busy;

        public PuffySleeveTab(Action<string, bool> status)
        {
            _status = status;
            var panel = new StackPanel { Margin = new Thickness(6) };
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(new TextBlock { Text = "Дутые аксессуары · чехол", FontSize = 14,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
            panel.Children.Add(Note("Прямоугольник → стёжка → шаблон формы → обрезка с припуском → сборка с кольцом."));
            _preview = new Image { Height = 145, Stretch = Stretch.Uniform, Margin = new Thickness(0, 6, 0, 4) };
            panel.Children.Add(_preview);
            panel.Children.Add(Note("Оранжевый — молния · синий — стёжка · пунктир — пена"));
            _summary = Note(""); panel.Children.Add(_summary);
            _mode = Choice(panel, "Размер ноутбука", "По корпусу, мм", "По диагонали экрана (оценка)");
            _bodySize = new StackPanel(); panel.Children.Add(_bodySize);
            _width = Number(_bodySize, "Ширина корпуса, мм", "325");
            _height = Number(_bodySize, "Глубина корпуса, мм", "225");
            _screenSize = new StackPanel(); panel.Children.Add(_screenSize);
            _diagonal = Number(_screenSize, "Диагональ экрана, дюймы", "14");
            _aspect = Choice(_screenSize, "Стороны экрана", "16:10", "16:9", "3:2", "4:3");
            _bezel = Number(_screenSize, "Рамка с каждой стороны, мм", "8");
            _screenSize.Children.Add(Note("Диагональ описывает экран. Размер корпуса с рамкой — приблизительный; для раскроя измерьте ноутбук."));
            _thickness = Number(panel, "Толщина ноутбука, мм", "18");
            _ease = Number(panel, "Свобода по краям с каждой стороны, мм", "5");

            StackPanel layers = Section(panel, "Пакет боковинок и глубина кольца");
            layers.Children.Add(Note("Снаружи: плащёвка + синтепон + стабилизатор. Затем пена и микрофибра. Указывайте толщины в сжатом виде."));
            _loft = Number(layers, "Стёганый пакет, мм", "2");
            _foam = Number(layers, "Пена с каждой стороны, мм", "4,5");
            _lining = Number(layers, "Микрофибра, мм", "0,5");
            _depthEase = Number(layers, "Свобода по толщине, мм", "2");
            _foamInset = Number(layers, "Отступ пены внутрь от шва, мм", "2");
            layers.Children.Add(Note("Кольцо: плащёвка + синтепон + микрофибра, без пены. Его глубина учитывает ноутбук и оба пакета боковинок."));

            StackPanel seams = Section(panel, "Швы, углы и молния");
            _allowance = Number(seams, "Припуск на швы, мм", "5");
            _fixingOffset = Number(seams, "Фиксация наружу от шва сборки, мм", "2");
            _radius = Number(seams, "Радиус угла по шву, мм", "12");
            _zipDrop = Number(seams, "Спуск молнии с каждой стороны, %", "25");
            _zipWidth = Number(seams, "Молния с лентами, мм (0 = расчёт)", "40");
            _zipTail = Number(seams, "Запас ленты за припусками, мм", "20");
            seams.Children.Add(Note("Голая молния без планок: весь верх и по ¼ высоты с каждой стороны. Её концы соединяются с нижней полосой. Ширина с лентами минус два припуска задаёт глубину кольца."));

            _quilting = Choice(panel, "Стёжка боковинок", "Прямые", "Плавные дуги", "Углы / шевроны", "Без стёжки");
            _step = Number(panel, "Шаг рядов стёжки, мм", "45");
            _quiltInset = Number(panel, "Отступ стёжки от края заготовки, мм", "10");
            StackPanel shrink = Section(panel, "Усадка стёжки и вылет печати");
            _shrinkX = Number(shrink, "Усадка по ширине, %", "0");
            _shrinkY = Number(shrink, "Усадка по высоте, %", "0");
            _bleed = Number(shrink, "Вылет за заготовку, мм", "5");
            _trimMargin = Number(shrink, "Запас заготовки за припуском, мм", "15");
            _commonWidth = Number(shrink, "Общая ширина до стёжки, мм (0 = авто)", "0");
            _commonHeight = Number(shrink, "Общая высота до стёжки, мм (0 = авто)", "0");
            shrink.Children.Add(Note("Для общего шаблона стёжки нескольких размеров задайте одинаковую большую заготовку. Контур формы каждого чехла расположится по её центру."));
            shrink.Children.Add(Note("Например, 100 мм стали 97 мм — усадка 3%. Увеличиваются заготовка до стёжки и печать. Итоговое лекало и кольцо сохраняют размер."));

            StackPanel jigs = Section(panel, "Пластиковые шаблоны · резка на лазере", true);
            jigs.Children.Add(Note("Два листа на каждый шаблон. CUT — замкнутые контуры резки; ориентиры и подписи находятся на отдельных непечатаемых слоях."));
            _guideDiameter = Number(jigs, "Наружный диаметр выступа, мм", "");
            _guideHeight = Number(jigs, "Высота выступа над пластиной, мм", "");
            _guideCentered = new CheckBox { Content = "Игла по центру выступа — проверено", FontSize = 11, Margin = new Thickness(0, 3, 0, 6) };
            jigs.Children.Add(_guideCentered);
            _plasticThickness = Number(jigs, "Толщина одного листа пластика, мм", "1");
            _jigClearance = Number(jigs, "Рабочий зазор паза по диаметру, мм", "0,3");
            _jigFrame = Number(jigs, "Рамка за краем заготовки, мм", "25");
            _jigBridge = Number(jigs, "Цельная перемычка формы, мм", "15");
            _jigHole = Number(jigs, "Отверстия совмещения, мм (0 = нет)", "3");
            _jigKerf = Number(jigs, "Ширина лазерного реза, мм", "0");
            jigs.Children.Add(Note("0 — компенсация в генераторе отключена. Если ширина реза задана здесь, повторно в ПО лазера её не применять. Зазор проверяется коротким пробным пазом."));
            _includeJigs = new CheckBox { Content = "Добавлять пластик (CDR + SVG) в полный комплект", FontSize = 11,
                Margin = new Thickness(0, 3, 0, 6) };
            jigs.Children.Add(_includeJigs);
            _jigSummary = Note(""); jigs.Children.Add(_jigSummary);
            _jigQuilt = Button("Пластик для стёжки в CorelDRAW", (_, __) => OpenJig(true));
            _jigShape = Button("Пластик для формы в CorelDRAW", (_, __) => OpenJig(false));
            _saveJigs = Button("Сохранить шаблоны CDR + SVG", (_, __) => SaveJigs());
            jigs.Children.Add(_jigQuilt); jigs.Children.Add(_jigShape); jigs.Children.Add(_saveJigs);
            _guideCentered.Checked += (_, __) => Update(); _guideCentered.Unchecked += (_, __) => Update();
            _includeJigs.Checked += (_, __) => Update(); _includeJigs.Unchecked += (_, __) => Update();

            StackPanel art = Section(panel, "Принты для сублимации");
            art.Children.Add(Note("Изображение заполняет заготовку с обрезкой по центру. Вывод боковинок: PNG 300 dpi + SVG точного размера. Полосы периметра оформляются в отдельном шаблоне."));
            Artwork(art, "Перед", true, out _frontLabel);
            _same = new CheckBox { Content = "Одинаковый принт с обеих сторон", IsChecked = true, FontSize = 11,
                Margin = new Thickness(0, 4, 0, 4) };
            art.Children.Add(_same);
            _backPanel = new StackPanel(); art.Children.Add(_backPanel);
            Artwork(_backPanel, "Спинка", false, out _backLabel);
            _mirror = new CheckBox { Content = "Зеркалить файлы для сублимации", IsChecked = false, FontSize = 11,
                Margin = new Thickness(0, 4, 0, 4) };
            art.Children.Add(_mirror);
            art.Children.Add(Note("Включайте зеркало здесь либо в драйвере принтера один раз."));
            _artInfo = Note(""); art.Children.Add(_artInfo);
            _same.Checked += (_, __) => Update(); _same.Unchecked += (_, __) => Update();
            _mirror.Checked += (_, __) => Update(); _mirror.Unchecked += (_, __) => Update();

            var actions = new UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 0) };
            _create = Button("Лекала в CorelDRAW", (_, __) => Open(0)); actions.Children.Add(_create);
            _quilt = Button("Линии стёжки (ткань)", (_, __) => Open(1)); actions.Children.Add(_quilt);
            panel.Children.Add(actions);
            _shape = Button("Контур формы (ткань)", (_, __) => Open(2)); panel.Children.Add(_shape);
            _save = DockerTheme.Primary(Button("Сохранить комплект файлов", async (_, __) => await Save()));
            panel.Children.Add(_save);
            _open = Button("Открыть папку комплекта", (_, __) =>
                Safe(() => Process.Start(new ProcessStartInfo(_folder) { UseShellExecute = true })));
            _open.IsEnabled = false; panel.Children.Add(_open);
            panel.Children.Add(Button("Устройство двух шаблонов и материалы", (_, __) => Safe(OpenGuide)));
            panel.Children.Add(Note("SVG — масштаб 1:1. Сначала проверьте посадку и усадку на пробном образце."));
            panel.Children.Add(Note("Для лазера используйте кнопки «Пластик»: каждый шаблон — новый CDR с двумя страницами и отдельными слоями CUT / ENGRAVE / INFO."));
            _ready = true; Update();
        }

        private PuffySleeveOptions Options(bool includeJigs = false)
        {
            bool diagonal = _mode.SelectedIndex == 1;
            string[] ratio = ((string)_aspect.SelectedItem).Split(':');
            bool quilt = _quilting.SelectedIndex != 3;
            return new PuffySleeveOptions
            {
                UseDiagonal = diagonal,
                DeviceWidth = diagonal ? 325 : Read(_width, "Ширина корпуса"),
                DeviceHeight = diagonal ? 225 : Read(_height, "Глубина корпуса"),
                DiagonalInches = diagonal ? Read(_diagonal, "Диагональ") : 14,
                Bezel = diagonal ? Read(_bezel, "Рамка экрана") : 8,
                AspectWidth = Double.Parse(ratio[0], CultureInfo.InvariantCulture),
                AspectHeight = Double.Parse(ratio[1], CultureInfo.InvariantCulture),
                DeviceThickness = Read(_thickness, "Толщина ноутбука"), Ease = Read(_ease, "Свобода по краям"),
                Loft = Read(_loft, "Стёганый пакет"), FoamThickness = Read(_foam, "Пена"),
                LiningThickness = Read(_lining, "Микрофибра"), DepthEase = Read(_depthEase, "Свобода по толщине"),
                FoamInset = Read(_foamInset, "Отступ пены"), SeamAllowance = Read(_allowance, "Припуск"),
                CornerRadius = Read(_radius, "Радиус"), ZipperDropPercent = Read(_zipDrop, "Спуск молнии"),
                ZipperTapeWidth = Read(_zipWidth, "Полная ширина молнии с лентами"), ZipperTail = Read(_zipTail, "Запас ленты"),
                Quilting = (SleeveQuilting)_quilting.SelectedIndex,
                QuiltStep = quilt ? Read(_step, "Шаг стёжки") : 45,
                QuiltInset = quilt ? Read(_quiltInset, "Отступ стёжки") : 10,
                ShrinkX = Read(_shrinkX, "Усадка по ширине"), ShrinkY = Read(_shrinkY, "Усадка по высоте"),
                Bleed = Read(_bleed, "Вылет печати"),
                TrimMargin = Read(_trimMargin, "Запас заготовки"), FixingOffset = Read(_fixingOffset, "Строчка фиксации"),
                CommonBlankWidth = Read(_commonWidth, "Общая ширина заготовки"),
                CommonBlankHeight = Read(_commonHeight, "Общая высота заготовки"),
                Jigs = includeJigs && _includeJigs.IsChecked == true ? JigOptions() : null
            };
        }

        private void Update()
        {
            if (!_ready) return;
            _bodySize.Visibility = _mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            _screenSize.Visibility = _mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            _backPanel.Visibility = _same.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            _step.IsEnabled = _quiltInset.IsEnabled = _quilting.SelectedIndex != 3;
            try
            {
                PuffySleevePattern p = PuffySleevePattern.Build(Options());
                DockerTheme.UseTheme(_summary, TextBlock.ForegroundProperty, DockerTheme.TextBrush);
                _summary.Text = "Боковинка по шву: " + Size(p.SeamWidth, p.SeamHeight) +
                    " · крой: " + Size(p.CutWidth, p.CutHeight) + "\nКольцо: " + F(p.RingDepth) +
                    " мм · молния по шву: " + F(p.ZipperLength) + " мм\nМолния с лентами: " + F(p.ZipperTapeWidth) +
                    " мм · заготовка до стёжки: " +
                    Size(p.BlankWidth, p.BlankHeight) + " · пена: " + Size(p.FoamWidth, p.FoamHeight);
                if (_mode.SelectedIndex == 1) _summary.Text = "Корпус ≈ " + Size(p.DeviceWidth, p.DeviceHeight) + "\n" + _summary.Text;
                if (p.RingDepth < p.RequiredRingDepth)
                    _summary.Text += "\nПо толщине пакета ориентир ≈ " + F(p.RequiredRingDepth) +
                        " мм, между строчками молнии — " + F(p.RingDepth) + " мм. Проверьте посадку на образце.";
                _preview.Source = Preview(p);
                _artInfo.Text = ArtworkInfo(_frontBitmap, p, "Перед") + "\n" + ArtworkInfo(BackBitmap(), p, "Спинка");
                _create.IsEnabled = _quilt.IsEnabled = _shape.IsEnabled = _save.IsEnabled = !_busy;
                UpdateJigs(p);
            }
            catch (Exception ex)
            {
                _preview.Source = null;
                DockerTheme.UseTheme(_summary, TextBlock.ForegroundProperty, DockerTheme.ErrorTextBrush);
                _summary.Text = ex.GetBaseException().Message;
                _artInfo.Text = "";
                _create.IsEnabled = _quilt.IsEnabled = _shape.IsEnabled = _save.IsEnabled = false;
                _jigQuilt.IsEnabled = _jigShape.IsEnabled = _saveJigs.IsEnabled = false;
                _jigSummary.Text = "Сначала исправьте размеры изделия.";
            }
        }

        private SleeveJigOptions JigOptions() => new SleeveJigOptions
        {
            GuideDiameter = Read(_guideDiameter, "Диаметр направляющего выступа"),
            GuideHeight = Read(_guideHeight, "Высота направляющего выступа"),
            CenteredGuideConfirmed = _guideCentered.IsChecked == true,
            PlasticThickness = Read(_plasticThickness, "Толщина пластика"),
            Clearance = Read(_jigClearance, "Зазор паза"), Frame = Read(_jigFrame, "Рамка"),
            Bridge = Read(_jigBridge, "Перемычка"), RegistrationHole = Read(_jigHole, "Совмещение"),
            Kerf = Read(_jigKerf, "Ширина лазерного реза")
        };

        private void UpdateJigs(PuffySleevePattern p)
        {
            try
            {
                SleeveJigOptions options = JigOptions();
                SleeveJigPlan form = SleeveJigPlan.Build(p, options, false);
                SleeveJigPlan quilt = p.QuiltPaths.Count == 0 ? null : SleeveJigPlan.Build(p, options, true);
                _jigSummary.Text = "Готовый паз: " + F(form.SlotWidth) + " мм. Форма: " + Size(form.Width, form.Height) +
                    (quilt == null ? "" : "; стёжка: " + Size(quilt.Width, quilt.Height)) + ". По 2 листа. Фиксация формы — 4 прохода.";
                _jigShape.IsEnabled = _saveJigs.IsEnabled = !_busy;
                _jigQuilt.IsEnabled = !_busy && quilt != null;
            }
            catch (Exception ex)
            {
                _jigSummary.Text = ex.GetBaseException().Message;
                _jigQuilt.IsEnabled = _jigShape.IsEnabled = _saveJigs.IsEnabled = false;
                if (_includeJigs.IsChecked == true) _save.IsEnabled = false;
            }
        }

        private void OpenJig(bool quilting) => Safe(() =>
        {
            SleeveJigPlan p = SleeveJigPlan.Build(PuffySleevePattern.Build(Options()), JigOptions(), quilting);
            SleeveJigOutput.CreateInCorel(p);
            _status("Пластик · " + p.Title + ": создан CDR 1:1 с нижним и верхним листами. Для лазера печатается только слой CUT.", false);
        });

        private void SaveJigs() => Safe(() =>
        {
            PuffySleevePattern p = PuffySleevePattern.Build(Options());
            SleeveJigOptions options = JigOptions();
            SleeveJigPlan.Build(p, options, false);
            if (p.QuiltPaths.Count > 0) SleeveJigPlan.Build(p, options, true);
            string parent;
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Куда сохранить пластиковые шаблоны CDR + SVG?" })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                parent = dialog.SelectedPath;
            }
            _folder = Path.Combine(parent, "Шаблоны-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(_folder); _open.IsEnabled = true;
            SaveJigDocuments(_folder, p, options);
            _status("Пластиковые шаблоны сохранены: " + _folder + ". Каждый CDR — два листа; CUT-SVG готовы для импорта в ПО лазера.", false);
        });

        private static void SaveJigDocuments(string folder, PuffySleevePattern pattern, SleeveJigOptions options)
        {
            string marker = Path.Combine(folder, "CDR_НЕ_ЗАВЕРШЕНО.txt");
            File.WriteAllText(marker, "Сохранение CDR не завершено. SVG могут быть готовы; повторите создание CDR в CorelDRAW.");
            foreach (bool quilting in pattern.QuiltPaths.Count == 0 ? new[] { false } : new[] { true, false })
            {
                SleeveJigPlan p = SleeveJigPlan.Build(pattern, options, quilting);
                SleeveJigOutput.Save(folder, p);
                dynamic doc = SleeveJigOutput.CreateInCorel(p);
                CorelDocumentSaver.SaveAs(doc, Path.Combine(folder, quilting ? "05-quilting-jig.cdr" : "06-shaping-jig.cdr"));
            }
            File.Delete(marker);
        }

        private DrawingImage Preview(PuffySleevePattern p)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                for (int i = 0; i < 2; i++)
                {
                    dc.PushTransform(new TranslateTransform(i * (p.CutWidth + 20), 0));
                    Geometry cut = Geometry.Parse(p.CutPath);
                    dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(222, 231, 236)), new Pen(Brushes.SlateGray, 1), cut);
                    BitmapSource artwork = i == 0 ? _frontBitmap : BackBitmap();
                    if (artwork != null)
                    {
                        dc.PushClip(cut);
                        var brush = new ImageBrush(artwork) { Stretch = Stretch.UniformToFill };
                        if (_mirror.IsChecked == true) brush.RelativeTransform = new ScaleTransform(-1, 1, 0.5, 0.5);
                        dc.DrawRectangle(brush, null, new Rect(-p.BlankMarginX - p.Bleed / p.ScaleX,
                            -p.BlankMarginY - p.Bleed / p.ScaleY, p.PrintWidth / p.ScaleX, p.PrintHeight / p.ScaleY));
                        dc.Pop();
                    }
                    dc.PushClip(cut);
                    foreach (string path in p.QuiltPaths)
                        dc.DrawGeometry(null, new Pen(Brushes.SteelBlue, 1.3), Geometry.Parse(path));
                    dc.Pop();
                    dc.DrawGeometry(null, new Pen(Brushes.SlateGray, 0.9) { DashStyle = DashStyles.Dash }, Geometry.Parse(p.FoamPath));
                    dc.DrawGeometry(null, new Pen(Brushes.DimGray, 0.8) { DashStyle = DashStyles.Dash }, Geometry.Parse(p.SeamPath));
                    dc.DrawGeometry(null, new Pen(Brushes.Chocolate, 3), Geometry.Parse(p.ZipperPath));
                    dc.Pop();
                }
            }
            return new DrawingImage(visual.Drawing);
        }

        private void Open(int kind)
        {
            Safe(() =>
            {
                PuffySleevePattern p = PuffySleevePattern.Build(Options());
                string path = Path.Combine(Path.GetTempPath(), "Vanya-Sleeve-" + Guid.NewGuid().ToString("N") + ".svg");
                (kind == 1 ? PuffySleeveOutput.Quilting(p) : kind == 2 ? PuffySleeveOutput.Shaping(p) : PuffySleeveOutput.Patterns(p)).Save(path);
                PuffySleeveOutput.OpenPatterns(path);
                _status((kind == 1 ? "Схема стёжки" : kind == 2 ? "Схема формы" : "Лекала") + " — отдельный документ, масштаб 1:1.", false);
            });
        }

        private async Task Save()
        {
            if (_busy) return;
            try
            {
                PuffySleeveOptions options = Options(true);
                PuffySleevePattern p = PuffySleevePattern.Build(options);
                if (options.Jigs != null)
                {
                    SleeveJigPlan.Build(p, options.Jigs, false);
                    if (p.QuiltPaths.Count > 0) SleeveJigPlan.Build(p, options.Jigs, true);
                }
                string parent;
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Куда сохранить комплект лекал чехла?" })
                {
                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                    parent = dialog.SelectedPath;
                }
                string front = _front, back = Back(); bool mirror = _mirror.IsChecked == true;
                _busy = true; IsEnabled = false;
                _status("Сохраняю лекала, схему стёжки и файлы сублимации…", false);
                // WPF rendering needs an STA thread; Corel calls stay on the UI thread.
                var completion = new TaskCompletionSource<string>();
                var thread = new Thread(() =>
                {
                    try { completion.SetResult(PuffySleeveOutput.Save(parent, options, front, back, mirror)); }
                    catch (Exception ex) { completion.SetException(ex); }
                }) { IsBackground = true };
                thread.SetApartmentState(ApartmentState.STA); thread.Start();
                _folder = await completion.Task;
                _open.IsEnabled = true;
                if (options.Jigs != null) SaveJigDocuments(_folder, p, options.Jigs);
                _status("Комплект сохранён: " + _folder + (String.IsNullOrEmpty(front) && String.IsNullOrEmpty(back)
                    ? ". Принты не заданы — сохранён шаблон размещения для сублимации." : ". Готовые принты боковинок — PNG 300 dpi и SVG."), false);
            }
            catch (Exception ex) { Error(ex); }
            finally { _busy = false; IsEnabled = true; Update(); }
        }

        private void Artwork(StackPanel panel, string title, bool front, out TextBlock label)
        {
            label = Note(title + ": принт не выбран"); panel.Children.Add(label);
            var buttons = new UniformGrid { Columns = 3 };
            buttons.Children.Add(Button("Из файла", (_, __) => Safe(() =>
            {
                var dialog = new OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp", CheckFileExists = true };
                if (dialog.ShowDialog() == true) SetArtwork(front, dialog.FileName);
            })));
            buttons.Children.Add(Button("Из выделения", (_, __) => Safe(() => SetArtwork(front, AiSelectionCapture.Capture()))));
            buttons.Children.Add(Button("Очистить", (_, __) => SetArtwork(front, null)));
            panel.Children.Add(buttons);
        }
        private void SetArtwork(bool front, string path)
        {
            BitmapSource bitmap = path == null ? null : PuffySleeveOutput.ReadBitmap(path);
            if (front) { _front = path; _frontBitmap = bitmap; _frontLabel.Text = "Перед: " + (path == null ? "принт не выбран" : Path.GetFileName(path)); }
            else { _back = path; _backBitmap = bitmap; _backLabel.Text = "Спинка: " + (path == null ? "принт не выбран" : Path.GetFileName(path)); }
            Update();
        }
        private string Back() { return _same.IsChecked == true ? _front : _back; }
        private static void OpenGuide()
        {
            string path = Path.Combine(Path.GetTempPath(), "Vanya-Sleeve-Jigs-" + Guid.NewGuid().ToString("N") + ".html");
            using (var source = typeof(PuffySleeveTab).Assembly.GetManifestResourceStream("VanyaTools.Native.Assets.puffy-sleeve-jigs.html"))
            {
                if (source == null) throw new InvalidOperationException("Схема шаблонов отсутствует в сборке.");
                using (var output = File.Create(path)) source.CopyTo(output);
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        private BitmapSource BackBitmap() { return _same.IsChecked == true ? _frontBitmap : _backBitmap; }
        private static string ArtworkInfo(BitmapSource bitmap, PuffySleevePattern p, string name)
        {
            if (bitmap == null) return name + ": шаблон без принта";
            double dpi = Math.Min(bitmap.PixelWidth / p.PrintWidth, bitmap.PixelHeight / p.PrintHeight) * 25.4;
            return name + ": исходник ≈ " + F(dpi) + " dpi" + (dpi < 150 ? " — желательно более крупное изображение" : "");
        }
        private void Safe(Action action) { try { action(); } catch (Exception ex) { Error(ex); } }
        private void Error(Exception ex) { Log.Error("Puffy sleeve failed.", ex); _status(ex.GetBaseException().Message, true); }
        private static double Read(TextBox field, string name)
        {
            double value;
            if (!Double.TryParse(field.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || Double.IsNaN(value) || Double.IsInfinity(value)) throw new InvalidOperationException(name + ": введите число.");
            return value;
        }
        private static string F(double value) { return value.ToString("0.#", CultureInfo.CurrentCulture); }
        private static string Size(double w, double h) { return F(w) + " × " + F(h) + " мм"; }
        private TextBox Number(Panel panel, string title, string value)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            var label = Note(title); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
            var field = new TextBox { Text = value, FontSize = 11, Padding = new Thickness(3), VerticalContentAlignment = VerticalAlignment.Center };
            Grid.SetColumn(field, 1); row.Children.Add(field); panel.Children.Add(row);
            field.TextChanged += (_, __) => Update(); return field;
        }
        private ComboBox Choice(Panel panel, string title, params string[] items)
        {
            panel.Children.Add(Note(title));
            var combo = new ComboBox { FontSize = 11, Margin = new Thickness(0, 0, 0, 5) };
            foreach (string item in items) combo.Items.Add(item);
            combo.SelectedIndex = 0; combo.SelectionChanged += (_, __) => Update(); panel.Children.Add(combo); return combo;
        }
        private static StackPanel Section(Panel panel, string title, bool expanded = false)
        {
            var contents = new StackPanel { Margin = new Thickness(4) };
            panel.Children.Add(new Expander { Header = title, Content = contents, IsExpanded = expanded, FontSize = 11, Margin = new Thickness(0, 3, 0, 5) });
            return contents;
        }
        private static TextBlock Note(string text)
        {
            var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
                FontSize = 10.5, Margin = new Thickness(0, 0, 4, 4) };
            DockerTheme.UseTheme(note, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush);
            return note;
        }
        private static Button Button(string text, RoutedEventHandler handler)
        {
            var button = new Button { Content = text, FontSize = 10.5, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 0, 3, 4) };
            button.Click += handler; return button;
        }
    }
}
