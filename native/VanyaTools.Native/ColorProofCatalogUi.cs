using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace VanyaTools.Native
{
    internal sealed partial class ColorProofTab
    {
        private Window _colorProofNumberWindow;

        private void ShowColorProofNumber()
        {
            try
            {
                if (_colorProofNumberWindow != null) { _colorProofNumberWindow.Activate(); return; }
                dynamic app = CorelApp.Get(), document = app.ActiveDocument;
                if (document == null) throw new InvalidOperationException("Откройте CDR с таблицей цветопробы.");
                object sourceDocument = (object)document;
                IReadOnlyList<ColorProofCatalog> catalogs = ColorProofCatalogStore.Load(sourceDocument);
                string preferred = null;
                try { preferred = ColorProofCatalogStore.ReadPage((object)document.ActivePage)?.CatalogId; }
                catch (Exception error) { Log.Error("Could not read active color proof catalog page binding.", error); }
                Window window = BuildColorProofNumberWindow(catalogs, preferred,
                    () => ColorProofCatalogActions.IsSourceDocumentActive(sourceDocument),
                    (cmyk, values) => { RestoreGridColorBase(cmyk, values); _mode.SelectedIndex = 2; },
                    (catalog, patch) => ColorProofCatalogActions.CreateSwatch(sourceDocument, catalog, patch.Number));
                _colorProofNumberWindow = window;
                window.Show();
            }
            catch (Exception error)
            {
                _colorProofNumberWindow = null;
                Fail("Could not open color proof number lookup.", error);
            }
        }

        // Managed snapshot + injected actions: constructing this window never reads Corel or changes an image.
        internal Window BuildColorProofNumberWindow(IReadOnlyList<ColorProofCatalog> catalogs, string preferredCatalogId,
            Func<bool> sourceDocumentIsActive, Action<bool, int[]> restoreReference,
            Action<ColorProofCatalog, ColorProofCatalogPatch> createSwatch)
        {
            if (catalogs == null) throw new ArgumentNullException(nameof(catalogs));
            if (sourceDocumentIsActive == null) throw new ArgumentNullException(nameof(sourceDocumentIsActive));
            if (restoreReference == null) throw new ArgumentNullException(nameof(restoreReference));
            if (createSwatch == null) throw new ArgumentNullException(nameof(createSwatch));
            var snapshots = catalogs.Select(ColorProofCatalog.CopyValidated).ToArray();
            var window = new Window { Title = "Цвет по номеру · Vanya Tools", Width = 480, Height = 330,
                MinWidth = 390, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            DockerTheme.Apply(window);
            var root = new DockPanel { Margin = new Thickness(12), LastChildFill = false }; window.Content = root;
            var body = new StackPanel(); DockPanel.SetDock(body, Dock.Top); root.Children.Add(body);
            body.Children.Add(Title("Цвет по номеру"));
            var table = Combo(); table.Name = "ProofCatalogSelector";
            table.Margin = new Thickness(0, 3, 0, 8);
            foreach (ColorProofCatalog catalog in snapshots)
                table.Items.Add(new ComboBoxItem { Content = catalog.Id + " · " + (catalog.Cmyk ? "CMYK" : "RGB") +
                    " · " + catalog.Patches.Count + " образцов", Tag = catalog });
            body.Children.Add(table);
            var number = Field("1"); number.Name = "ProofPatchNumber"; number.Width = 92;
            number.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(Row("Номер образца", number));
            var composition = Field(""); composition.Name = "ProofPatchComposition"; composition.IsReadOnly = true;
            composition.TextAlignment = TextAlignment.Left; composition.VerticalContentAlignment = VerticalAlignment.Center;
            composition.FontSize = 13; composition.Margin = new Thickness(0, 10, 0, 6); body.Children.Add(composition);
            var message = Note(""); message.Name = "ProofPatchStatus"; body.Children.Add(message);
            var buttons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) };
            var restore = Button("Сделать эталоном", null); restore.Name = "ProofPatchSetReference";
            restore.Margin = new Thickness(0, 0, 4, 0);
            var create = DockerTheme.Primary(Button("Создать плашку", null)); create.Name = "ProofPatchCreate";
            create.Margin = new Thickness(4, 0, 0, 0);
            buttons.Children.Add(restore); buttons.Children.Add(create);
            DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
            ColorProofCatalog selectedCatalog = null;
            ColorProofCatalogPatch selectedPatch = null;
            bool closed = false;
            void Status(string text, bool error)
            {
                message.Text = text;
                DockerTheme.UseTheme(message, TextBlock.ForegroundProperty,
                    error ? DockerTheme.ErrorTextBrush : DockerTheme.MutedTextBrush);
            }
            void UpdatePatch()
            {
                selectedCatalog = (table.SelectedItem as ComboBoxItem)?.Tag as ColorProofCatalog;
                selectedPatch = null; composition.Text = "";
                restore.IsEnabled = create.IsEnabled = false;
                if (selectedCatalog == null)
                {
                    Status("В этом CDR ещё нет таблиц с номерами. Для старой таблицы выделите нужную плашку и нажмите «Из выделения».", false);
                    return;
                }
                if (!Int32.TryParse(number.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int patchNumber) ||
                    patchNumber < 1 || patchNumber > selectedCatalog.Patches.Count)
                {
                    Status("Введите номер от 1 до " + selectedCatalog.Patches.Count + ".", true); return;
                }
                selectedPatch = selectedCatalog.FindPatch(patchNumber);
                composition.Text = ColorProofCatalogActions.Describe(selectedCatalog.Cmyk, selectedPatch.Values);
                Status("Образец №" + selectedPatch.Number + " · сохранённый состав таблицы.", false);
                restore.IsEnabled = create.IsEnabled = true;
            }
            bool CanAct()
            {
                if (closed || selectedCatalog == null || selectedPatch == null) return false;
                if (sourceDocumentIsActive()) return true;
                Status("Активный CDR изменился. Закройте окно и снова нажмите «По номеру» в нужном файле.", true);
                return false;
            }
            restore.Click += (_, __) => {
                try
                {
                    if (!CanAct()) return;
                    restoreReference(selectedCatalog.Cmyk, (int[])selectedPatch.Values.Clone());
                    Status("Образец №" + selectedPatch.Number + " выбран эталоном.", false);
                    _status(message.Text, false);
                }
                catch (Exception error) { Status(error.GetBaseException().Message, true); Log.Error("Could not restore numbered proof reference.", error); }
            };
            create.Click += (_, __) => {
                try
                {
                    if (!CanAct()) return;
                    createSwatch(ColorProofCatalog.CopyValidated(selectedCatalog), selectedCatalog.FindPatch(selectedPatch.Number));
                    Status("Плашка №" + selectedPatch.Number + " создана на новом листе.", false);
                    _status(message.Text, false);
                }
                catch (Exception error) { Status(error.GetBaseException().Message, true); Log.Error("Could not create numbered proof swatch.", error); }
            };
            table.SelectionChanged += (_, __) => UpdatePatch(); number.TextChanged += (_, __) => UpdatePatch();
            table.IsEnabled = number.IsEnabled = snapshots.Length > 0;
            int preferredIndex = Array.FindIndex(snapshots, catalog => catalog.Id == preferredCatalogId);
            table.SelectedIndex = snapshots.Length == 0 ? -1 : preferredIndex >= 0 ? preferredIndex : snapshots.Length - 1;
            UpdatePatch();
            Window owner = Window.GetWindow(this);
            if (owner != null) window.Owner = owner;
            else if (PresentationSource.FromVisual(this) is HwndSource host) new WindowInteropHelper(window).Owner = host.Handle;
            window.Closed += (_, __) => {
                closed = true;
                if (ReferenceEquals(_colorProofNumberWindow, window)) _colorProofNumberWindow = null;
            };
            return window;
        }
    }
}
