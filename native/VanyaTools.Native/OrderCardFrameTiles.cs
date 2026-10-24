using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed partial class OrderCardTab
    {
        private Window _frameGalleryWindow;
        private UniformGrid _frameGalleryTiles;

        private void RenderFrames()
        {
            int count = _data.Frames.Count;
            _frameExpander.Header = "Кадры · " + count;
            _frameExpander.ToolTip = "Нажмите кадр для просмотра. ↻ переснимает его из текущего выделения, × удаляет; остальные действия находятся в меню ⋯.";
            _frames.Children.Clear();
            if (count == 0)
            {
                var empty = Note("Добавьте выделение или подтяните фото заказа.");
                empty.FontSize = 11; empty.Margin = new Thickness(2, 2, 2, 3);
                _frames.Children.Add(empty);
            }
            else
            {
                int visible = Math.Min(4, count);
                var tiles = new UniformGrid { Columns = visible == 4 ? 2 : visible,
                    Margin = new Thickness(-2, 0, -2, 0) };
                double height = visible == 1 ? 106 : visible == 2 ? 78 : visible == 3 ? 68 : count > 4 ? 42 : 56;
                for (int index = 0; index < visible; index++) tiles.Children.Add(CreateFrameTile(index, height));
                _frames.Children.Add(tiles);
                if (count > 4)
                {
                    var all = Button("Все кадры (" + count + ")…", (_, __) => ShowAllFrameTiles());
                    all.Margin = new Thickness(1, 2, 1, 0); all.MinHeight = 23;
                    _frames.Children.Add(all);
                }
            }
            RefreshFrameGallery();
        }

        private FrameworkElement CreateFrameTile(int index, double height)
        {
            OrderCardFrame frame = _data.Frames[index];
            object document = _documentIdentity;
            var tile = new Grid { Height = height, Margin = new Thickness(2), MinWidth = 0 };
            var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(2), SnapsToDevicePixels = true };
            var imageArea = new Border { Child = image, Background = new SolidColorBrush(Color.FromRgb(235, 239, 242)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(184, 198, 207)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand };
            int objects = frame.ObjectCount > 0 ? frame.ObjectCount : frame.Shapes?.Count ?? 0;
            string description = "Кадр " + (index + 1) + (objects > 0 ? " · объектов: " + objects :
                String.IsNullOrWhiteSpace(frame.ServerPhotoKey) ? " · растровый снимок" : " · фото заказа");
            imageArea.ToolTip = description + ". Нажмите для просмотра.";
            AutomationProperties.SetName(imageArea, description);
            try
            {
                if (!String.IsNullOrEmpty(frame.Thumbnail))
                    image.Source = DecodePhoto(Convert.FromBase64String(frame.Thumbnail), 512);
            }
            catch (Exception error) { Log.Error("Order frame tile thumbnail unavailable.", error); }
            if (image.Source == null)
                imageArea.Child = new TextBlock { Text = "Кадр " + (index + 1), FontSize = 11,
                    Foreground = Brushes.SlateGray, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            imageArea.MouseLeftButtonUp += (_, e) =>
            { e.Handled = true; RunFrameTileAction(index, frame, document, () => PreviewFrame(index)); };
            tile.Children.Add(imageArea);

            var badge = new Border { Background = new SolidColorBrush(Color.FromArgb(225, 248, 250, 251)),
                CornerRadius = new CornerRadius(2), Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Child = new TextBlock { Text = (index + 1).ToString(), FontSize = 11, Foreground = Brushes.DarkSlateGray } };
            tile.Children.Add(badge);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2) };
            actions.Children.Add(FrameOverlayButton("↻", "Переснять кадр из текущего выделения", (_, __) =>
                RunFrameTileAction(index, frame, document, () => RecaptureFrame(index))));
            actions.Children.Add(FrameOverlayButton("×", "Удалить кадр из карточки", (_, __) =>
                RunFrameTileAction(index, frame, document, () => RemoveFrame(index))));
            var more = FrameOverlayButton("⋯", "Другие действия с кадром", null);
            var menu = new ContextMenu();
            AddFrameTileMenuItem(menu, "Открыть кадр", index, frame, document, () => PreviewFrame(index));
            AddFrameTileMenuItem(menu, "Все кадры…", index, frame, document, ShowAllFrameTiles);
            var select = AddFrameTileMenuItem(menu, "Показать объекты на холсте", index, frame, document, () => SelectFrame(index));
            select.IsEnabled = (frame.Shapes?.Count ?? 0) > 0;
            if (!select.IsEnabled) { select.ToolTip = "Фото заказа не связано с объектами холста."; ToolTipService.SetShowOnDisabled(select, true); }
            menu.Items.Add(new Separator());
            var earlier = AddFrameTileMenuItem(menu, "Переместить раньше", index, frame, document, () => MoveFrame(index, -1));
            var later = AddFrameTileMenuItem(menu, "Переместить позже", index, frame, document, () => MoveFrame(index, 1));
            earlier.IsEnabled = index > 0; later.IsEnabled = index + 1 < _data.Frames.Count;
            more.Click += (_, __) => { if (_busy) return; menu.PlacementTarget = more; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
            tile.ContextMenu = menu; actions.Children.Add(more); tile.Children.Add(actions);
            return tile;
        }

        private static Button FrameOverlayButton(string glyph, string caption, RoutedEventHandler clicked)
        {
            var button = new Button { Content = glyph, Width = 22, Height = 22, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(0), Margin = new Thickness(1, 0, 0, 0), FontSize = 12,
                ToolTip = caption, Focusable = true };
            AutomationProperties.SetName(button, caption);
            if (clicked != null) button.Click += clicked;
            return button;
        }

        private MenuItem AddFrameTileMenuItem(ContextMenu menu, string caption, int index,
            OrderCardFrame frame, object document, Action action)
        {
            var item = new MenuItem { Header = caption };
            item.Click += (_, __) => RunFrameTileAction(index, frame, document, action);
            menu.Items.Add(item); return item;
        }

        private void RunFrameTileAction(int index, OrderCardFrame frame, object document, Action action)
        {
            if (_busy) return;
            Safe(() =>
            {
                // A gallery is modeless. Refresh the current CDR before using
                // its captured frame index; a different card must not be edited.
                EnsureDocument(true, true);
                if (!SameDocument(document, _documentIdentity) || index < 0 || index >= _data.Frames.Count ||
                    !ReferenceEquals(_data.Frames[index], frame))
                    throw new InvalidOperationException("Карточка или порядок кадров изменились. Выберите кадр повторно.");
                action();
            });
        }

        private void ShowAllFrameTiles()
        {
            if (_busy) return;
            if (_frameGalleryWindow != null) { _frameGalleryWindow.Activate(); return; }
            _frameGalleryTiles = new UniformGrid { Columns = 3, Margin = new Thickness(6) };
            var root = new Grid(); DockerTheme.Apply(root);
            root.Children.Add(new ScrollViewer { Content = _frameGalleryTiles,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            _frameGalleryWindow = new Window { Title = "Кадры заказа", Width = 720, Height = 610,
                MinWidth = 380, MinHeight = 280, Content = root, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            Window owner = Window.GetWindow(this);
            if (owner != null) { _frameGalleryWindow.Owner = owner; _frameGalleryWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            _frameGalleryWindow.Closed += (_, __) => { _frameGalleryTiles = null; _frameGalleryWindow = null; };
            RefreshFrameGallery(); _frameGalleryWindow.Show();
        }

        private void RefreshFrameGallery()
        {
            if (_frameGalleryTiles == null) return;
            _frameGalleryWindow.Title = "Кадры · " + _data.Frames.Count + (String.IsNullOrWhiteSpace(_designName.Text) ? "" : " · " + _designName.Text);
            _frameGalleryTiles.Children.Clear();
            _frameGalleryTiles.Columns = _data.Frames.Count == 4 ? 2 : Math.Min(3, Math.Max(1, _data.Frames.Count));
            for (int index = 0; index < _data.Frames.Count; index++)
                _frameGalleryTiles.Children.Add(CreateFrameTile(index, 138));
            if (_data.Frames.Count == 0) _frameGalleryTiles.Children.Add(Note("Кадров нет."));
        }
    }
}
