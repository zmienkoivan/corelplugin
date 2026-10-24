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
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal enum SimpleAiMode { Raster, Prompt, AiStyles, LocalStyles }

    internal sealed partial class SimpleAiTab : UserControl
    {
        private readonly Func<string> _capture;
        private readonly Func<string> _token;
        private readonly Func<string, string, string> _import;
        private readonly Func<string, string, string> _importDtf;
        private readonly Action<string, bool> _status;
        private readonly SimpleAiMode _mode;
        private readonly ComboBox _model;
        private readonly ComboBox _backgroundModel;
        private readonly ComboBox _localPreset;
        private readonly Slider _localStrength, _localContrast, _localWarmth,
            _localSaturation, _localBrightness;
        private readonly Button _localApplyButton, _localPreviewButton;
        private readonly TextBox _prompt;
        private readonly TextBox _backgroundTolerance, _whiteCutPixels, _backgroundHex;
        private readonly Border _backgroundSwatch;
        private readonly TextBox _alphaThreshold;
        private readonly ComboBox _highlightColor;
        private readonly TextBox _edgeRadius, _edgeExpansion, _underlayHex;
        private readonly ComboBox _edgeLengthUnit;
        private readonly Button _thinOutline, _thickOutline;
        private readonly DtfOutlineSettings _outlineSettings;
        private bool _convertingEdgeUnits, _edgeDpiKnown;
        private int _previousEdgeUnit;
        private double _edgeDpi = 400;
        private readonly Border _underlaySwatch;
        private readonly TextBlock _edgeUnits;
        private readonly Image _sourcePreview, _resultPreview;
        private readonly TextBlock _progress;
        private readonly ProgressBar _progressBar;
        private readonly Button _removeButton, _removeWhiteButton, _backgroundPipetteButton, _upscaleButton,
            _alphaButton, _highlightButton, _smoothButton, _stairButton, _pipetteButton, _underlayColorButton,
            _editButton, _cancelButton, _retryButton;
        private readonly List<RadioButton> _styleButtons = new List<RadioButton>();
        private readonly List<RadioButton> _localStyleButtons = new List<RadioButton>();
        private readonly List<Image> _localStyleImages = new List<Image>();
        private readonly string _radioGroup = Guid.NewGuid().ToString("N");
        private BitmapSource _localStyleSample;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _localPreviewTimer = new DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(70) };
        private readonly Stopwatch _elapsed = new Stopwatch();
        private CancellationTokenSource _cancellation;
        private bool _busy;
        private string _stage, _lastModel, _lastOutput, _lastSource, _readyOutput;
        private string _importWarning;
        private string _localSourcePath;
        private BitmapSource _localPreviewSource;
        private Dictionary<string, object> _lastInput;
        private StyleChoice _selectedStyle;

        private const string TiledUpscaler = "xinntao/realesrgan:1b976a4d456ed9e4d1a846597b7614e79eadad3032e9124fa63859db0fd59b56";
        private const string BiRefNet = "sprited/birefnet:d6ec17201fa9a19b3c6c5b1250c11f8e0ade3e1f97874b9f4cf3ac6826447384";

        private sealed class ModelChoice
        {
            public readonly string Id, Name, Variant;
            public readonly decimal EstimatedCost;
            public ModelChoice(string id, string name, decimal estimatedCost, string variant = null)
            { Id = id; Name = name; EstimatedCost = estimatedCost; Variant = variant; }
            public override string ToString() { return Name; }
        }

        private sealed class StyleChoice
        {
            public readonly string Name, Asset, Prompt;
            public StyleChoice(string name, string asset, string prompt)
            { Name = name; Asset = asset; Prompt = prompt; }
        }

        public SimpleAiTab(Func<string> capture, Func<string> token,
            Func<string, string, string> import, Action<string, bool> status, SimpleAiMode mode,
            Func<string, string, string> importDtf = null, DtfOutlineSettings outlineSettings = null,
            FrameworkElement printButtons = null)
        {
            _capture = capture; _token = token; _import = import;
            _importDtf = importDtf ?? import; _status = status;
            _outlineSettings = outlineSettings ?? DtfOutlineSettings.Load();
            _mode = mode;
            bool styleOnly = mode != SimpleAiMode.Raster;
            bool localStyles = mode == SimpleAiMode.LocalStyles;
            bool aiStyles = mode == SimpleAiMode.AiStyles;
            bool aiEdit = mode == SimpleAiMode.Prompt || aiStyles;
            var panel = new StackPanel { Margin = styleOnly ? new Thickness(5) : new Thickness(0) };
            Content = panel;

            if (styleOnly) panel.Children.Add(Label(localStyles ? "Стили фото · без интернета" :
                aiStyles ? "Готовые ИИ-стили" : "Обработка фото по промпту", true));
            if (styleOnly) DockerHelp.Add(panel, "Выделите объект или группу.");
            if (mode == SimpleAiMode.Prompt) DockerHelp.Add(panel,
                "Напишите короткую команду: «убери бутылку», «замени фон на лес», «сделай студийный свет». " +
                "Модель получает выделенное фото целиком. Для точной области правки используйте вкладку «Заливка». " +
                "Результат вставляется рядом с исходником. Модель и стоимость показываются перед платным запросом.");
            _removeButton = Button("Удалить фон · AI", async (_, __) => await Run("background"));
            _backgroundModel = new ComboBox { FontSize = 11, MinHeight = 27, Margin = new Thickness(1, 1, 1, 0) };
            _backgroundModel.Items.Add(new ModelChoice(BiRefNet, "BiRefNet · фото 2K", 0m, "general-hr"));
            _backgroundModel.Items.Add(new ModelChoice(BiRefNet, "BiRefNet · портрет", 0m, "portrait"));
            _backgroundModel.Items.Add(new ModelChoice("bria/remove-background", "Bria · прежняя модель", 0.018m));
            _backgroundModel.SelectedIndex = 0;
            _backgroundModel.ToolTip = "Модель удаления фона. Строит маску прозрачности; настоящие отверстия в объекте автоматически не закрашиваются.";
            System.Windows.Automation.AutomationProperties.SetName(_backgroundModel, "Модель удаления фона");
            _upscaleButton = Button("Апскейл ×2", async (_, __) => await Run("upscale"));
            var whiteRow = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            whiteRow.ColumnDefinitions.Add(new ColumnDefinition());
            for (int column = 0; column < 3; column++)
                whiteRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _removeWhiteButton = Button("Удалить фон", async (_, __) => await RemoveWhiteBackground());
            whiteRow.Children.Add(_removeWhiteButton);
            _backgroundHex = new TextBox { Text = "#FFFFFF", Width = 72, FontSize = 11,
                Margin = new Thickness(3, 0, 2, 0), VerticalContentAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_backgroundHex, 1);
            whiteRow.Children.Add(_backgroundHex);
            _backgroundSwatch = new Border { Width = 18, Height = 18, Background = Brushes.White,
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(2) };
            Grid.SetColumn(_backgroundSwatch, 2); whiteRow.Children.Add(_backgroundSwatch);
            _backgroundPipetteButton = Button("Пипетка", (_, __) => PickBackgroundFromCanvas());
            _backgroundPipetteButton.ToolTip = "Выберите цвет на холсте CorelDRAW. Esc — отмена.";
            Grid.SetColumn(_backgroundPipetteButton, 3); whiteRow.Children.Add(_backgroundPipetteButton);
            _backgroundHex.TextChanged += (_, __) => UpdateBackgroundSwatch();
            _backgroundTolerance = new TextBox { Text = "10", FontSize = 11,
                VerticalContentAlignment = VerticalAlignment.Center };
            _whiteCutPixels = new TextBox { Text = "0", FontSize = 11,
                VerticalContentAlignment = VerticalAlignment.Center };
            _whiteCutPixels.ToolTip = "Сдвинуть край прозрачности внутрь на указанное число пикселей.";
            var backgroundFields = new UniformGrid { Columns = 2 };
            var toleranceRow = DockerLayout.FieldRow("Допуск", _backgroundTolerance, 50);
            toleranceRow.Margin = new Thickness(1, 0, 5, 3);
            backgroundFields.Children.Add(toleranceRow);
            var cutRow = DockerLayout.FieldRow("Срез, px", _whiteCutPixels, 50);
            cutRow.Margin = new Thickness(5, 0, 1, 3);
            backgroundFields.Children.Add(cutRow);
            _backgroundHex.ToolTip = "Цвет удаляемого фона · HEX";
            _backgroundTolerance.ToolTip = "Допуск близости к цвету фона";
            _backgroundTolerance.MinHeight = _whiteCutPixels.MinHeight = 27;
            if (!styleOnly)
            {
                var backgroundTools = DockerLayout.ToolGroup("Фон и размер",
                    "Выделите объект или группу. Цвет удаляемого фона можно ввести в HEX или взять пипеткой с холста. " +
                    "Допуск задаёт близость к выбранному цвету; подрезка сдвигает край прозрачности внутрь в пикселях.");
                backgroundTools.Children.Add(whiteRow);
                backgroundTools.Children.Add(backgroundFields);
                var aiRow = new UniformGrid { Columns = 2 };
                aiRow.Children.Add(_removeButton);
                aiRow.Children.Add(_upscaleButton);
                backgroundTools.Children.Add(aiRow);
                backgroundTools.Children.Add(_backgroundModel);
                panel.Children.Add(backgroundTools);
                if (printButtons != null) panel.Children.Add(printButtons);
            }
            var correctionTools = DockerLayout.ToolGroup("Коррекция");
            _correctionButton = DockerTheme.Primary(Button("Коррекция и обводка…", async (_, __) => await OpenCorrection()));
            _correctionButton.ToolTip = "Открыть выделение один раз и подбирать прозрачность, сглаживание и обводку с живым предпросмотром. Работает без интернета.";
            if (!styleOnly) panel.Children.Add(correctionTools);
            _alphaButton = Button("Убрать полупрозрачность", async (_, __) => await RemoveSemiTransparent());
            _alphaThreshold = new TextBox { Text = "130", Width = 50, MinHeight = 27, FontSize = 11,
                VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var alphaRow = DockerLayout.Trailing(_alphaButton,
                DockerLayout.FieldRow("Порог", _alphaThreshold, 50));
            alphaRow.Margin = new Thickness(0, 0, 0, 2);
            if (!styleOnly) correctionTools.Children.Add(alphaRow);

            _highlightButton = Button(styleOnly ? "Подсветить полупрозрачные" : "Подсветить", async (_, __) => await HighlightSemiTransparent());
            _highlightButton.ToolTip = "Создаёт рядом с оригиналом копию с яркой отметкой пикселей, у которых прозрачность между 0 и 100%.";
            _highlightColor = new ComboBox { Width = 88, MinHeight = 27, FontSize = 11,
                Margin = new Thickness(3, 1, 0, 1), VerticalAlignment = VerticalAlignment.Center };
            _highlightColor.Items.Add("Зелёный");
            _highlightColor.Items.Add("Красный");
            _highlightColor.SelectedIndex = 0;
            var highlightRow = DockerLayout.Trailing(_highlightButton, _highlightColor);
            highlightRow.Margin = new Thickness(0, 0, 0, 2);
            if (!styleOnly) correctionTools.Children.Add(highlightRow);

            _stairButton = Button("Сгладить лесенку", async (_, __) => await SmoothStairs());
            _stairButton.ToolTip = "Создаёт в 2–4 раза более плотную сетку пикселей и сглаживает край и цветовые границы рисунка. Прозрачность результата остаётся 0 или 100%.";
            if (!styleOnly) correctionTools.Children.Add(_stairButton);

            var edgeRow = DockerLayout.ToolGroup("Обводка", "Единицы мм/px относятся к сглаживанию и наружной обводке. " +
                "При переключении значения пересчитываются по DPI захвата. Под полями показан размер в других единицах; " +
                "подробный расчёт — в подсказке этой строки. Иконки тонкого и толстого контура заполняют только обводку; " +
                "значения меняются в «Настройки → Пресеты обводки ДТФ».");
            _smoothButton = Button(styleOnly ? "Обводка" : "Применить", async (_, __) => await SmoothEdge());
            _smoothButton.ToolTip = "Сгладить край и добавить наружную обводку с выбранными размером и цветом";
            _edgeLengthUnit = new ComboBox { Width = 53, FontSize = 11, MinHeight = 24,
                Margin = new Thickness(3, 1, 0, 1), ItemsSource = new[] { "мм", "px" }, SelectedIndex = 0 };
            _thinOutline = OutlinePreset(false, (_, __) => ApplyOutlinePreset(_outlineSettings.ThinMm));
            _thickOutline = OutlinePreset(true, (_, __) => ApplyOutlinePreset(_outlineSettings.ThickMm));
            var outlineActions = new StackPanel { Orientation = Orientation.Horizontal };
            outlineActions.Children.Add(_thinOutline); outlineActions.Children.Add(_thickOutline);
            outlineActions.Children.Add(_edgeLengthUnit);
            edgeRow.Children.Add(DockerLayout.Trailing(_smoothButton, outlineActions));
            _edgeRadius = new TextBox { Text = DtfOutlineUnits.Format(2 * 25.4 / 400), FontSize = 11,
                MinHeight = 27, VerticalContentAlignment = VerticalAlignment.Center };
            _edgeExpansion = new TextBox { Text = DtfOutlineUnits.Format(_outlineSettings.ThinMm), FontSize = 11,
                MinHeight = 27, VerticalContentAlignment = VerticalAlignment.Center };
            var outlineFields = new UniformGrid { Columns = 2, Margin = new Thickness(1, 1, 1, 4) };
            var radiusField = DockerLayout.LabeledField("Сглаживание", _edgeRadius);
            radiusField.Margin = new Thickness(0, 0, 5, 0);
            var expansionField = DockerLayout.LabeledField("Обводка +", _edgeExpansion);
            expansionField.Margin = new Thickness(5, 0, 0, 0);
            outlineFields.Children.Add(radiusField); outlineFields.Children.Add(expansionField);
            edgeRow.Children.Add(outlineFields);
            _underlayHex = new TextBox { Text = "#FFFFFF", Width = 72, FontSize = 11,
                Margin = new Thickness(3, 0, 2, 0), VerticalContentAlignment = VerticalAlignment.Center };
            _underlaySwatch = new Border { Width = 18, Height = 18, Background = Brushes.White,
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            _underlayColorButton = new Button { Content = _underlaySwatch, Width = 28, Height = 28,
                Padding = new Thickness(3), Margin = new Thickness(2, 0, 2, 0),
                ToolTip = "Выбрать цвет обводки в палитре" };
            System.Windows.Automation.AutomationProperties.SetName(_underlayColorButton, "Выбрать цвет обводки");
            _underlayColorButton.Click += (_, __) => ChooseUnderlayColor();
            _pipetteButton = Button("Пипетка", async (_, __) => await BeginColorPick());
            _pipetteButton.ToolTip = "Взять цвет обводки с экрана. Esc — отмена.";
            var colorFields = new StackPanel { Orientation = Orientation.Horizontal };
            colorFields.Children.Add(new TextBlock { Text = "Цвет", FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center });
            colorFields.Children.Add(_underlayHex); colorFields.Children.Add(_underlayColorButton);
            var outlineColorRow = new Grid();
            outlineColorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            outlineColorRow.ColumnDefinitions.Add(new ColumnDefinition());
            outlineColorRow.Children.Add(colorFields);
            Grid.SetColumn(_pipetteButton, 1); outlineColorRow.Children.Add(_pipetteButton);
            edgeRow.Children.Add(outlineColorRow);
            _underlayHex.TextChanged += (_, __) => UpdateUnderlaySwatch();
            if (!styleOnly) panel.Children.Add(edgeRow);
            _edgeUnits = Note("");
            if (!styleOnly) edgeRow.Children.Add(_edgeUnits);
            RefreshOutlinePresets();
            if (!styleOnly)
            {
                _outlineSettings.Changed += RefreshOutlinePresets;
                Loaded += (_, __) => RefreshOutlinePresets();
            }
            _edgeLengthUnit.SelectionChanged += (_, __) => SwitchEdgeUnits();
            _edgeRadius.TextChanged += (_, __) => UpdateEdgeUnits(false);
            _edgeExpansion.TextChanged += (_, __) => UpdateEdgeUnits(false);
            edgeRow.MouseEnter += (_, __) => UpdateEdgeUnits();
            if (!styleOnly) { UpdateEdgeUnits(false); panel.Children.Add(_correctionButton); }

            var localPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
            _localPreviewButton = Button("Загрузить выделение для предпросмотра",
                async (_, __) => await LoadLocalPreview());
            _localPreviewButton.ToolTip = "Один раз подготавливает выделение. Затем пресеты и ползунки меняют предпросмотр сразу, без повторной работы CorelDRAW.";
            localPanel.Children.Add(_localPreviewButton);
            _localPreset = new ComboBox { FontSize = 11, Margin = new Thickness(1, 2, 1, 3) };
            _localPreset.Items.Add("Чёрно-белый");
            _localPreset.Items.Add("Сепия");
            _localPreset.Items.Add("Благородное золото");
            _localPreset.Items.Add("Тёплый матовый");
            _localPreset.Items.Add("Винтаж · серо-коричневый");
            _localPreset.Items.Add("Свой тон");
            _localPreset.SelectedIndex = 4;
            if (localStyles)
            {
                var localCards = DockerLayout.Cards(130);
                localCards.Margin = new Thickness(0, 2, 0, 4);
                for (int index = 0; index < _localPreset.Items.Count; index++)
                    localCards.Children.Add(LocalStyleCard(index, Convert.ToString(_localPreset.Items[index])));
                localPanel.Children.Add(localCards);
                DockerHelp.Add(localPanel, "Миниатюры показывают действие каждого эффекта на одном образце. " +
                    "После загрузки выделения все миниатюры строятся по вашему фото. " +
                    "Выберите эффект нажатием на картинку; сила и тонкая настройка меняют предпросмотр без платных запросов.");
            }
            _localStrength = AddLocalStyleSlider(localPanel, "Сила эффекта", 0, 100, 100, "%");
            var localFineTuning = new StackPanel { Margin = new Thickness(3, 2, 3, 3) };
            _localContrast = AddLocalStyleSlider(localFineTuning, "Контраст", -50, 50, 0, "");
            _localWarmth = AddLocalStyleSlider(localFineTuning, "Тепло", -50, 50, 0, "");
            _localSaturation = AddLocalStyleSlider(localFineTuning, "Насыщенность", 0, 150, 100, "%");
            _localBrightness = AddLocalStyleSlider(localFineTuning, "Светлота", -50, 50, 0, "");
            localPanel.Children.Add(new Expander { Header = "Тонкая настройка", Content = localFineTuning,
                IsExpanded = false, FontSize = 10, Margin = new Thickness(1, 1, 1, 3) });
            _localApplyButton = DockerTheme.Primary(Button("Применить локальный стиль",
                async (_, __) => await ApplyLocalStyle()));
            _localApplyButton.ToolTip = "Обрабатывает выделение на этом ПК без Replicate. Оригинал остаётся на холсте.";
            localPanel.Children.Add(_localApplyButton);
            if (localStyles)
            {
                panel.Children.Add(localPanel);
            }

            var previews = new UniformGrid { Columns = 2 };
            previews.Children.Add(Preview(styleOnly ? "Выделение" : "До", out _sourcePreview, styleOnly ? 220 : 72));
            previews.Children.Add(Preview(styleOnly ? "Результат" : "После", out _resultPreview, styleOnly ? 220 : 72));
            if (!styleOnly)
            {
                var previewStyle = new Style(typeof(UniformGrid));
                var empty = new MultiDataTrigger();
                empty.Conditions.Add(new Condition(new Binding("Source") { Source = _sourcePreview }, null));
                empty.Conditions.Add(new Condition(new Binding("Source") { Source = _resultPreview }, null));
                empty.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
                previewStyle.Triggers.Add(empty); previews.Style = previewStyle;
            }
            panel.Children.Add(previews);
            if (localStyles)
            {
                _localPreviewTimer.Tick += (_, __) =>
                {
                    _localPreviewTimer.Stop();
                    UpdateLocalPreview();
                };
                _localPreset.SelectionChanged += (_, __) => {
                    for (int i = 0; i < _localStyleButtons.Count; i++)
                        _localStyleButtons[i].IsChecked = i == _localPreset.SelectedIndex;
                    QueueLocalPreview(); };
                foreach (Slider slider in new[] { _localStrength, _localContrast, _localWarmth,
                    _localSaturation, _localBrightness })
                    slider.ValueChanged += (_, __) => QueueLocalPreview();
                _localStyleButtons[_localPreset.SelectedIndex].IsChecked = true;
                UpdateLocalStyleCards();
            }

            var styles = DockerLayout.Cards(120);
            if (aiStyles) foreach (var style in new[]
            {
                new StyleChoice("Аниме", "anime", "Transform the selected image into polished Japanese anime illustration with clean expressive linework and cel shading."),
                new StyleChoice("Наивная иллюстрация", "naive", "Transform the selected image into a playful handmade naive illustration with simple shapes, uneven ink outlines and flat warm colors."),
                new StyleChoice("Пиксар-персонаж", "animated3d", "Transform the selected subject into a friendly, expressive 3D animated feature-film character with rounded forms and soft studio lighting."),
                new StyleChoice("Акварель", "watercolor", "Transform the selected image into a delicate hand-painted watercolor illustration with soft pigment washes and natural paper texture."),
                new StyleChoice("Комикс", "comic", "Transform the selected image into modern comic-book art with bold ink outlines, halftone shadows and vivid flat color blocks."),
                new StyleChoice("Городская иллюстрация", "street-illustration", "Render the selected subject as a playful hand-painted editorial street illustration: irregular thick black ink outlines, flat warm peach and cream areas, vivid cobalt blue and orange accents, simplified expressive figures and subtle painted-paper texture. Preserve the source subject and composition.")
            }) styles.Children.Add(StyleCard(style));
            if (aiStyles)
            {
                panel.Children.Add(styles);
            }
            if (aiEdit) panel.Children.Add(Label("Модель", false));
            _model = new ComboBox { FontSize = 11, Margin = new Thickness(0, 0, 0, 5) };
            _model.Items.Add(new ModelChoice("google/nano-banana-2", "Nano Banana 2 · 2K", 0.101m));
            _model.Items.Add(new ModelChoice("google/nano-banana-pro", "Nano Banana Pro · 2K", 0.15m));
            _model.Items.Add(new ModelChoice("bytedance/seedream-4.5", "Seedream 4.5 · 2K", 0.04m));
            _model.Items.Add(new ModelChoice("black-forest-labs/flux-2-pro", "FLUX.2 Pro", 0.14m));
            _model.Items.Add(new ModelChoice("black-forest-labs/flux-kontext-max", "FLUX.1 Kontext Max", 0.08m));
            _model.Items.Add(new ModelChoice("qwen/qwen-image-edit", "Qwen Image Edit", 0.03m));
            _model.SelectedIndex = 0;
            if (aiEdit)
            {
                panel.Children.Add(_model);
                var modelCost = Note(""); panel.Children.Add(modelCost);
                Action updateCost = () => {
                    var choice = _model.SelectedItem as ModelChoice;
                    modelCost.Text = choice == null ? "" : "Ориентировочно $" +
                        choice.EstimatedCost.ToString("0.###", CultureInfo.InvariantCulture) + " за результат · Replicate"; };
                _model.SelectionChanged += (_, __) => updateCost(); updateCost();
                panel.Children.Add(Label(aiStyles ? "Промпт выбранного стиля · можно уточнить" : "Что изменить на фото", false));
            }
            _prompt = new TextBox { MinHeight = 55, MaxHeight = 78, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 11 };
            if (aiEdit)
            {
                panel.Children.Add(_prompt);
            }
            _editButton = DockerTheme.Primary(Button(aiStyles ? "Применить ИИ-стиль" : "Обработать фото по промпту",
                async (_, __) => await Run("edit")));
            if (aiEdit) panel.Children.Add(_editButton);
            if (aiStyles && _styleButtons.Count > 0) _styleButtons[0].IsChecked = true;

            _cancelButton = Button("Отменить", (_, __) => Cancel());
            _cancelButton.Visibility = Visibility.Collapsed;
            panel.Children.Add(_cancelButton);
            _retryButton = Button("Повторить", async (_, __) => await Retry());
            _retryButton.Visibility = Visibility.Collapsed;
            panel.Children.Add(_retryButton);
            _progress = Note("");
            if (!styleOnly)
            {
                var progressStyle = new Style(typeof(TextBlock));
                var empty = new DataTrigger { Binding = new Binding("Text") { Source = _progress }, Value = "" };
                empty.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
                progressStyle.Triggers.Add(empty); _progress.Style = progressStyle;
            }
            _progressBar = new ProgressBar { Height = 7, IsIndeterminate = true,
                Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 5) };
            panel.Children.Add(_progress); panel.Children.Add(_progressBar);
            if (!styleOnly)
            {
                foreach (var button in new[] { _removeWhiteButton, _backgroundPipetteButton, _removeButton,
                    _upscaleButton, _alphaButton, _highlightButton, _stairButton, _smoothButton, _pipetteButton })
                { button.MinHeight = 27; button.FontSize = 11; }
                _backgroundHex.MinHeight = _underlayHex.MinHeight = 27;
                _alphaThreshold.ToolTip = "Порог 0–255 · используется также для сглаживания края и обводки";
                _edgeRadius.ToolTip = "Радиус сглаживания в выбранных единицах мм/px";
                _edgeExpansion.ToolTip = "Наружная обводка в выбранных единицах мм/px";
                _underlayHex.ToolTip = "Цвет обводки · HEX";
            }
            _timer.Tick += (_, __) => _progress.Text = _stage + " · " + (int)_elapsed.Elapsed.TotalSeconds + " с";
            RestorePending();
            if (localStyles)
            {
                Unloaded += (_, __) => _localPreviewTimer.Stop();
                Loaded += (_, __) => QueueLocalPreview();
            }
        }

        private Border StyleCard(StyleChoice style)
        {
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new Image { Source = Thumbnail(style.Asset), Width = 72, Height = 72,
                Stretch = Stretch.UniformToFill, Margin = new Thickness(1) });
            content.Children.Add(new TextBlock { Text = style.Name, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, FontSize = 10, MaxWidth = 100 });
            var choice = new RadioButton { GroupName = "VanyaSimpleAiStyle" + _radioGroup, Content = content,
                HorizontalAlignment = HorizontalAlignment.Center };
            _styleButtons.Add(choice);
            var border = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1),
                Margin = new Thickness(1), Padding = new Thickness(2), Child = choice };
            choice.Checked += (_, __) => { _selectedStyle = style;
                _prompt.Text = style.Prompt +
                " Preserve the main subject, pose and composition of the input image. Do not invent text.";
                border.BorderBrush = Brushes.SteelBlue; border.BorderThickness = new Thickness(2); };
            choice.Unchecked += (_, __) => { border.BorderBrush = Brushes.LightGray;
                border.BorderThickness = new Thickness(1); };
            return border;
        }

        private Border LocalStyleCard(int index, string name)
        {
            var image = new Image { Width = 92, Height = 64, Stretch = Stretch.UniformToFill };
            _localStyleImages.Add(image);
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(image);
            content.Children.Add(new TextBlock { Text = name, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, FontSize = 10, MaxWidth = 105, Margin = new Thickness(0, 2, 0, 0) });
            var choice = new RadioButton { Content = content, GroupName = "VanyaLocalPhotoStyle" + _radioGroup,
                HorizontalAlignment = HorizontalAlignment.Center };
            _localStyleButtons.Add(choice);
            var border = new Border { Child = choice, BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(1), Margin = new Thickness(1), Padding = new Thickness(2) };
            choice.Checked += (_, __) => { _localPreset.SelectedIndex = index;
                border.BorderBrush = Brushes.SteelBlue; border.BorderThickness = new Thickness(2); };
            choice.Unchecked += (_, __) => { border.BorderBrush = Brushes.LightGray;
                border.BorderThickness = new Thickness(1); };
            return border;
        }

        private void UpdateLocalStyleCards()
        {
            if (_localStyleImages.Count == 0) return;
            BitmapSource source = _localPreviewSource ?? (_localStyleSample ??
                (_localStyleSample = LocalPhotoStyleSample.Create()));
            double scale = Math.Min(1, 128.0 / Math.Max(source.PixelWidth, source.PixelHeight));
            if (scale < 1)
            {
                var small = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                small.Freeze(); source = small;
            }
            for (int index = 0; index < _localStyleImages.Count; index++)
            {
                LocalPhotoStyleSettings settings = LocalSettings();
                settings.Preset = (LocalPhotoPreset)index;
                _localStyleImages[index].Source = LocalPhotoStyleProcessor.Render(source, settings, CancellationToken.None);
            }
        }

        private static BitmapSource Thumbnail(string asset)
        {
            string resource = "VanyaTools.Native.Assets.styles." + asset + ".png";
            using (Stream stream = typeof(SimpleAiTab).Assembly.GetManifestResourceStream(resource))
            {
                if (stream == null) throw new InvalidDataException("Не найдена миниатюра стиля: " + asset);
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                image.Freeze();
                return image;
            }
        }

        private static Slider AddLocalStyleSlider(Panel panel, string title,
            double minimum, double maximum, double initial, string suffix)
        {
            var header = new DockPanel { Margin = new Thickness(1, 1, 1, 0) };
            var current = new TextBlock { Width = 42, TextAlignment = TextAlignment.Right,
                FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(current, Dock.Right);
            header.Children.Add(current);
            header.Children.Add(new TextBlock { Text = title, FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(header);
            var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = initial,
                TickFrequency = 1, Margin = new Thickness(3, 0, 3, 2) };
            current.Text = Math.Round(initial).ToString(CultureInfo.CurrentCulture) + suffix;
            slider.ValueChanged += (_, __) => current.Text =
                Math.Round(slider.Value).ToString(CultureInfo.CurrentCulture) + suffix;
            panel.Children.Add(slider);
            return slider;
        }

        private LocalPhotoStyleSettings LocalSettings()
        {
            return new LocalPhotoStyleSettings
            {
                Preset = (LocalPhotoPreset)_localPreset.SelectedIndex,
                Strength = (int)Math.Round(_localStrength.Value),
                Contrast = (int)Math.Round(_localContrast.Value),
                Warmth = (int)Math.Round(_localWarmth.Value),
                Saturation = (int)Math.Round(_localSaturation.Value),
                Brightness = (int)Math.Round(_localBrightness.Value)
            };
        }

        private void QueueLocalPreview()
        {
            if (_mode != SimpleAiMode.LocalStyles || _busy ||
                _localPreset.SelectedIndex < 0) return;
            // Throttle rather than debounce: the image must keep changing while dragging.
            if (!_localPreviewTimer.IsEnabled) _localPreviewTimer.Start();
        }

        private void UpdateLocalPreview()
        {
            if (_busy || _localPreset.SelectedIndex < 0) return;
            try
            {
                if (_localPreviewSource != null)
                    _resultPreview.Source = LocalPhotoStyleProcessor.Render(_localPreviewSource,
                        LocalSettings(), CancellationToken.None);
                UpdateLocalStyleCards();
            }
            catch (Exception ex)
            {
                Log.Error("Local photo style preview failed.", ex);
                Report("Не удалось обновить предпросмотр: " + ex.GetBaseException().Message, true);
            }
        }

        private async Task LoadLocalPreview()
        {
            if (_busy) return;
            _localPreviewTimer.Stop();
            SetBusy(true, "Подготавливаю выделение для предпросмотра…");
            string captured = null;
            try
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                captured = AiSelectionCapture.Capture();
                BitmapSource preview = PreviewBitmap(captured);
                string previous = _localSourcePath;
                _localSourcePath = captured;
                captured = null;
                _localPreviewSource = preview;
                _sourcePreview.Source = preview;
                _resultPreview.Source = LocalPhotoStyleProcessor.Render(preview,
                    LocalSettings(), CancellationToken.None);
                UpdateLocalStyleCards();
                _localPreviewButton.Content = "Обновить выделение для предпросмотра";
                if (!String.IsNullOrEmpty(previous))
                    try { File.Delete(previous); } catch { }
                Report("Предпросмотр готов. Пресеты и ползунки обновляют его сразу.", false);
            }
            catch (Exception ex)
            {
                Log.Error("Local photo style preview capture failed.", ex);
                Report(ex.GetBaseException().Message, true);
            }
            finally
            {
                if (!String.IsNullOrEmpty(captured))
                    try { File.Delete(captured); } catch { }
                SetBusy(false, null);
            }
        }

        private async Task ApplyLocalStyle()
        {
            if (_busy) return;
            if (!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput))
            { Report("Сначала вставьте готовый результат кнопкой «Повторить вставку».", true); return; }
            if (_localPreset.SelectedIndex < 0)
            { Report("Выберите локальный стиль.", true); return; }
            var settings = LocalSettings();
            string presetName = Convert.ToString(_localPreset.SelectedItem, CultureInfo.CurrentCulture);
            SetBusy(true, "Подготавливаю фото…");
            try
            {
                _importWarning = null;
                _lastModel = _lastOutput = _lastSource = null;
                _lastInput = null;
                string source = _localSourcePath;
                if (String.IsNullOrEmpty(source) || !File.Exists(source))
                {
                    source = AiSelectionCapture.Capture();
                    _localSourcePath = source;
                    _localPreviewSource = PreviewBitmap(source);
                    _sourcePreview.Source = _localPreviewSource;
                    _localPreviewButton.Content = "Обновить выделение для предпросмотра";
                    UpdateLocalStyleCards();
                }
                string output = Path.Combine(Path.GetTempPath(),
                    "Vanya-Local-Style-" + Guid.NewGuid().ToString("N") + ".png");
                CancellationToken cancellation = _cancellation.Token;
                SetStage("Обрабатываю фото локально…");
                await Task.Run(() => LocalPhotoStyleProcessor.Apply(source, output,
                    settings, cancellation), cancellation);
                cancellation.ThrowIfCancellationRequested();
                _readyOutput = output;
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
                File.WriteAllText(PendingPath(), output);
                _resultPreview.Source = PreviewBitmap(output);
                SetStage("Вставляю результат в Corel…");
                InsertReady();
                string warning = _importWarning;
                Report(String.IsNullOrEmpty(warning)
                    ? "Готово: " + presetName + ". Обработанная копия вставлена рядом."
                    : warning, !String.IsNullOrEmpty(warning));
                _importWarning = null;
            }
            catch (OperationCanceledException) { Report("Операция отменена.", false); }
            catch (Exception ex) { Log.Error("Local photo style failed.", ex);
                Report(ex.GetBaseException().Message, true); }
            finally { SetBusy(false, null); }
        }

        private async Task Run(string action)
        {
            if (_busy) return;
            if (!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput))
            { Report("Сначала вставьте готовый результат кнопкой «Повторить вставку».", true); return; }
            var selectedModel = _model.SelectedItem as ModelChoice;
            if (selectedModel == null) { Report("Выберите модель.", true); return; }
            var backgroundModel = _backgroundModel.SelectedItem as ModelChoice;
            if (action == "background" && backgroundModel == null)
            { Report("Выберите модель удаления фона.", true); return; }
            string prompt = _prompt.Text.Trim();
            if (action == "edit" && prompt.Length == 0) { Report("Выберите стиль или введите промпт.", true); return; }
            if (action == "edit" && selectedModel.Id == "bytedance/seedream-4.5" && prompt.Length > 4000)
            { Report("Для Seedream 4.5 сократите промпт до 4000 символов.", true); return; }
            string model = action == "background" ? backgroundModel.Id :
                action == "upscale" ? TiledUpscaler : selectedModel.Id;
            string key = _token();
            if (key.Length < 8) { Report("Укажите ключ Replicate во вкладке «Настройки».", true); return; }
            decimal cost = action == "background" ? backgroundModel.EstimatedCost :
                action == "upscale" ? 0.006m : selectedModel.EstimatedCost;
            string price = cost > 0m
                ? "Платный запрос Replicate · ориентировочно $" +
                    cost.ToString("0.000", CultureInfo.InvariantCulture) + "."
                : "Платный запрос Replicate · стоимость зависит от времени обработки.";
            if (MessageBox.Show(price + " Продолжить?", "Vanya Tools", MessageBoxButton.YesNo,
                MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            SetBusy(true, "Подготавливаю выделение…");
            try
            {
                _importWarning = null;
                // Corel COM capture must run on the UI thread. It duplicates the selection,
                // rasterizes the temporary copy, then restores the original objects.
                Log.Info("Simple AI capture starting: " + action);
                string source = action == "background" ? AiSelectionCapture.Capture() : _capture();
                Log.Info("Simple AI capture finished: " + action);
                _lastSource = action == "background" ? source : null;
                _sourcePreview.Source = PreviewBitmap(source);
                _resultPreview.Source = null;
                string image = action == "background"
                    ? AiPrintTab.DataUri(AiPrintTab.Bitmap(source), 4096, 16000000)
                    : AiPrintTab.DataUri(AiPrintTab.Bitmap(source), 2048,
                        action == "upscale" ? 1900000 : 4000000);
                Dictionary<string, object> input;
                if (action == "background")
                    input = model == BiRefNet
                        ? new Dictionary<string, object> { ["image"] = image,
                            ["variant"] = backgroundModel.Variant, ["output_format"] = "cutout" }
                        : new Dictionary<string, object> { ["image"] = image,
                            ["preserve_alpha"] = true, ["content_moderation"] = false };
                else if (action == "upscale")
                    input = new Dictionary<string, object> { ["img"] = image,
                        ["scale"] = 2, ["tile"] = 200, ["version"] = "General - v3", ["face_enhance"] = false };
                else input = PrintRestorationPipeline.ImageInput(model, image, prompt);
                _lastModel = model; _lastInput = input;
                _lastOutput = Path.Combine(Path.GetTempPath(), "Vanya-Simple-AI-" + Guid.NewGuid().ToString("N") + ".png");
                await Execute(key);
                ReportOutcome();
            }
            catch (OperationCanceledException) { Report("Операция отменена.", false); }
            catch (Exception ex) { Log.Error("Simple AI operation failed.", ex); Report(ex.Message, true); }
            finally { SetBusy(false, null); }
        }

        private async Task RemoveWhiteBackground()
        {
            if (_busy) return;
            if (!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput))
            { Report("Сначала вставьте готовый результат.", true); return; }
            int tolerance, cutPixels;
            Color backgroundColor;
            if (!TryHexColor(_backgroundHex.Text, out backgroundColor))
            { Report("Цвет фона: #RRGGBB.", true); return; }
            if (!Int32.TryParse(_backgroundTolerance.Text, out tolerance) || tolerance < 0 || tolerance > 80)
            { Report("Допуск цвета: от 0 до 80.", true); return; }
            if (!Int32.TryParse(_whiteCutPixels.Text, out cutPixels) || cutPixels < 0 || cutPixels > 8)
            { Report("Подрезка края: от 0 до 8 пикселей.", true); return; }
            SetBusy(true, "Удаляю выбранный фон…");
            try
            {
                _importWarning = null;
                Color[] backgroundVariants = BackgroundVariants(backgroundColor);
                string source = AiSelectionCapture.Capture();
                _sourcePreview.Source = PreviewBitmap(source);
                _resultPreview.Source = null;
                string output = Path.Combine(Path.GetTempPath(), "Vanya-Background-" + Guid.NewGuid().ToString("N") + ".png");
                CancellationToken cancellation = _cancellation.Token;
                await Task.Run(() => BackgroundRemovalProcessor.RemoveColor(
                    source, output, backgroundVariants, tolerance, cutPixels, cancellation), cancellation);
                cancellation.ThrowIfCancellationRequested();
                _readyOutput = output;
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
                File.WriteAllText(PendingPath(), output);
                _resultPreview.Source = PreviewBitmap(output);
                SetStage("Вставляю результат в Corel…");
                InsertReady();
                ReportOutcome();
            }
            catch (OperationCanceledException) { Report("Операция отменена.", false); }
            catch (Exception ex) { Log.Error("Local background removal failed.", ex);
                Report(ex.GetBaseException().Message, true); }
            finally { SetBusy(false, null); }
        }

        private async Task RemoveSemiTransparent()
        {
            if (_busy) return;
            if (!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput))
            { Report("Сначала вставьте готовый результат.", true); return; }
            int threshold;
            if (!Int32.TryParse(_alphaThreshold.Text, out threshold) || threshold < 0 || threshold > 255)
            { Report("Порог должен быть от 0 до 255.", true); return; }
            SetBusy(true, "Удаляю полупрозрачные пиксели…");
            try
            {
                _importWarning = null;
                // Local processing can retain the full capture resolution; the AI
                // actions above limit pixels to each remote model's practical range.
                string source = AiSelectionCapture.Capture();
                _sourcePreview.Source = PreviewBitmap(source);
                _resultPreview.Source = null;
                string output = Path.Combine(Path.GetTempPath(), "Vanya-Alpha-" + Guid.NewGuid().ToString("N") + ".png");
                CancellationToken cancellation = _cancellation.Token;
                await Task.Run(() => ThresholdAlpha(source, output, threshold, cancellation), cancellation);
                cancellation.ThrowIfCancellationRequested();
                _readyOutput = output;
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
                File.WriteAllText(PendingPath(), output);
                _resultPreview.Source = PreviewBitmap(output);
                InsertReady();
                ReportOutcome();
            }
            catch (OperationCanceledException) { Report("Операция отменена.", false); }
            catch (Exception ex) { Log.Error("Alpha threshold failed.", ex); Report(ex.GetBaseException().Message, true); }
            finally { SetBusy(false, null); }
        }

        private static void ThresholdAlpha(string source, string output, int threshold, CancellationToken cancellation)
        {
            BitmapSource input = AiPrintTab.Bitmap(source);
            var bgra = new FormatConvertedBitmap(input, PixelFormats.Bgra32, null, 0);
            int stride = checked(bgra.PixelWidth * 4);
            byte[] pixels = new byte[checked(stride * bgra.PixelHeight)];
            bgra.CopyPixels(pixels, stride, 0);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if ((i & 0x3fffff) == 0) cancellation.ThrowIfCancellationRequested();
                bool keep = pixels[i + 3] > 0 && pixels[i + 3] >= threshold;
                pixels[i + 3] = keep ? (byte)255 : (byte)0;
                if (!keep) pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
            }
            var result = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight,
                input.DpiX, input.DpiY, PixelFormats.Bgra32, null, pixels, stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
            using (var stream = File.Create(output)) encoder.Save(stream);
        }

        private async Task HighlightSemiTransparent()
        {
            if (_busy) return;
            if (!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput))
            { Report("Сначала вставьте готовый результат.", true); return; }
            bool red = _highlightColor.SelectedIndex == 1;
            SetBusy(true, "Ищу полупрозрачные пиксели…");
            try
            {
                _importWarning = null;
                string source = AiSelectionCapture.Capture();
                _sourcePreview.Source = PreviewBitmap(source);
                _resultPreview.Source = null;
                string output = Path.Combine(Path.GetTempPath(),
                    "Vanya-Alpha-Highlight-" + Guid.NewGuid().ToString("N") + ".png");
                CancellationToken cancellation = _cancellation.Token;
                long count = await Task.Run(() => MarkSemiTransparent(source, output, red, cancellation), cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (count == 0)
                {
                    Report("Полупрозрачных пикселей не найдено в подготовленном растре.", false);
                    return;
                }
                _readyOutput = output;
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
                File.WriteAllText(PendingPath(), output);
                _resultPreview.Source = PreviewBitmap(output);
                SetStage("Вставляю подсвеченную копию в Corel…");
                InsertReady();
                string warning = _importWarning;
                Report(String.IsNullOrEmpty(warning)
                    ? "Подсвечено полупрозрачных пикселей: " + count.ToString("N0", CultureInfo.CurrentCulture) + ". Копия вставлена рядом."
                    : warning, !String.IsNullOrEmpty(warning));
                _importWarning = null;
            }
            catch (OperationCanceledException) { Report("Операция отменена.", false); }
            catch (Exception ex) { Log.Error("Semi-transparent pixel highlight failed.", ex);
                Report(ex.GetBaseException().Message, true); }
            finally { SetBusy(false, null); }
        }

        private static long MarkSemiTransparent(string source, string output, bool red,
            CancellationToken cancellation)
        {
            BitmapSource input = AiPrintTab.Bitmap(source);
            var bgra = new FormatConvertedBitmap(input, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight;
            int length = checked(width * height), stride = checked(width * 4);
            byte[] pixels = new byte[checked(stride * height)];
            byte[] mask = new byte[length], work = new byte[length];
            bgra.CopyPixels(pixels, stride, 0);
            long count = 0;
            for (int p = 0; p < length; p++)
            {
                if ((p & 0xfffff) == 0) cancellation.ThrowIfCancellationRequested();
                byte alpha = pixels[p * 4 + 3];
                if (alpha > 0 && alpha < 255) { mask[p] = 255; count++; }
            }
            if (count == 0) return 0;

            // Two-pixel halo makes even a one-pixel translucent fringe visible.
            DilateHorizontal(mask, work, width, height, 2, cancellation);
            DilateVertical(work, mask, width, height, 2, cancellation);
            byte markerB = red ? (byte)32 : (byte)50;
            byte markerG = red ? (byte)45 : (byte)255;
            byte markerR = red ? (byte)255 : (byte)20;
            for (int p = 0; p < length; p++)
            {
                if ((p & 0xfffff) == 0) cancellation.ThrowIfCancellationRequested();
                if (mask[p] == 0) continue;
                int i = p * 4;
                byte originalAlpha = pixels[i + 3];
                bool center = originalAlpha > 0 && originalAlpha < 255;
                int strength = center ? 210 : 115;
                pixels[i] = (byte)((pixels[i] * (255 - strength) + markerB * strength + 127) / 255);
                pixels[i + 1] = (byte)((pixels[i + 1] * (255 - strength) + markerG * strength + 127) / 255);
                pixels[i + 2] = (byte)((pixels[i + 2] * (255 - strength) + markerR * strength + 127) / 255);
                pixels[i + 3] = (byte)Math.Max((int)originalAlpha, center ? 220 : 115);
            }
            var result = BitmapSource.Create(width, height, input.DpiX, input.DpiY,
                PixelFormats.Bgra32, null, pixels, stride);
            cancellation.ThrowIfCancellationRequested();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
            using (var stream = File.Create(output)) encoder.Save(stream);
            return count;
        }

        private async Task SmoothStairs()
        {
            if (_busy) return;
            if (!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput))
            { Report("Сначала вставьте готовый результат.", true); return; }
            int threshold;
            if (!Int32.TryParse(_alphaThreshold.Text, out threshold) || threshold < 0 || threshold > 255)
            { Report("Порог должен быть от 0 до 255.", true); return; }
            SetBusy(true, "Сглаживаю ступеньки края…");
            try
            {
                _importWarning = null;
                Log.Info("Opaque stair smoothing capture starting.");
                // Leave room for at least a 2x result, including on large prints.
                string source = AiSelectionCapture.Capture(8000, 8000000);
                Log.Info("Opaque stair smoothing capture finished.");
                _sourcePreview.Source = PreviewBitmap(source);
                _resultPreview.Source = null;
                string output = Path.Combine(Path.GetTempPath(), "Vanya-Stairs-" + Guid.NewGuid().ToString("N") + ".png");
                CancellationToken cancellation = _cancellation.Token;
                int scale = await Task.Run(() => SmoothOpaqueStairs(source, output, threshold, cancellation), cancellation);
                Log.Info("Opaque stair smoothing PNG finished.");
                cancellation.ThrowIfCancellationRequested();
                _readyOutput = output;
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
                File.WriteAllText(PendingPath(), output);
                _resultPreview.Source = PreviewBitmap(output);
                SetStage("Вставляю результат в Corel…");
                InsertReady();
                string warning = _importWarning;
                Report(String.IsNullOrEmpty(warning)
                    ? "Готово: рисунок сглажен, пиксельная сетка ×" + scale + ". Результат вставлен рядом."
                    : warning, !String.IsNullOrEmpty(warning));
                _importWarning = null;
            }
            catch (OperationCanceledException) { Report("Операция отменена.", false); }
            catch (Exception ex) { Log.Error("Opaque stair smoothing failed.", ex); Report(ex.GetBaseException().Message, true); }
            finally { SetBusy(false, null); }
        }

        private static int SmoothOpaqueStairs(string source, string output, int threshold,
            CancellationToken cancellation, Action<Int32Rect, double, double> cropped = null,
            int maximumOutputPixels = Int32.MaxValue, int fixedScale = 0)
        {
            BitmapSource input = AiPrintTab.Bitmap(source);
            var bgra = new FormatConvertedBitmap(input, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight;
            int length = checked(width * height), stride = checked(width * 4);
            // The capture is limited to 8 MP. An integer grid keeps every
            // generated pixel small and the final image within about 32 MP.
            int scale = length <= 2000000 ? 4 : length <= 3500000 ? 3 : 2;
            if (fixedScale > 0) scale = Math.Max(1, Math.Min(4, fixedScale));
            scale = Math.Min(scale, Math.Max(1, (int)Math.Sqrt((double)maximumOutputPixels / length)));
            int outWidth = checked(width * scale);
            int outHeight = checked(height * scale);
            Log.Info("Opaque stair smoothing: " + width + "x" + height +
                " -> " + outWidth + "x" + outHeight + ".");
            byte[] pixels = new byte[checked(stride * height)];
            byte[] mask = new byte[length], work = new byte[length];
            bgra.CopyPixels(pixels, stride, 0);
            for (int p = 0; p < length; p++)
            {
                if ((p & 0xfffff) == 0) cancellation.ThrowIfCancellationRequested();
                mask[p] = pixels[p * 4 + 3] >= threshold && pixels[p * 4 + 3] > 0
                    ? (byte)255 : (byte)0;
            }
            GaussianHorizontal(mask, work, width, height, 1, cancellation);
            GaussianVertical(work, mask, width, height, 1, cancellation);
            // Preserve original alpha separately while changing the blurred mask.
            for (int p = 0; p < length; p++)
            {
                int original = pixels[p * 4 + 3] >= threshold && pixels[p * 4 + 3] > 0 ? 255 : 0;
                work[p] = (byte)original;
                mask[p] = (byte)((mask[p] * 65 + original * 35 + 50) / 100);
            }
            byte[] resultPixels = new byte[checked(outWidth * outHeight * 4)];
            double scaleX = (double)outWidth / width, scaleY = (double)outHeight / height;
            for (int y = 0; y < outHeight; y++)
            {
                if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                double sy = (y + 0.5) / scaleY - 0.5;
                int y0 = Math.Max(0, Math.Min(height - 1, (int)Math.Floor(sy)));
                int y1 = Math.Min(height - 1, y0 + 1);
                double fy = Math.Max(0, Math.Min(1, sy - y0));
                for (int x = 0; x < outWidth; x++)
                {
                    double sx = (x + 0.5) / scaleX - 0.5;
                    int x0 = Math.Max(0, Math.Min(width - 1, (int)Math.Floor(sx)));
                    int x1 = Math.Min(width - 1, x0 + 1);
                    double fx = Math.Max(0, Math.Min(1, sx - x0));
                    int p00 = y0 * width + x0, p10 = y0 * width + x1;
                    int p01 = y1 * width + x0, p11 = y1 * width + x1;
                    double w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy);
                    double w01 = (1 - fx) * fy, w11 = fx * fy;
                    double coverage = mask[p00] * w00 + mask[p10] * w10 +
                        mask[p01] * w01 + mask[p11] * w11;
                    int destination = (y * outWidth + x) * 4;
                    if (coverage < 128) continue;
                    // Sample color from the kept artwork, not from translucent
                    // fringe pixels that may contain a matte or white halo.
                    double c00 = work[p00] != 0 ? w00 : 0;
                    double c10 = work[p10] != 0 ? w10 : 0;
                    double c01 = work[p01] != 0 ? w01 : 0;
                    double c11 = work[p11] != 0 ? w11 : 0;
                    double total = c00 + c10 + c01 + c11;
                    for (int channel = 0; channel < 3; channel++)
                    {
                        double color = total > 0 ?
                            (pixels[p00 * 4 + channel] * c00 + pixels[p10 * 4 + channel] * c10 +
                             pixels[p01 * 4 + channel] * c01 + pixels[p11 * 4 + channel] * c11) / total :
                            pixels[p00 * 4 + channel];
                        resultPixels[destination + channel] = (byte)Math.Max(0, Math.Min(255, Math.Round(color)));
                    }
                    resultPixels[destination + 3] = 255;
                }
            }
            Log.Info("Opaque stair smoothing pixels ready; saving PNG.");
            Int32Rect crop = SaveBinaryResult(output, resultPixels, outWidth, outHeight,
                input.DpiX * scaleX, input.DpiY * scaleY, cancellation);
            cropped?.Invoke(crop, input.DpiX * scaleX, input.DpiY * scaleY);
            return scale;
        }

        private void RefreshOutlinePresets()
        {
            string thin = "Тонкая обводка +" + DtfOutlineUnits.Format(_outlineSettings.ThinMm) + " мм";
            string thick = "Толстая обводка +" + DtfOutlineUnits.Format(_outlineSettings.ThickMm) + " мм";
            _thinOutline.ToolTip = thin; _thickOutline.ToolTip = thick;
            System.Windows.Automation.AutomationProperties.SetName(_thinOutline, thin);
            System.Windows.Automation.AutomationProperties.SetName(_thickOutline, thick);
        }

        private static Button OutlinePreset(bool thick, RoutedEventHandler handler)
        {
            var shape = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M5,4 C8,0 18,0 21,6 L23,14 C24,20 18,24 13,22 L4,20 C0,18 2,9 5,4 Z"),
                Stroke = Brushes.SlateGray, StrokeThickness = thick ? 3.0 : 1.0,
                StrokeLineJoin = PenLineJoin.Round, Fill = Brushes.Transparent,
                Width = 22, Height = 22, Stretch = Stretch.Uniform
            };
            var button = new Button { Content = shape, Width = 32, Height = 28,
                MinWidth = 32, MinHeight = 28, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(1) };
            button.Click += handler; return button;
        }

        private void RefreshEdgeDpi()
        {
            try { _edgeDpi = AiSelectionCapture.EstimateDefaultCaptureDpi(); _edgeDpiKnown = true; }
            catch { _edgeDpiKnown = false; }
        }

        private void ApplyOutlinePreset(double millimeters)
        {
            if (_busy) return;
            RefreshEdgeDpi();
            _edgeExpansion.Text = DtfOutlineUnits.Format(_edgeLengthUnit.SelectedIndex == 0
                ? millimeters : millimeters * _edgeDpi / 25.4);
            UpdateEdgeUnits(false);
        }

        private void SwitchEdgeUnits()
        {
            if (_convertingEdgeUnits || _edgeLengthUnit.SelectedIndex < 0) return;
            int next = _edgeLengthUnit.SelectedIndex;
            if (next == _previousEdgeUnit) return;
            RefreshEdgeDpi();
            _convertingEdgeUnits = true;
            try
            {
                if (!DtfOutlineUnits.TryNumber(_edgeRadius.Text, out double radius) ||
                    !DtfOutlineUnits.TryNumber(_edgeExpansion.Text, out double expansion))
                { _edgeLengthUnit.SelectedIndex = _previousEdgeUnit; SetEdgeMessage("Закончите ввод размеров перед переключением единиц."); return; }
                double factor = next == 0 ? 25.4 / _edgeDpi : _edgeDpi / 25.4;
                _edgeRadius.Text = DtfOutlineUnits.Format(radius * factor);
                _edgeExpansion.Text = DtfOutlineUnits.Format(expansion * factor);
                _previousEdgeUnit = next;
            }
            finally { _convertingEdgeUnits = false; }
            UpdateEdgeUnits(false);
        }

        private sealed class EdgeMetrics
        {
            internal int RadiusX, RadiusY, ExpansionX, ExpansionY;
            internal double DpiX, DpiY;
            internal static EdgeMetrics Measure(double radius, double expansion, bool mm, double dpiX, double dpiY) =>
                new EdgeMetrics { DpiX = dpiX, DpiY = dpiY,
                    RadiusX = DtfOutlineUnits.Pixels(radius, mm, dpiX, DtfOutlineUnits.MaxRadiusPixels, "Сглаживание"),
                    RadiusY = DtfOutlineUnits.Pixels(radius, mm, dpiY, DtfOutlineUnits.MaxRadiusPixels, "Сглаживание"),
                    ExpansionX = DtfOutlineUnits.Pixels(expansion, mm, dpiX, DtfOutlineUnits.MaxExpansionPixels, "Обводка"),
                    ExpansionY = DtfOutlineUnits.Pixels(expansion, mm, dpiY, DtfOutlineUnits.MaxExpansionPixels, "Обводка") };
            private static string Pair(double x, double y) => Math.Abs(x - y) < 0.000001
                ? DtfOutlineUnits.Format(x) : DtfOutlineUnits.Format(x) + "×" + DtfOutlineUnits.Format(y);
            internal string Description => "Сглаживание: " + Pair(RadiusX, RadiusY) + " px ≈ " +
                Pair(RadiusX * 25.4 / DpiX, RadiusY * 25.4 / DpiY) + " мм · обводка: +" +
                Pair(ExpansionX, ExpansionY) + " px ≈ " + Pair(ExpansionX * 25.4 / DpiX, ExpansionY * 25.4 / DpiY) +
                " мм · " + Pair(DpiX, DpiY) + " DPI";
            internal string Summary(bool inputMillimeters) => "Сгл. " +
                (inputMillimeters ? Pair(RadiusX, RadiusY) + " px" :
                    Pair(RadiusX * 25.4 / DpiX, RadiusY * 25.4 / DpiY) + " мм") + " · обв. +" +
                (inputMillimeters ? Pair(ExpansionX, ExpansionY) + " px" :
                    Pair(ExpansionX * 25.4 / DpiX, ExpansionY * 25.4 / DpiY) + " мм") +
                " · " + Pair(DpiX, DpiY) + " DPI";
        }

        private sealed class EdgeResult
        {
            internal Tuple<double, double> Offset;
            internal EdgeMetrics Metrics;
        }

        private void ShowEdgeMetrics(EdgeMetrics metrics, bool preliminary = false)
        {
            string details = metrics.Description + (preliminary
                ? ". Предварительно: выделите объект; точный пересчёт выполняется после захвата." : "");
            _edgeUnits.Text = metrics.Summary(_edgeLengthUnit.SelectedIndex == 0);
            _edgeUnits.ToolTip = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
            System.Windows.Automation.AutomationProperties.SetHelpText(_edgeUnits, details);
        }

        private void SetEdgeMessage(string message)
        {
            _edgeUnits.Text = message; _edgeUnits.ToolTip = null;
            System.Windows.Automation.AutomationProperties.SetHelpText(_edgeUnits, message);
        }

        private void UpdateEdgeUnits(bool refreshDpi = true)
        {
            if (_edgeUnits == null || _busy || _convertingEdgeUnits) return;
            if (refreshDpi) RefreshEdgeDpi();
            if (!DtfOutlineUnits.TryNumber(_edgeRadius.Text, out double radius) ||
                !DtfOutlineUnits.TryNumber(_edgeExpansion.Text, out double expansion))
            { SetEdgeMessage("Введите неотрицательные размеры, запятую или точку можно использовать как разделитель."); return; }
            try
            {
                ShowEdgeMetrics(EdgeMetrics.Measure(radius, expansion, _edgeLengthUnit.SelectedIndex == 0, _edgeDpi, _edgeDpi), !_edgeDpiKnown);
            }
            catch (Exception error) { SetEdgeMessage(error.Message); }
        }

        private async Task SmoothEdge()
        {
            if (_busy) return;
            if (!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput))
            { Report("Сначала вставьте готовый результат.", true); return; }
            int threshold;
            if (!Int32.TryParse(_alphaThreshold.Text, out threshold) || threshold < 0 || threshold > 255 ||
                !DtfOutlineUnits.TryNumber(_edgeRadius.Text, out double radius) ||
                !DtfOutlineUnits.TryNumber(_edgeExpansion.Text, out double expansion))
            { Report("Порог: 0–255; сглаживание и обводка — неотрицательные числа в выбранных единицах.", true); return; }
            bool millimeters = _edgeLengthUnit.SelectedIndex == 0;
            Color fill;
            if (!TryUnderlayColor(out fill)) { Report("Введите цвет подложки в формате #RRGGBB.", true); return; }
            SetBusy(true, "Сглаживаю край…");
            try
            {
                _importWarning = null;
                string source = AiSelectionCapture.Capture();
                _sourcePreview.Source = PreviewBitmap(source);
                _resultPreview.Source = null;
                string output = Path.Combine(Path.GetTempPath(), "Vanya-Edge-" + Guid.NewGuid().ToString("N") + ".png");
                CancellationToken cancellation = _cancellation.Token;
                EdgeResult edge = await Task.Run(() => SmoothBinaryEdge(source, output, threshold, radius,
                    expansion, millimeters, fill, cancellation), cancellation);
                _edgeDpi = edge.Metrics.DpiX; _edgeDpiKnown = true;
                ShowEdgeMetrics(edge.Metrics);
                cancellation.ThrowIfCancellationRequested();
                _readyOutput = output;
                Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
                File.WriteAllText(PendingPath(), output);
                _resultPreview.Source = PreviewBitmap(output);
                SetStage("Вставляю результат в Corel…");
                InsertReady();
                try { MoveImportedEdge(edge.Offset); }
                catch (Exception placementError)
                {
                    Log.Error("Edge result inserted but placement correction failed.", placementError);
                    _importWarning = (_importWarning ?? "Обводка вставлена.") +
                        " Проверьте положение результата: " + placementError.GetBaseException().Message;
                }
                ReportOutcome();
            }
            catch (OperationCanceledException) { Report("Операция отменена.", false); }
            catch (Exception ex) { Log.Error("Edge smoothing failed.", ex); Report(ex.GetBaseException().Message, true); }
            finally { SetBusy(false, null); }
        }

        private static EdgeResult SmoothBinaryEdge(string source, string output, int threshold,
            double radius, double expansion, bool millimeters, Color fill, CancellationToken cancellation)
        {
            BitmapSource input = AiPrintTab.Bitmap(source);
            EdgeMetrics metrics = EdgeMetrics.Measure(radius, expansion, millimeters, input.DpiX, input.DpiY);
            var bgra = new FormatConvertedBitmap(input, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight;
            int paddingX = checked(metrics.ExpansionX + metrics.RadiusX + 2);
            int paddingY = checked(metrics.ExpansionY + metrics.RadiusY + 2);
            int canvasWidth = checked(width + 2 * paddingX);
            int canvasHeight = checked(height + 2 * paddingY);
            int length = checked(canvasWidth * canvasHeight);
            byte[] pixels = new byte[checked(length * 4)];
            byte[] mask = new byte[length];
            byte[] work = new byte[length];
            bgra.CopyPixels(new Int32Rect(0, 0, width, height), pixels,
                checked(canvasWidth * 4), checked(((paddingY * canvasWidth) + paddingX) * 4));
            cancellation.ThrowIfCancellationRequested();
            for (int p = 0; p < length; p++)
                mask[p] = pixels[p * 4 + 3] > 0 && pixels[p * 4 + 3] >= threshold ? (byte)255 : (byte)0;
            if (expansion > 0)
            {
                DilateHorizontal(mask, work, canvasWidth, canvasHeight, metrics.ExpansionX, cancellation);
                DilateVertical(work, mask, canvasWidth, canvasHeight, metrics.ExpansionY, cancellation);
            }
            if (radius > 0)
            {
                GaussianHorizontal(mask, work, canvasWidth, canvasHeight, metrics.RadiusX, cancellation);
                GaussianVertical(work, mask, canvasWidth, canvasHeight, metrics.RadiusY, cancellation);
            }
            for (int p = 0; p < length; p++)
            {
                if ((p & 0xfffff) == 0) cancellation.ThrowIfCancellationRequested();
                int i = p * 4;
                // Blur changes only the outline. The exported alpha remains binary.
                if (mask[p] < 128)
                {
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = 0;
                }
                else if (pixels[i + 3] > 0 && pixels[i + 3] >= threshold)
                    pixels[i + 3] = 255;
                else
                {
                    pixels[i] = fill.B; pixels[i + 1] = fill.G; pixels[i + 2] = fill.R;
                    pixels[i + 3] = 255;
                }
            }
            Int32Rect crop = SaveBinaryResult(output, pixels, canvasWidth, canvasHeight,
                input.DpiX, input.DpiY, cancellation);
            double dpiX = input.DpiX > 0 ? input.DpiX : 96.0;
            double dpiY = input.DpiY > 0 ? input.DpiY : 96.0;
            return new EdgeResult { Metrics = metrics, Offset = Tuple.Create((paddingX - crop.X) * 25.4 / dpiX,
                (paddingY - crop.Y) * 25.4 / dpiY) };
        }

        private static void MoveImportedEdge(Tuple<double, double> offsetMm)
        {
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            dynamic shape = app.ActiveShape;
            if (doc == null || shape == null || Convert.ToInt32(shape.Type) != CorelConstants.CdrBitmapShape)
                throw new InvalidOperationException("CorelDRAW не вернул вставленную обводку для размещения.");
            int oldUnit = Convert.ToInt32(doc.Unit);
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                shape.Move(-offsetMm.Item1, offsetMm.Item2);
            }
            finally { doc.Unit = oldUnit; }
        }

        // Crop on the worker thread, before Corel imports the result. The old
        // post-import Corel bitmap scan blocked the docker on large prints.
        private static Int32Rect SaveBinaryResult(string output, byte[] pixels, int width,
            int height, double dpiX, double dpiY, CancellationToken cancellation)
        {
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (int y = 0; y < height; y++)
            {
                if ((y & 127) == 0) cancellation.ThrowIfCancellationRequested();
                int row = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    if (pixels[row + x * 4 + 3] == 0) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            var bitmap = BitmapSource.Create(width, height, dpiX, dpiY,
                PixelFormats.Bgra32, null, pixels, checked(width * 4));
            BitmapSource result = bitmap;
            var crop = new Int32Rect(0, 0, width, height);
            if (maxX >= 0)
            {
                const int padding = 2;
                int left = Math.Max(0, minX - padding), top = Math.Max(0, minY - padding);
                int right = Math.Min(width - 1, maxX + padding);
                int bottom = Math.Min(height - 1, maxY + padding);
                if (left != 0 || top != 0 || right != width - 1 || bottom != height - 1)
                {
                    crop = new Int32Rect(left, top, right - left + 1, bottom - top + 1);
                    result = new CroppedBitmap(bitmap, crop);
                }
            }
            cancellation.ThrowIfCancellationRequested();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
            using (var stream = File.Create(output)) encoder.Save(stream);
            return crop;
        }

        private static void DilateHorizontal(byte[] source, byte[] target, int width,
            int height, int radius, CancellationToken cancellation)
        {
            for (int y = 0; y < height; y++)
            {
                if ((y & 127) == 0) cancellation.ThrowIfCancellationRequested();
                int row = y * width, count = 0;
                for (int x = 0; x <= Math.Min(radius, width - 1); x++)
                    if (source[row + x] != 0) count++;
                for (int x = 0; x < width; x++)
                {
                    target[row + x] = count > 0 ? (byte)255 : (byte)0;
                    if (x - radius >= 0 && source[row + x - radius] != 0) count--;
                    if (x + radius + 1 < width && source[row + x + radius + 1] != 0) count++;
                }
            }
        }

        private static void DilateVertical(byte[] source, byte[] target, int width,
            int height, int radius, CancellationToken cancellation)
        {
            for (int x = 0; x < width; x++)
            {
                if ((x & 127) == 0) cancellation.ThrowIfCancellationRequested();
                int count = 0;
                for (int y = 0; y <= Math.Min(radius, height - 1); y++)
                    if (source[y * width + x] != 0) count++;
                for (int y = 0; y < height; y++)
                {
                    target[y * width + x] = count > 0 ? (byte)255 : (byte)0;
                    if (y - radius >= 0 && source[(y - radius) * width + x] != 0) count--;
                    if (y + radius + 1 < height && source[(y + radius + 1) * width + x] != 0) count++;
                }
            }
        }

        private static double[] GaussianWeights(int radius)
        {
            var weights = new double[radius * 2 + 1];
            double sigma = Math.Max(0.7, radius / 1.5), total = 0;
            for (int i = -radius; i <= radius; i++)
            {
                double value = Math.Exp(-(i * i) / (2 * sigma * sigma));
                weights[i + radius] = value; total += value;
            }
            for (int i = 0; i < weights.Length; i++) weights[i] /= total;
            return weights;
        }

        private static void GaussianHorizontal(byte[] source, byte[] target, int width,
            int height, int radius, CancellationToken cancellation)
        {
            double[] weights = GaussianWeights(radius);
            for (int y = 0; y < height; y++)
            {
                if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    double value = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int sample = x + k;
                        if (sample >= 0 && sample < width) value += source[row + sample] * weights[k + radius];
                    }
                    target[row + x] = (byte)Math.Min(255, Math.Round(value));
                }
            }
        }

        private static void GaussianVertical(byte[] source, byte[] target, int width,
            int height, int radius, CancellationToken cancellation)
        {
            double[] weights = GaussianWeights(radius);
            for (int y = 0; y < height; y++)
            {
                if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    double value = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int sample = y + k;
                        if (sample >= 0 && sample < height) value += source[sample * width + x] * weights[k + radius];
                    }
                    target[row + x] = (byte)Math.Min(255, Math.Round(value));
                }
            }
        }

        private bool TryUnderlayColor(out Color color)
        {
            return TryHexColor(_underlayHex.Text, out color);
        }

        private static bool TryHexColor(string value, out Color color)
        {
            color = Colors.White;
            string hex = value.Trim().TrimStart('#');
            if (hex.Length != 6) return false;
            int rgbValue;
            if (!Int32.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgbValue)) return false;
            color = Color.FromRgb((byte)(rgbValue >> 16), (byte)(rgbValue >> 8), (byte)rgbValue);
            return true;
        }

        private void UpdateUnderlaySwatch()
        {
            Color color;
            _underlaySwatch.Background = TryUnderlayColor(out color) ?
                new SolidColorBrush(color) : Brushes.Transparent;
        }

        private void UpdateBackgroundSwatch()
        {
            Color color;
            _backgroundSwatch.Background = TryHexColor(_backgroundHex.Text, out color) ?
                new SolidColorBrush(color) : Brushes.Transparent;
        }

        private void PickBackgroundFromCanvas()
        {
            if (_busy) return;
            try
            {
                dynamic app = CorelApp.Get();
                dynamic doc = app.ActiveDocument;
                if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
                Report("Кликните по цвету на холсте CorelDRAW. Esc — отмена.", false);
                double x = 0, y = 0;
                int shift = 0;
                int result = Convert.ToInt32(doc.GetUserClick(ref x, ref y, ref shift,
                    30, false, 326)); // cdrCursorEyeDrop
                if (result != 0) { Report("Выбор цвета отменён.", false); return; }
                dynamic sampled = doc.SampleColorAtPoint(x, y, 99); // cdrColorMixed: retain RGB/CMYK source
                if (sampled == null) throw new InvalidOperationException("Цвет в этой точке не найден.");
                dynamic rgb = app.CreateColor();
                rgb.CopyAssign(sampled);
                rgb.ConvertToRGB(); // Corel applies the document color profile to CMYK colors.
                _backgroundHex.Text = String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}",
                    (int)rgb.RGBRed, (int)rgb.RGBGreen, (int)rgb.RGBBlue);
                Report("Цвет фона: " + _backgroundHex.Text, false);
            }
            catch (Exception ex) { Log.Error("Corel canvas color pick failed.", ex);
                Report("Не удалось взять цвет с холста: " + ex.GetBaseException().Message, true); }
        }

        private static Color[] BackgroundVariants(Color chosen)
        {
            int min = Math.Min(chosen.R, Math.Min(chosen.G, chosen.B));
            int max = Math.Max(chosen.R, Math.Max(chosen.G, chosen.B));
            bool white = min >= 230 && max - min <= 15;
            bool black = max <= 60 && max - min <= 15;
            if (!white && !black) return new[] { chosen };
            try
            {
                dynamic app = CorelApp.Get();
                dynamic cmyk = app.CreateCMYKColor(0, 0, 0, white ? 0 : 100);
                cmyk.ConvertToRGB();
                Color converted = Color.FromRgb((byte)(int)cmyk.RGBRed,
                    (byte)(int)cmyk.RGBGreen, (byte)(int)cmyk.RGBBlue);
                return new[] { chosen, white ? Colors.White : Colors.Black, converted };
            }
            catch (Exception ex)
            {
                Log.Error("CMYK neutral conversion failed; using RGB neutral.", ex);
                return new[] { chosen, white ? Colors.White : Colors.Black };
            }
        }

        private void ChooseUnderlayColor()
        {
            if (_busy) return;
            try
            {
                TryUnderlayColor(out Color current);
                var source = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
                Color? picked = WindowsColorPicker.Pick(source == null ? IntPtr.Zero : source.Handle, current);
                if (!picked.HasValue) return;
                Color color = picked.Value;
                _underlayHex.Text = String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}",
                    color.R, color.G, color.B);
                Report("Цвет обводки: " + _underlayHex.Text, false);
            }
            catch (Exception error)
            {
                Log.Error("Could not choose outline color.", error);
                Report("Не удалось выбрать цвет обводки: " + error.GetBaseException().Message, true);
            }
        }

        private async Task BeginColorPick()
        {
            if (_busy) return;
            try
            {
                Report("Кликните по цвету на экране. Esc — отмена.", false);
                Color? picked = await ScreenColorPicker.PickAsync();
                if (!picked.HasValue) { Report("Выбор цвета отменён.", false); return; }
                Color color = picked.Value;
                _underlayHex.Text = String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}",
                    color.R, color.G, color.B);
                Report("Цвет подложки: " + _underlayHex.Text, false);
            }
            catch (Exception ex) { Report(ex.GetBaseException().Message, true); }
        }

        private async Task Execute(string key)
        {
            CancellationToken cancellation = _cancellation.Token;
            SetStage("Отправляю изображение…");
            await Task.Run(() => ReplicateWorkerClient.Run(_lastModel, key, _lastInput,
                _lastOutput, UpdateProgress, cancellation));
            cancellation.ThrowIfCancellationRequested();
            SetStage("Подготавливаю результат…");
            bool background = (_lastModel == "bria/remove-background" ||
                _lastModel == BiRefNet) &&
                !String.IsNullOrEmpty(_lastSource) && File.Exists(_lastSource);
            string png = Path.Combine(Path.GetTempPath(),
                (background ? "Vanya-Background-Ready-" :
                    _lastModel == TiledUpscaler ? "Vanya-Upscale-Ready-" : "Vanya-Style-Ready-") +
                Guid.NewGuid().ToString("N") + ".png");
            if (background)
            {
                SetStage("Совмещаю маску с исходником…");
                Log.Info("AI background mask application starting.");
                await Task.Run(() => BackgroundRemovalProcessor.ApplyModelMask(
                    _lastSource, _lastOutput, png, cancellation), cancellation);
                Log.Info("AI background mask application finished.");
            }
            else
                await Task.Run(() => SaveAsPng(_lastOutput, png), cancellation);
            _readyOutput = png;
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath()));
            File.WriteAllText(PendingPath(), png);
            _resultPreview.Source = PreviewBitmap(png);
            cancellation.ThrowIfCancellationRequested();
            SetStage("Вставляю результат в Corel…");
            InsertReady();
        }

        private static void SaveAsPng(string source, string destination)
        {
            BitmapSource image = AiPrintTab.Bitmap(source);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(destination)) encoder.Save(stream);
        }

        private void InsertReady()
        {
            try
            {
                string name = Path.GetFileName(_readyOutput);
                bool correction = name.StartsWith("Vanya-Correction-", StringComparison.OrdinalIgnoreCase);
                var placement = correction ? ReadCorrectionPlacement(_readyOutput) : null;
                if (correction) VerifyCorrectionDocument(placement);
                bool alreadyCropped = correction || name.StartsWith("Vanya-Edge-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Vanya-Stairs-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Vanya-Alpha-Highlight-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Vanya-Background-Ready-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Vanya-Local-Style-", StringComparison.OrdinalIgnoreCase);
                Log.Info("Simple AI import starting: " + name +
                    (alreadyCropped ? " (cropped PNG)" : ""));
                string warning = (alreadyCropped ? _importDtf : _import)(_readyOutput,
                    OperationName(name));
                if (correction) warning = PlaceCorrection(placement, warning);
                Log.Info("Simple AI import finished: " + name);
                _importWarning = warning;
                _readyOutput = null; _lastInput = null; _lastSource = null;
                try { if (File.Exists(PendingPath())) File.Delete(PendingPath()); } catch { }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Результат готов, но вставка не удалась. " +
                    "Нажмите «Повторить вставку» без новой оплаты. " + ex.GetBaseException().Message, ex);
            }
        }

        private string OperationName(string filename)
        {
            if (filename.StartsWith("Vanya-Correction-", StringComparison.OrdinalIgnoreCase))
                return "Коррекция и обводка";
            if (filename.StartsWith("Vanya-Background-Ready-", StringComparison.OrdinalIgnoreCase))
                return "Удаление фона · AI";
            if (filename.StartsWith("Vanya-Background-", StringComparison.OrdinalIgnoreCase))
                return "Удаление фона по цвету";
            if (filename.StartsWith("Vanya-Alpha-", StringComparison.OrdinalIgnoreCase))
                return filename.StartsWith("Vanya-Alpha-Highlight-", StringComparison.OrdinalIgnoreCase)
                    ? "Подсветка полупрозрачных пикселей" : "Удаление полупрозрачности";
            if (filename.StartsWith("Vanya-Stairs-", StringComparison.OrdinalIgnoreCase))
                return "Сглаживание лесенки";
            if (filename.StartsWith("Vanya-Edge-", StringComparison.OrdinalIgnoreCase))
                return "Обводка края";
            if (filename.StartsWith("Vanya-Upscale-Ready-", StringComparison.OrdinalIgnoreCase))
                return "Апскейл ×2";
            if (filename.StartsWith("Vanya-Style-Ready-", StringComparison.OrdinalIgnoreCase))
                return _mode == SimpleAiMode.Prompt ? "ИИ · обработка фото по промпту" :
                    _selectedStyle == null ? "Стилизация AI" : "Стилизация · " + _selectedStyle.Name;
            if (filename.StartsWith("Vanya-Local-Style-", StringComparison.OrdinalIgnoreCase))
                return "Локальная стилизация фото";
            return "Обработка растра";
        }

        private async Task Retry()
        {
            if (_busy) return;
            bool ready = !String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput);
            if (!ready && (_lastInput == null || String.IsNullOrEmpty(_lastOutput))) return;
            string key = ready ? "" : _token();
            if (!ready && key.Length < 8) { Report("Укажите ключ Replicate.", true); return; }
            if (!ready && MessageBox.Show("Продолжить запрос? При необходимости будет создан новый платный запуск.",
                "Vanya Tools", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            SetBusy(true, ready ? "Вставляю результат…" : "Продолжаю запрос…");
            try
            {
                _importWarning = null;
                if (ready) InsertReady();
                else await Execute(key);
                ReportOutcome();
            }
            catch (OperationCanceledException) { Report("Операция отменена.", false); }
            catch (Exception ex) { Log.Error("Simple AI retry failed.", ex); Report(ex.Message, true); }
            finally { SetBusy(false, null); }
        }

        private void Cancel()
        {
            if (!_busy || _cancellation == null) return;
            _cancellation.Cancel();
            _cancelButton.IsEnabled = false;
            SetStage("Отменяю запрос…");
        }

        private void UpdateProgress(string progress)
        {
            string message = progress == "sending" ? "Отправляю изображение…" :
                progress == "processing" ? "Модель обрабатывает изображение…" :
                progress == "downloading" ? "Загружаю результат…" :
                progress == "cancelling" ? "Отменяю запрос…" : progress;
            Dispatcher.BeginInvoke(new Action(() => { if (_busy) SetStage(message); }));
        }

        private void SetStage(string message) { _stage = message; _progress.Text = message; _status(message, false); }

        private void SetBusy(bool busy, string stage)
        {
            _busy = busy;
            _correctionButton.IsEnabled = !busy;
            _removeButton.IsEnabled = _removeWhiteButton.IsEnabled = _backgroundPipetteButton.IsEnabled = _upscaleButton.IsEnabled = _alphaButton.IsEnabled =
                _highlightButton.IsEnabled = _smoothButton.IsEnabled = _stairButton.IsEnabled = _pipetteButton.IsEnabled = _underlayColorButton.IsEnabled =
                _editButton.IsEnabled = _localApplyButton.IsEnabled = _localPreviewButton.IsEnabled = !busy;
            _model.IsEnabled = _backgroundModel.IsEnabled = _localPreset.IsEnabled = _prompt.IsEnabled = _backgroundHex.IsEnabled = _backgroundTolerance.IsEnabled = _whiteCutPixels.IsEnabled = _alphaThreshold.IsEnabled =
                _edgeRadius.IsEnabled = _edgeExpansion.IsEnabled = _underlayHex.IsEnabled = !busy;
            _localStrength.IsEnabled = _localContrast.IsEnabled = _localWarmth.IsEnabled =
                _localSaturation.IsEnabled = _localBrightness.IsEnabled = !busy;
            _highlightColor.IsEnabled = !busy;
            _edgeLengthUnit.IsEnabled = _thinOutline.IsEnabled = _thickOutline.IsEnabled = !busy;
            foreach (var choice in _styleButtons) choice.IsEnabled = !busy;
            foreach (var choice in _localStyleButtons) choice.IsEnabled = !busy;
            _cancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            _cancelButton.IsEnabled = busy;
            _retryButton.Visibility = !busy && ((!String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput)) ||
                _lastInput != null) ? Visibility.Visible : Visibility.Collapsed;
            _retryButton.Content = !String.IsNullOrEmpty(_readyOutput) && File.Exists(_readyOutput)
                ? "Повторить вставку · бесплатно" : "Повторить запрос";
            _progressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy)
            {
                _cancellation = new CancellationTokenSource();
                _elapsed.Restart(); _timer.Start(); SetStage(stage);
            }
            else
            {
                _timer.Stop(); _elapsed.Stop();
                if (_cancellation != null) { _cancellation.Dispose(); _cancellation = null; }
            }
        }

        private string PendingPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools", _mode == SimpleAiMode.Raster ? "pending-simple-ai-result.txt" :
                _mode == SimpleAiMode.Prompt ? "pending-photo-prompt-result.txt" :
                _mode == SimpleAiMode.LocalStyles ? "pending-local-photo-style-result.txt" : "pending-simple-style-result.txt");
        }

        private void RestorePending()
        {
            try
            {
                string legacy = Path.Combine(Path.GetDirectoryName(PendingPath()), "pending-simple-style-result.txt");
                if (_mode == SimpleAiMode.LocalStyles && !File.Exists(PendingPath()) && File.Exists(legacy))
                {
                    string previous = File.ReadAllText(legacy).Trim();
                    if (Path.GetFileName(previous).StartsWith("Vanya-Local-Style-", StringComparison.OrdinalIgnoreCase) && File.Exists(previous))
                    { File.WriteAllText(PendingPath(), previous); File.Delete(legacy); }
                }
                if (!File.Exists(PendingPath())) return;
                string path = File.ReadAllText(PendingPath()).Trim();
                if (!File.Exists(path)) return;
                if (_mode == SimpleAiMode.AiStyles &&
                    Path.GetFileName(path).StartsWith("Vanya-Local-Style-", StringComparison.OrdinalIgnoreCase)) return;
                _readyOutput = path;
                _resultPreview.Source = PreviewBitmap(path);
                SetBusy(false, null);
            }
            catch (Exception ex) { Log.Error("Simple AI pending result restore failed.", ex); }
        }

        private void Report(string message, bool error) { _status(message, error); _progress.Text = message; }
        private void ReportOutcome()
        {
            Report(String.IsNullOrEmpty(_importWarning) ? "Готово: результат вставлен на холст." : _importWarning,
                !String.IsNullOrEmpty(_importWarning));
            _importWarning = null;
        }
        private static TextBlock Label(string text, bool heading) { return new TextBlock { Text = text,
            FontSize = heading ? 12 : 10, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) }; }
        private static TextBlock Note(string text) { return new TextBlock { Text = text, FontSize = 10,
            Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 2) }; }
        private static BitmapSource PreviewBitmap(string path)
        {
            int width, height;
            using (var stream = File.OpenRead(path))
            {
                BitmapFrame frame = BitmapFrame.Create(stream,
                    BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand);
                width = frame.PixelWidth; height = frame.PixelHeight;
            }
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (width >= height) image.DecodePixelWidth = 256;
            else image.DecodePixelHeight = 256;
            image.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        private static Button Button(string text, RoutedEventHandler handler) { var button = new Button
            { Content = text, FontSize = 10, MinHeight = 24, Margin = new Thickness(1),
              Padding = new Thickness(3, 1, 3, 1) };
            button.Click += handler; return button; }
        private static Border Preview(string title, out Image image, int height)
        {
            image = new Image { Height = height, Stretch = Stretch.Uniform };
            var stack = new StackPanel(); stack.Children.Add(Label(title, false));
            stack.Children.Add(new Border { Height = height + 4, BorderThickness = new Thickness(1),
                BorderBrush = Brushes.LightGray, Child = image });
            return new Border { Margin = new Thickness(2), Child = stack };
        }
    }
}
