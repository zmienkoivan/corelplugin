using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class FrameBagWindow : Window
    {
        private readonly Dictionary<string, NumberField> _fields = new Dictionary<string, NumberField>();
        private readonly FrameBagPreview _side = new FrameBagPreview(FrameBagView.Side);
        private readonly FrameBagPreview _front = new FrameBagPreview(FrameBagView.Front);
        private readonly FrameBagPreview _pieces = new FrameBagPreview(FrameBagView.Pieces);
        private readonly TextBlock _message, _summary;
        private readonly TextBox _name;
        private readonly ComboBox _leftZipper, _rightZipper, _frameShape;
        private readonly System.Windows.Controls.Primitives.ToggleButton _leftView, _rightView;
        private int _editSide;
        private readonly CheckBox _notches;
        private int _cornerCount;
        private FrameBagSetup _anchors;
        private readonly Button _apply, _dxf, _save;
        private readonly DispatcherTimer _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        private FrameBagPlan _plan;
        private bool _ready, _sync;
        private FrameBagSetup _last;
        internal FrameBagPlan Plan => _plan;
        internal FrameBagSetup Setup => (_plan?.Setup ?? _last).Copy();

        internal FrameBagWindow(FrameBagSetup initial)
        {
            _last = initial.Copy(); _anchors = initial.Copy();
            Title = "Выкройка велосумки · Vanya Tools";
            Width = Math.Min(1240, SystemParameters.WorkArea.Width - 28);
            Height = Math.Min(850, SystemParameters.WorkArea.Height - 28);
            MinWidth = 850; MinHeight = 550; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            DockerTheme.Apply(this);
            var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
            DockerTheme.UseTheme(root, Panel.BackgroundProperty, DockerTheme.WindowBackground);
            var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
            _message = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 7), FontSize = 12 };
            footer.Children.Add(_message);
            var actions = new DockPanel { LastChildFill = false };
            var close = Button("Закрыть", (_, __) => Close()); close.IsCancel = true;
            DockPanel.SetDock(close, Dock.Right); actions.Children.Add(close);
            _apply = DockerTheme.Primary(Button("Создать комплект в Corel", (_, __) => { if (_plan != null) DialogResult = true; }));
            DockPanel.SetDock(_apply, Dock.Right); actions.Children.Add(_apply);
            _dxf = Button("DXF для лазера…", (_, __) => ExportDxf());
            DockPanel.SetDock(_dxf, Dock.Right); actions.Children.Add(_dxf);
            footer.Children.Add(actions);

            var fields = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            var scroll = new ScrollViewer { Content = fields, Width = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            DockPanel.SetDock(scroll, Dock.Left); root.Children.Add(scroll);
            fields.Children.Add(new TextBlock { Text = "Все размеры в миллиметрах", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7) });
            _name = new TextBox { Text = initial.Name, MaxLength = 80, MinHeight = 28, Margin = new Thickness(0, 0, 0, 6) };
            fields.Children.Add(DockerLayout.LabeledField("Название", _name));
            _name.TextChanged += (_, __) => Queue();
            var presets = new Grid(); presets.ColumnDefinitions.Add(new ColumnDefinition()); presets.ColumnDefinitions.Add(new ColumnDefinition());
            _save = Button("Сохранить замеры…", (_, __) => Save());
            presets.Children.Add(_save);
            var load = Button("Загрузить…", (_, __) => Load()); Grid.SetColumn(load, 1); presets.Children.Add(load);
            fields.Children.Add(presets);
            var frame = DockerLayout.ToolGroup("1 · Внутренний проём рамы",
                "Треугольник: A — верх подседельной, B — передний угол, C — низ подседельной; нужны три стороны. " +
                "Трапеция: A — верх подседельной, B/C — верх/низ стакана, D — низ подседельной; нужны четыре стороны и диагональ A–C. " +
                "Допускается произвольный выпуклый четырёхугольник. Верхняя труба на схеме горизонтальна.");
            fields.Children.Add(frame);
            _frameShape = new ComboBox { ItemsSource = new[] { "Треугольник · 3 замера", "Трапеция · 5 замеров" },
                SelectedIndex = initial.TriangleFrame ? 0 : 1, MinHeight = 28, Margin = new Thickness(0, 0, 0, 6) };
            frame.Children.Add(_frameShape);
            Add(frame, "Top", "A–B · верхняя труба", 10, 1500, initial.Top);
            Add(frame, "Seat", "D–A · подседельная", 10, 1500, initial.Seat);
            Add(frame, "Down", "C–D · нижняя труба", 10, 1800, initial.Down);
            Add(frame, "Head", "B–C · стакан", 1, 500, initial.Head);
            Add(frame, "Diagonal", "A–C · диагональ", 10, 1800, initial.Diagonal);
            _frameShape.SelectionChanged += (_, __) => { UpdateFrameControls(); Queue(); };
            UpdateFrameControls();
            var bag = DockerLayout.ToolGroup("2 · Сумка",
                "Высота откладывается вниз от верхней границы сумки до скругления углов. Можно задать независимые высоты спереди и сзади. " +
                "Глубина — толщина поперёк рамы. Нижняя глубина задаётся у самой низкой точки; при наклонном дне глубина вдоль него меняется. " +
                "Зелёные буквы A–E отмечают углы сумки. Общий радиус меняет все углы, ниже каждый можно настроить отдельно. Длины стенок пересчитываются по шву.");
            fields.Children.Add(bag);
            Add(bag, "Height", "Высота сзади", 10, 1500, initial.Height);
            Add(bag, "FrontHeight", "Высота спереди", 10, 1500, initial.SplitHeights ? initial.FrontHeight : initial.Height);
            Add(bag, "TopDepth", "Глубина сверху", 10, 300, initial.TopDepth);
            Add(bag, "BottomDepth", "Глубина снизу", 10, 300, initial.BottomDepth);
            Add(bag, "CornerRadius", "Радиус всех углов", 0, 200, initial.CornerRadius, AllCornersChanged);
            var corners = new StackPanel();
            for (int n = 0; n < 5; n++)
                Add(corners, "Radius" + n, "Угол " + (char)('A' + n), 0, 200,
                    initial.CornerRadii != null && n < initial.CornerRadii.Length ? initial.CornerRadii[n] : initial.CornerRadius);
            bag.Children.Add(new Expander { Header = "Радиусы по углам · мм", Content = corners, IsExpanded = true });
            var closure = DockerLayout.ToolGroup("3 · Молнии и клапаны",
                "Для каждой стороны выберите конструкцию. М1 и М2 перетаскиваются на виде сбоку: они задают начало и конец молнии. " +
                "У «Двух деталей» линия проходит через якоря до границ боковины; ширина открытия оставляет место молнии между деталями. " +
                "У «Прорези» и «Клапана» якоря — концы прорези. Их положение фиксировано в миллиметрах от рамы, изменение высоты сумки их не сдвигает. Клапан — отдельная накладная деталь с припусками.");
            fields.Children.Add(closure);
            string[] modes = { "Без молнии", "Прорезь", "Две детали", "Прорезь + клапан" };
            _leftZipper = new ComboBox { ItemsSource = modes, SelectedIndex = (int)initial.LeftZipper, MinHeight = 27 };
            _rightZipper = new ComboBox { ItemsSource = modes, SelectedIndex = (int)initial.RightZipper, MinHeight = 27 };
            closure.Children.Add(DockerLayout.FieldRow("Левая боковина", _leftZipper, 155));
            closure.Children.Add(DockerLayout.FieldRow("Правая боковина", _rightZipper, 155));
            _leftZipper.SelectionChanged += (_, __) => { SelectSide(0); Queue(); };
            _rightZipper.SelectionChanged += (_, __) => { SelectSide(1); Queue(); };
            Add(closure, "ZipperWidth", "Ширина открытия", 1, 40, initial.ZipperWidth);
            Add(closure, "FlapDepth", "Ширина клапана", 5, 100, initial.FlapDepth);
            Add(closure, "FlapExtra", "Выступ по торцам", 0, 50, initial.FlapExtra);
            closure.Children.Add(Button("Выровнять молнию", (_, __) =>
            {
                if (_editSide == 0) _anchors.LeftZipEndYmm = _anchors.LeftZipStartYmm;
                else _anchors.RightZipEndYmm = _anchors.RightZipStartYmm;
                Queue();
            }));
            _side.AnchorChanged += SetAnchor;
            var cutting = DockerLayout.ToolGroup("4 · Посадка и резка",
                "Зазор — от рамы до готовой сумки. Припуск добавляется наружу от линии шва. " +
                "Выдаётся две зеркальные боковины и отдельные стенки между точками A–E. " +
                "У скруглённого угла стенки соединяются на середине дуги. " +
                "Надсечки на припуске: две рядом с началом каждой стенки, одна перед концом. " +
                "Такие же отметки стоят на обеих боковинах по длине шва. Глубина ограничена половиной припуска. " +
                "DXF содержит только красные замкнутые линии резки, без подписей и швов. Крепёжные ремни в этой версии не раскраиваются.");
            fields.Children.Add(cutting);
            Add(cutting, "Clearance", "Зазор до рамы", 0, 100, initial.Clearance);
            Add(cutting, "Allowance", "Припуск на шов", 0, 50, initial.Allowance);
            _notches = new CheckBox { Content = "Надсечки совмещения", IsChecked = initial.Notches, Margin = new Thickness(0, 2, 0, 5) };
            cutting.Children.Add(_notches);
            Add(cutting, "NotchDepth", "Глубина надсечки", .2, 5, initial.NotchDepth);
            _fields["NotchDepth"].View.Visibility = initial.Notches ? Visibility.Visible : Visibility.Collapsed;
            _notches.Checked += (_, __) => { _fields["NotchDepth"].View.Visibility = Visibility.Visible; Queue(); };
            _notches.Unchecked += (_, __) => { _fields["NotchDepth"].View.Visibility = Visibility.Collapsed; Queue(); };

            var right = new DockPanel(); root.Children.Add(right);
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
            toolbar.Children.Add(Button("−", (_, __) => Zoom(1 / 1.2)));
            toolbar.Children.Add(Button("+", (_, __) => Zoom(1.2)));
            toolbar.Children.Add(Button("Вписать виды", (_, __) => { _side.Fit(); _front.Fit(); _pieces.Fit(); }));
            toolbar.ToolTip = "Колесо — зум; средняя/правая кнопка или пробел + перетаскивание — перемещение.";
            DockPanel.SetDock(toolbar, Dock.Top); right.Children.Add(toolbar);
            _summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 7, 0, 0) };
            DockPanel.SetDock(_summary, Dock.Bottom); right.Children.Add(_summary);
            var tabs = new TabControl(); right.Children.Add(tabs);
            var views = new Grid();
            views.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            views.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            var sideView = new DockPanel();
            var sideHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 3, 6, 3) };
            sideHeader.Children.Add(new TextBlock { Text = "Вид сбоку", VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 10, 0) });
            _leftView = SideButton("Левая", 0); _rightView = SideButton("Правая", 1);
            sideHeader.Children.Add(_leftView); sideHeader.Children.Add(_rightView);
            DockPanel.SetDock(sideHeader, Dock.Top); sideView.Children.Add(sideHeader); sideView.Children.Add(_side);
            var sideBorder = new Border { Child = sideView, Margin = new Thickness(2), BorderThickness = new Thickness(1) };
            DockerTheme.UseTheme(sideBorder, Border.BorderBrushProperty, DockerTheme.BorderBrush);
            views.Children.Add(sideBorder);
            SelectSide(0);
            var front = View("Вид спереди", _front); Grid.SetColumn(front, 1); views.Children.Add(front);
            tabs.Items.Add(new TabItem { Header = "Форма сумки", Content = views });
            tabs.Items.Add(new TabItem { Header = "Комплект деталей", Content = View("Красный — резка · синий — шов", _pieces) });
            _refresh.Tick += (_, __) => { _refresh.Stop(); Rebuild(); };
            Closed += (_, __) => { _ready = false; _refresh.Stop(); };
            _ready = true; Rebuild();
        }

        private System.Windows.Controls.Primitives.ToggleButton SideButton(string title, int side)
        {
            var button = new System.Windows.Controls.Primitives.ToggleButton { Content = title, MinHeight = 29,
                MinWidth = 70, Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(1),
                ToolTip = title + " боковина · вид снаружи; якоря молнии изменяются только на этой стороне." };
            button.Click += (_, __) => SelectSide(side);
            return button;
        }
        private void SelectSide(int side)
        {
            _editSide = side == 1 ? 1 : 0;
            _side.EditSide(_editSide);
            if (_leftView != null) _leftView.IsChecked = _editSide == 0;
            if (_rightView != null) _rightView.IsChecked = _editSide == 1;
        }

        private void AllCornersChanged()
        {
            if (_sync) return;
            try
            {
                double radius = _fields["CornerRadius"].Read();
                for (int n = 0; n < 5; n++) _fields["Radius" + n].Set(radius);
            }
            catch (ArgumentException) { }
            Queue();
        }
        private void Add(Panel parent, string key, string title, double min, double max, double value, Action changed = null)
        { var field = new NumberField(title, min, max, value, changed ?? Queue); _fields.Add(key, field); parent.Children.Add(field.View); }
        private FrameBagSetup Read() => new FrameBagSetup { Name = _name.Text.Trim(), TriangleFrame = _frameShape.SelectedIndex == 0,
            Top = _fields["Top"].Read(), Seat = _fields["Seat"].Read(), Down = _fields["Down"].Read(),
            Head = _frameShape.SelectedIndex == 0 ? _last.Head : _fields["Head"].Read(),
            Diagonal = _frameShape.SelectedIndex == 0 ? _last.Diagonal : _fields["Diagonal"].Read(), Height = _fields["Height"].Read(),
            SplitHeights = true, FrontHeight = _fields["FrontHeight"].Read(),
            LeftZipper = (BagZipper)_leftZipper.SelectedIndex, RightZipper = (BagZipper)_rightZipper.SelectedIndex,
            ZipperWidth = ClosureValue("ZipperWidth", _leftZipper.SelectedIndex != 0 || _rightZipper.SelectedIndex != 0, _last.ZipperWidth),
            FlapDepth = ClosureValue("FlapDepth", _leftZipper.SelectedIndex == 3 || _rightZipper.SelectedIndex == 3, _last.FlapDepth),
            FlapExtra = ClosureValue("FlapExtra", _leftZipper.SelectedIndex == 3 || _rightZipper.SelectedIndex == 3, _last.FlapExtra),
            LeftZipAX = _anchors.LeftZipAX, LeftZipAY = _anchors.LeftZipAY, LeftZipBX = _anchors.LeftZipBX, LeftZipBY = _anchors.LeftZipBY,
            RightZipAX = _anchors.RightZipAX, RightZipAY = _anchors.RightZipAY, RightZipBX = _anchors.RightZipBX, RightZipBY = _anchors.RightZipBY,
            FixedZipperAnchors = _anchors.FixedZipperAnchors,
            LeftZipStartXmm = _anchors.LeftZipStartXmm, LeftZipStartYmm = _anchors.LeftZipStartYmm,
            LeftZipEndXmm = _anchors.LeftZipEndXmm, LeftZipEndYmm = _anchors.LeftZipEndYmm,
            RightZipStartXmm = _anchors.RightZipStartXmm, RightZipStartYmm = _anchors.RightZipStartYmm,
            RightZipEndXmm = _anchors.RightZipEndXmm, RightZipEndYmm = _anchors.RightZipEndYmm,
            TopDepth = _fields["TopDepth"].Read(), BottomDepth = _fields["BottomDepth"].Read(),
            Clearance = _fields["Clearance"].Read(), Allowance = _fields["Allowance"].Read(), CornerRadius = _fields["CornerRadius"].Read(),
            Notches = _notches.IsChecked == true, NotchDepth = ClosureValue("NotchDepth", _notches.IsChecked == true, _last.NotchDepth) };

        private void UpdateFrameControls()
        {
            bool triangle = _frameShape.SelectedIndex == 0;
            _fields["Head"].View.Visibility = _fields["Diagonal"].View.Visibility = triangle ? Visibility.Collapsed : Visibility.Visible;
            _fields["Seat"].Label(triangle ? "C–A · подседельная" : "D–A · подседельная");
            _fields["Down"].Label(triangle ? "B–C · нижняя труба" : "C–D · нижняя труба");
        }

        private double ClosureValue(string key, bool needed, double previous) => needed ? _fields[key].Read() : previous;

        private void SetAnchor(int side, int index, double x, double y)
        {
            _anchors.FixedZipperAnchors = true;
            if (side == 0) { if (index == 0) { _anchors.LeftZipStartXmm = x; _anchors.LeftZipStartYmm = y; } else { _anchors.LeftZipEndXmm = x; _anchors.LeftZipEndYmm = y; } }
            else { if (index == 0) { _anchors.RightZipStartXmm = x; _anchors.RightZipStartYmm = y; } else { _anchors.RightZipEndXmm = x; _anchors.RightZipEndYmm = y; } }
            Queue();
        }

        private void Queue()
        {
            if (!_ready || _sync) return;
            _apply.IsEnabled = _dxf.IsEnabled = _save.IsEnabled = false;
            _refresh.Stop(); _refresh.Start();
        }
        private void Rebuild()
        {
            try
            {
                _sync = true;
                var setup = Read();
                var frame = FrameBagGeometry.Frame(setup);
                var inside = FrameBagGeometry.Offset(frame, setup.Clearance);
                double top = inside.Min(p => p.Y), maxHeight = inside.Max(p => p.Y) - top;
                if (maxHeight < 10) throw new ArgumentException("Проём слишком мал. Уменьшите зазор.");
                _fields["Height"].Limit(maxHeight); _fields["FrontHeight"].Limit(maxHeight);
                setup.Height = _fields["Height"].Read(); setup.FrontHeight = setup.SplitHeights ? _fields["FrontHeight"].Read() : setup.Height;
                var sharp = FrameBagGeometry.Segment(inside, setup.Height, setup.SplitHeights ? setup.FrontHeight : setup.Height);
                _fields["CornerRadius"].Limit(Math.Min(200, FrameBagGeometry.MaximumRadius(sharp)));
                setup.CornerRadius = _fields["CornerRadius"].Read();
                _cornerCount = sharp.Length;
                var radii = Enumerable.Range(0, _cornerCount).Select(n => _fields["Radius" + n].Read()).ToArray();
                var fitted = FrameBagGeometry.FitRadii(sharp, radii);
                for (int n = 0; n < 5; n++)
                {
                    _fields["Radius" + n].View.Visibility = n < _cornerCount ? Visibility.Visible : Visibility.Collapsed;
                    if (n < _cornerCount && fitted[n] < radii[n] - .00001)
                        _fields["Radius" + n].Set(Math.Floor(fitted[n] * 10) / 10);
                }
                radii = Enumerable.Range(0, _cornerCount).Select(n => _fields["Radius" + n].Read()).ToArray();
                for (int n = 0; n < _cornerCount; n++) _fields["Radius" + n].Limit(Math.Min(200, FrameBagGeometry.RadiusLimit(sharp, radii, n)));
                setup.CornerRadii = Enumerable.Range(0, _cornerCount).Select(n => _fields["Radius" + n].Read()).ToArray();
                FrameBagPlan preview;
                string closureError = null;
                try { _plan = FrameBagGeometry.Build(setup); preview = _plan; }
                catch (ArgumentException error) { preview = FrameBagGeometry.Build(setup, false); _plan = null; closureError = error.Message; }
                _last = preview.Setup.Copy(); _anchors = preview.Setup.Copy();
                _side.Update(preview); _front.Update(preview); _pieces.Update(_plan);
                _fields["FlapDepth"].View.Visibility = _fields["FlapExtra"].View.Visibility =
                    setup.LeftZipper == BagZipper.Flap || setup.RightZipper == BagZipper.Flap ? Visibility.Visible : Visibility.Collapsed;
                _summary.Text = _plan == null ? "Исправьте молнию перед созданием комплекта." :
                    "Комплект: " + _plan.Parts.Length + " деталей · объём ≈ " + _plan.VolumeLitres.ToString("0.00", CultureInfo.CurrentCulture) + " л\n" +
                    "Красный — резка; синий — швы. М1/М2 перетаскиваются мышью." +
                    (setup.Notches && setup.Allowance >= .4 ? " Надсечки: две — начало стенки, одна — конец." : "");
                Message(closureError ?? "Размеры в миллиметрах. Объекты создаются только по кнопке.", closureError != null);
                _apply.IsEnabled = _dxf.IsEnabled = _save.IsEnabled = _plan != null;
            }
            catch (Exception error)
            {
                _plan = null; _apply.IsEnabled = _dxf.IsEnabled = _save.IsEnabled = false;
                Message(error.GetBaseException().Message + " Показан предыдущий корректный вариант.", true);
            }
            finally { _sync = false; }
        }
        private void Zoom(double factor) { _side.Zoom(factor); _front.Zoom(factor); _pieces.Zoom(factor); }
        private void ExportDxf()
        {
            Rebuild(); if (_plan == null) return;
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "DXF|*.dxf", DefaultExt = ".dxf", AddExtension = true,
                FileName = SafeName(_plan.Setup.Name) + ".dxf" };
            if (dialog.ShowDialog(this) != true) return;
            try { File.WriteAllText(dialog.FileName, FrameBagDxf.Create(_plan), Encoding.ASCII);
                Message("DXF сохранён: " + _plan.Parts.Length + " деталей и " + _plan.Parts.Sum(p => p.Cutouts.Length) + " прорезей, миллиметры, масштаб 1:1.", false); }
            catch(Exception error) { Message(error.Message, true); }
        }
        private void Save()
        {
            Rebuild(); if (_plan == null) return;
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Замеры велосумки|*.framebag.json", DefaultExt = ".framebag.json",
                AddExtension = true, FileName = SafeName(_plan.Setup.Name) + ".framebag.json" };
            if(dialog.ShowDialog(this)!=true)return;
            try { File.WriteAllText(dialog.FileName,new JavaScriptSerializer().Serialize(_plan.Setup),Encoding.UTF8);Message("Замеры сохранены.",false); }
            catch(Exception error){Message(error.Message,true);}
        }
        private void Load()
        {
            var dialog=new Microsoft.Win32.OpenFileDialog {Filter="Замеры велосумки|*.framebag.json;*.json"};
            if(dialog.ShowDialog(this)!=true)return;
            try
            {
                if(new FileInfo(dialog.FileName).Length>32768)throw new ArgumentException("Файл замеров слишком большой.");
                var setup=new JavaScriptSerializer {MaxJsonLength=32768}.Deserialize<FrameBagSetup>(File.ReadAllText(dialog.FileName));
                FrameBagGeometry.Build(setup);
                _sync=true;
                foreach(var pair in _fields)
                {
                    double value;
                    if (pair.Key.StartsWith("Radius", StringComparison.Ordinal))
                    {
                        int corner = Int32.Parse(pair.Key.Substring(6), CultureInfo.InvariantCulture);
                        value = setup.CornerRadii != null && corner < setup.CornerRadii.Length ? setup.CornerRadii[corner] : setup.CornerRadius;
                    }
                    else value = pair.Key == "FrontHeight" && !setup.SplitHeights ? setup.Height :
                        Convert.ToDouble(typeof(FrameBagSetup).GetProperty(pair.Key).GetValue(setup,null));
                    pair.Value.Set(value, true);
                }
                _frameShape.SelectedIndex = setup.TriangleFrame ? 0 : 1;
                _anchors = setup.Copy(); _leftZipper.SelectedIndex = (int)setup.LeftZipper; _rightZipper.SelectedIndex = (int)setup.RightZipper;
                _notches.IsChecked = setup.Notches;
                _name.Text=setup.Name ?? "Велосумка"; _sync=false;Rebuild();
            }
            catch(Exception error){_sync=false;Message(error.GetBaseException().Message,true);}
        }
        private void Message(string text,bool error)
        { _message.Text=text;DockerTheme.UseTheme(_message,TextBlock.ForegroundProperty,error?DockerTheme.ErrorTextBrush:DockerTheme.MutedTextBrush); }
        private static string SafeName(string name)
        { string value=String.Concat((name??"Велосумка").Where(c=>!Path.GetInvalidFileNameChars().Contains(c))); return String.IsNullOrWhiteSpace(value)?"Велосумка":value; }
        private static Border View(string title, UIElement content)
        {
            var panel=new DockPanel();
            var header=new TextBlock {Text=title,Margin=new Thickness(8,5,8,5),FontWeight=FontWeights.SemiBold};
            DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);panel.Children.Add(content);
            var border=new Border {Child=panel,Margin=new Thickness(2),BorderThickness=new Thickness(1)};
            DockerTheme.UseTheme(border,Border.BorderBrushProperty,DockerTheme.BorderBrush);return border;
        }
        private static Button Button(string title,RoutedEventHandler click)
        {var b=new Button {Content=title,MinHeight=30,Margin=new Thickness(2),Padding=new Thickness(8,3,8,3)};b.Click+=click;return b;}

        private sealed class NumberField
        {
            private string _title;
            private readonly double _minimum,_baseMaximum;
            private readonly TextBox _input;
            private readonly Slider _slider;
            private bool _sync;
            private double _last;
            internal readonly StackPanel View=new StackPanel {Margin=new Thickness(0,1,0,5)};
            internal NumberField(string title,double minimum,double maximum,double value,Action changed)
            {
                _title=title;_minimum=minimum;_baseMaximum=maximum;_last=value;
                _input=new TextBox {MinHeight=25,VerticalContentAlignment=VerticalAlignment.Center,HorizontalContentAlignment=HorizontalAlignment.Center};
                View.Children.Add(DockerLayout.FieldRow(title,_input,72));
                _slider=new Slider {Minimum=minimum,Maximum=maximum,SmallChange=.1,LargeChange=10,Value=value,
                    TickFrequency=.1,IsSnapToTickEnabled=false,Height=20};
                System.Windows.Automation.AutomationProperties.SetName(_slider,title);
                View.Children.Add(_slider);Set(value);
                _input.TextChanged+=(_,__) =>
                {
                    if(_sync)return;
                    if(Try(out double next)) { _last=next;_sync=true;_slider.Value=next;_sync=false; }
                    changed();
                };
                _slider.ValueChanged+=(_,__) =>
                {if(_sync)return;Set(Math.Round(_slider.Value,1));changed();};
            }
            internal void Label(string title)
            {
                _title = title;
                ((TextBlock)((Grid)View.Children[0]).Children[0]).Text = title;
                System.Windows.Automation.AutomationProperties.SetName(_slider, title);
            }
            internal void Set(double value,bool resetLimit=false)
            {
                _sync=true;
                if(resetLimit)_slider.Maximum=_baseMaximum;
                _last=value;_input.Text=value.ToString("0.#",CultureInfo.CurrentCulture);_slider.Value=value;_sync=false;
            }
            internal void Limit(double maximum)
            {
                maximum=Math.Max(_minimum,Math.Min(_baseMaximum,maximum));
                _sync=true;_slider.Maximum=maximum;_sync=false;
                if(_last>maximum)Set(Math.Floor(maximum*10)/10);
            }
            private bool Try(out double value) => Double.TryParse(_input.Text.Trim().Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out value) &&
                !Double.IsInfinity(value) && !Double.IsNaN(value) && value>=_minimum && value<=_baseMaximum;
            internal double Read()
            {if(Try(out double value))return value;throw new ArgumentException(_title+": от "+_minimum+" до "+_baseMaximum+" мм.");}
        }
    }
}
