using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace VanyaTools.Native
{
    internal sealed partial class SportLayoutTab
    {
        private Grid _workflowGuide;
        private TextBlock _workflowGuideText;
        private Button _workflowGuideNext;
        private int _workflowGuideTarget = -1;
        private bool _workflowGuideContextReady;

        private void SetWorkflowGuideContext(bool ready)
        {
            _workflowGuideContextReady = ready;
            if (!ready && _workflowGuideNext != null) _workflowGuideNext.Visibility = Visibility.Collapsed;
            RefreshWorkflowGuide();
        }

        private FrameworkElement CreateWorkflowGuide()
        {
            if (_workflowGuide != null) return _workflowGuide;
            _workflowGuide = new Grid { Margin = new Thickness(0, 2, 0, 4) };
            _workflowGuide.ColumnDefinitions.Add(new ColumnDefinition());
            _workflowGuide.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _workflowGuideText = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center };
            DockerTheme.UseTheme(_workflowGuideText, TextBlock.ForegroundProperty, DockerTheme.MutedTextBrush);
            _workflowGuide.Children.Add(_workflowGuideText);
            _workflowGuideNext = new Button { FontSize = 11, MinHeight = 27, MinWidth = 0,
                Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 0),
                Content = "Далее", IsEnabled = false, Visibility = Visibility.Collapsed };
            _workflowGuideNext.Click += (_, __) =>
            {
                if (_busy || _loading || _switchingPlayers || !_workflowGuideContextReady) return;
                RefreshWorkflowGuide();
                int target = _workflowGuideTarget;
                if (!_workflowGuideNext.IsEnabled || _steps == null || target < 0 || target >= _steps.Items.Count) return;
                // Only navigate. Preparing, generating and exporting remain
                // explicit actions of their respective tabs.
                _steps.SelectedIndex = target;
                RefreshWorkflowGuide();
            };
            Grid.SetColumn(_workflowGuideNext, 1); _workflowGuide.Children.Add(_workflowGuideNext);
            RefreshWorkflowGuide();
            return _workflowGuide;
        }

        private void RefreshWorkflowGuide()
        {
            if (_workflowGuideText == null || _workflowGuideNext == null) return;
            _workflowGuideNext.IsEnabled = false; _workflowGuideTarget = -1;
            if (!_workflowGuideContextReady && _projectDocument != null)
            {
                ShowWorkflowGuide("Данные текущего CDR не загружены.");
                return;
            }
            if (_busy)
            {
                _workflowGuideText.Text = "Выполняется операция. Дождитесь завершения.";
                return;
            }
            if (_loading || _switchingPlayers || _steps == null)
            {
                _workflowGuideText.Text = "Загружаются данные текущего CDR…";
                return;
            }
            if (_projectDocument == null)
            {
                ShowWorkflowGuide("Откройте CDR проекта. Данные формы сохраняются в нём.");
                return;
            }

            bool modelsReady = _templates != null && _templates.Any(TemplateConfigured);
            bool playersReady = WorkflowGuidePlayersReady();
            bool layoutsPrepared = _completedSteps != null && _completedSteps.Length > 1 && _completedSteps[1];
            bool printCreated = _completedSteps != null && _completedSteps.Length > 3 && _completedSteps[3];
            bool pdfExported = _completedSteps != null && _completedSteps.Length > 4 && _completedSteps[4];
            bool cutCreated = _completedSteps != null && _completedSteps.Length > 5 && _completedSteps[5];

            // Visual order: patterns, artwork, players, mockups, printing, cutting.
            // Completion state indices stay in their original persisted order.
            switch (_steps.SelectedIndex)
            {
                case 0:
                    if (modelsReady) ShowWorkflowGuide("Лекала назначены. Далее — рисунки деталей.", 1, "К рисункам");
                    else ShowWorkflowGuide("Выберите модель CDR и проверьте размеры.");
                    break;
                case 1:
                    if (!modelsReady) ShowWorkflowGuide("Для новых комплектов сначала назначьте лекала.", 0, "К лекалам");
                    else ShowWorkflowGuide("Назначьте нужные рисунки. Далее — игроки; пустой рисунок допустим.", 2, "К игрокам");
                    break;
                case 2:
                    if (!modelsReady) ShowWorkflowGuide("Для новых комплектов сначала назначьте лекала.", 0, "К лекалам");
                    else if (!playersReady) ShowWorkflowGuide("Добавьте строки «В заказ», проверьте размеры и количество.");
                    else ShowWorkflowGuide("Список готов. Теперь подгоните макеты выбранных размеров.", 3, "К макетам");
                    break;
                case 3:
                    if (!modelsReady) ShowWorkflowGuide("Для новых макетов сначала назначьте лекала.", 0, "К лекалам");
                    else if (!playersReady) ShowWorkflowGuide("Сначала заполните игроков: по ним выбираются размеры макетов.", 2, "К игрокам");
                    else if (!layoutsPrepared) ShowWorkflowGuide("Нажмите «Подготовить макеты», затем подгоните рисунки и надписи на холсте.");
                    else ShowWorkflowGuide("Подгоните макеты на холсте. Далее — печать; резка при необходимости.", 4, "К печати");
                    break;
                case 4:
                    // A ready page can be exported even when an older CDR has
                    // no workflow marks or saved roster. Never require rebuild.
                    if (pdfExported) ShowWorkflowGuide("PDF сохранён. Другой лист — здесь; резка при необходимости.", 5, "К резке");
                    else if (printCreated) ShowWorkflowGuide("Откройте лист печати и сохраните PDF. Резка — при необходимости.", 5, "К резке");
                    else if (!playersReady) ShowWorkflowGuide("Готовый лист → PDF. Для новой раскладки заполните игроков.", 2, "К игрокам");
                    else if (!layoutsPrepared) ShowWorkflowGuide("Готовый лист → PDF. Для новой раскладки подготовьте макеты.", 3, "К макетам");
                    else ShowWorkflowGuide("Подберите раскладку и сохраните PDF. Резка — при необходимости.", 5, "К резке");
                    break;
                case 5:
                    // Cutting is independent of the printing/PDF branch.
                    if (cutCreated) ShowWorkflowGuide("Откройте лист резки и сохраните DXF.");
                    else if (!playersReady) ShowWorkflowGuide("Готовый лист → DXF. Для новой резки заполните игроков.", 2, "К игрокам");
                    else ShowWorkflowGuide("Подберите резку или сохраните DXF готового листа.");
                    break;
                default:
                    ShowWorkflowGuide("Выберите вкладку формы.");
                    break;
            }
        }

        private void ShowWorkflowGuide(string text, int target = -1, string caption = null)
        {
            _workflowGuideText.Text = text;
            bool navigate = _workflowGuideContextReady && _projectDocument != null && target >= 0 &&
                _steps != null && target < _steps.Items.Count && target != _steps.SelectedIndex;
            _workflowGuideTarget = navigate ? target : -1;
            _workflowGuideNext.Content = caption ?? "Далее";
            _workflowGuideNext.Visibility = navigate ? Visibility.Visible : Visibility.Collapsed;
            _workflowGuideNext.IsEnabled = navigate && !_busy && !_loading && !_switchingPlayers;
            _workflowGuideNext.ToolTip = "Только переход между вкладками. Рабочие действия запускаются отдельно.";
        }

        private bool WorkflowGuidePlayersReady()
        {
            if (_players == null || _templates == null) return false;
            // More than 300 selected rows cannot fit the existing 300-kit
            // limit. Bound this cheap UI check without reading any files.
            var selected = _players.Where(player => player != null && player.PrintEnabled).Take(301).ToArray();
            if (selected.Length == 0 || selected.Length > 300) return false;
            var sizes = new HashSet<string>(_templates.Where(TemplateConfigured)
                .GroupBy(template => SportLayoutService.NormalizeSize(template.Size), StringComparer.Ordinal)
                .Where(group => group.Count() == 1).Select(group => group.Key), StringComparer.Ordinal);
            long total = 0;
            foreach (var player in selected)
            {
                if (player.Quantity < 1 || player.Quantity > 100 || (player.Name ?? "").Length > 100 ||
                    (player.Number ?? "").Length > 20 || String.IsNullOrWhiteSpace(player.Size) ||
                    !sizes.Contains(SportLayoutService.NormalizeSize(player.Size))) return false;
                total += player.Quantity;
                if (total > 300) return false;
            }
            return true;
        }
    }
}
