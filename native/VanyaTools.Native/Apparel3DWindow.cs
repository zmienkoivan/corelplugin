using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace VanyaTools.Native
{
    internal sealed class Apparel3DWindow : Window
    {
        private readonly Apparel3DScene _scene = new Apparel3DScene();
        private readonly TextBlock _message;
        private readonly Image _front = new Image { Height = 210, Stretch = Stretch.Uniform }, _back = new Image { Height = 210, Stretch = Stretch.Uniform };
        private readonly Slider _folds;
        private Apparel3DSource _source;
        private ApparelOrderSnapshot _snapshot;
        private bool _swapped;
        private readonly Action<string, bool> _status;
        public Apparel3DWindow(Action<string, bool> status)
        {
            _status = status; Title = "3D-примерка футболки · Vanya Tools";
            Width = Math.Min(1320, SystemParameters.WorkArea.Width - 30); Height = Math.Min(900, SystemParameters.WorkArea.Height - 30);
            MinWidth = 860; MinHeight = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            DockerTheme.Apply(this);
            var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
            DockerTheme.UseTheme(root, Panel.BackgroundProperty, DockerTheme.WindowBackground);
            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) }; DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
            bar.Children.Add(DockerTheme.Primary(Button("Взять заказ из Corel", Bind)));
            bar.Children.Add(Button("Обновить из исходного CDR", UpdateSource));
            bar.Children.Add(Button("Поменять перед / спинку", () => { if (_snapshot == null) return; _swapped = !_swapped; Apply(); }));
            bar.Children.Add(Button("Сохранить вид PNG…", Export));
            _message = new TextBlock { Text = "Откройте CDR с двумя видами футболки. По умолчанию перед слева, спинка справа.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(_message, Dock.Bottom); root.Children.Add(_message);
            var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); root.Children.Add(body);
            var controls = new StackPanel(); body.Children.Add(new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            controls.Children.Add(Note("Исходные виды из заказа", true));
            controls.Children.Add(Note("Перед")); controls.Children.Add(_front);
            controls.Children.Add(Note("Спинка")); controls.Children.Add(_back);
            controls.Children.Add(Note("Складки модели"));
            _folds = new Slider { Minimum = 0, Maximum = 1, Value = .5, Margin = new Thickness(0, 0, 0, 10) }; controls.Children.Add(_folds);
            var views = new WrapPanel(); controls.Children.Add(views);
            views.Children.Add(Button("Перед", () => _scene.View(0))); views.Children.Add(Button("Спинка", () => _scene.View(180)));
            views.Children.Add(Button("¾", () => _scene.View(-25))); views.Children.Add(Button("Вписать", () => _scene.Fit()));
            controls.Children.Add(Note("Левая кнопка — вращение\nКолесо — масштаб\nПравая кнопка — перемещение"));
            controls.Children.Add(Note("Правьте рисунок в Corel и нажимайте «Обновить», пока активны исходный CDR и страница заказа. Окно остаётся открытым при работе с документом."));
            controls.Children.Add(Note("Первая версия читает две группы видов с корпусом и четырёхузловыми контурами рукавов. Если на странице несколько моделей, выделите нужные две группы перед загрузкой."));
            controls.Children.Add(Note("Модель показывает размещение принта. Посадку, совпадение швов и производственные лекала проверяйте отдельно."));
            Grid.SetColumn(_scene, 2); body.Children.Add(_scene);
            _folds.ValueChanged += (_, __) => Apply();
            Loaded += (_, __) => { Apply(); _scene.Fit(); };
            Closed += (_, __) => { _source = null; _snapshot = null; _front.Source = _back.Source = null; };
        }
        internal void SetSnapshot(ApparelOrderSnapshot snapshot) { _snapshot = snapshot; _swapped = false; Apply(true); }
        private void Bind() => Safe(() =>
        {
            var source = Apparel3DSource.Bind(); var snapshot = source.Capture();
            _source = source; _snapshot = snapshot; _swapped = false; Apply(true);
            Message("Загружен заказ: " + snapshot.SourceName + ". Перед слева, спинка справа; при необходимости поменяйте их местами.", false);
        });
        private void UpdateSource() => Safe(() =>
        {
            if (_source == null) { Bind(); return; }
            var snapshot = _source.Capture(); _snapshot = snapshot; Apply();
            Message("Обновлено из исходного заказа: " + snapshot.SourceName, false);
        });
        private void Apply(bool fit = false)
        {
            var snapshot = _snapshot;
            if (_swapped && snapshot != null) snapshot = new ApparelOrderSnapshot { Front = snapshot.Back, Back = snapshot.Front, SourceName = snapshot.SourceName };
            _front.Source = snapshot?.Front.Image; _back.Source = snapshot?.Back.Image;
            _scene.Set(snapshot, _folds?.Value ?? .5, fit);
        }
        private void Export() => Safe(() =>
        {
            var dialog = new SaveFileDialog { Title = "Сохранить 3D-вид", Filter = "PNG|*.png", DefaultExt = ".png", AddExtension = true,
                FileName = "3D-примерка-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png", OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return;
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(_scene.RenderPng()));
            using (var stream = File.Create(dialog.FileName)) encoder.Save(stream);
            Message("3D-вид сохранён: " + dialog.FileName, false);
        });
        private void Safe(Action action) { try { action(); } catch (Exception error) { Log.Error("Apparel 3D failed.", error); Message(error.GetBaseException().Message, true); } }
        private void Message(string message, bool error)
        { _message.Text = message; DockerTheme.UseTheme(_message, TextBlock.ForegroundProperty, error ? DockerTheme.ErrorTextBrush : DockerTheme.TextBrush); _status?.Invoke(message, error); }
        private static Button Button(string title, Action action)
        { var button = new Button { Content = title, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 6, 6), MinHeight = 32 }; button.Click += (_, __) => action(); return button; }
        private static TextBlock Note(string message, bool bold = false) => new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap,
            FontSize = 12, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 0, 8) };
    }
}
