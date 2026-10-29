using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class CorelApp
    {
        internal static FakeApp Current;
        internal static int Gets;
        public static dynamic Get() { Gets++; return Current; }
    }
    internal static class Log
    {
        public static void Info(string text) { }
        public static void Error(string text, Exception error) { }
    }

    public sealed class FakeOptions { }
    public sealed class FakeWindow { public void Refresh() { CorelApp.Current.Events.Add("Refresh"); } }
    public sealed class FakeApp
    {
        public FakeDocument ActiveDocument;
        public FakeRange ActiveSelectionRange;
        public bool Optimization;
        public FakeWindow ActiveWindow { get; } = new FakeWindow();
        public string Failure = "";
        public readonly List<string> Events = new List<string>();
        public FakeOptions CreateStructImportOptions() { Events.Add("Options"); return new FakeOptions(); }
        public FakeRange CreateShapeRange() { Events.Add("CreateRange"); return new FakeRange(this); }
    }
    public sealed class FakeRange : IEnumerable
    {
        internal readonly FakeApp App;
        public readonly List<FakeShape> Items = new List<FakeShape>();
        public FakeRange(FakeApp app) { App = app; }
        public int Count => Items.Count;
        public FakeRange Shapes => this;
        public double LeftX => Items.Min(shape => shape.LeftX);
        public double TopY => Items.Max(shape => shape.TopY);
        public double SizeWidth => Items.Max(shape => shape.LeftX + shape.SizeWidth) - LeftX;
        public double SizeHeight => TopY - Items.Min(shape => shape.TopY - shape.SizeHeight);
        public void Add(object shape) { Items.Add((FakeShape)shape); }
        public void Delete()
        {
            App.Events.Add("DeleteRange");
            foreach (FakeShape shape in Items.ToArray()) shape.Delete();
        }
        public IEnumerator GetEnumerator() => Items.GetEnumerator();
    }
    public sealed class FakeShape
    {
        internal readonly FakeDocument Document;
        internal double Left, Top, Width, Height;
        internal bool Deleted, Imported, ThrowOnRead;
        internal int ReadAttempts;
        internal FakeLayer Owner;
        internal string RawName;
        public int StaticID { get; }
        public int Type { get; set; } = 5;
        public double LeftX { get { Live(); return Left; } }
        public double TopY { get { Live(); return Top; } }
        public double SizeWidth { get { Live(); return Width; } }
        public double SizeHeight { get { Live(); return Height; } }
        public FakeLayer Layer { get { Live(); return Owner; } }
        public string Name
        {
            get { Live(); return RawName; }
            set { Live(); Document.App.Events.Add("Name:" + StaticID); RawName = value; Document.Mutated(); Fail("Name"); }
        }
        public FakeShape(FakeDocument document, FakeLayer layer, int id, string name, double left, double top, double width, double height, bool imported = false)
        { Document = document; Owner = layer; StaticID = id; RawName = name; Left = left; Top = top; Width = width; Height = height; Imported = imported; layer.Items.Add(this); }
        private void Live()
        {
            ReadAttempts++;
            if (ThrowOnRead) throw new InvalidOperationException("Original properties must not be read");
            if (Deleted) throw new InvalidOperationException("Object deleted");
        }
        private void Fail(string stage)
        { if (Imported && Document.App.Failure == stage) throw new InvalidOperationException("Injected " + stage); }
        public void SetSize(double width, double height)
        {
            Live(); if (Document.Unit != 3) throw new Exception("SetSize before millimetres");
            Document.App.Events.Add("SetSize"); Width = width; Height = height; Document.Mutated(); Fail("SetSize");
        }
        public void Move(double x, double y)
        {
            Live(); if (Document.Unit != 3) throw new Exception("Move before millimetres");
            Document.App.Events.Add((Imported ? "MoveImported" : "MoveOriginal:") + (Imported ? "" : StaticID.ToString()));
            Left += x; Top += y; Document.Mutated(); Fail("Move");
            if (!Imported && Document.App.Failure == "MoveOriginalSecond" && StaticID == 12) throw new InvalidOperationException("Injected source move failure");
        }
        public void MoveToLayer(object layer)
        {
            Live(); Document.App.Events.Add("MoveToLayer"); Owner.Items.Remove(this); Owner = (FakeLayer)layer; Owner.Items.Add(this); Document.Mutated(); Fail("MoveToLayer");
        }
        public void Delete()
        {
            Live(); Document.App.Events.Add("DeleteOriginal:" + StaticID); Deleted = true; Owner.Items.Remove(this); Document.Mutated();
            if (Document.App.Failure == "DeleteOriginalSecond" && StaticID == 12) throw new InvalidOperationException("Injected source delete failure");
        }
        public void CreateSelection()
        {
            Live(); Document.App.Events.Add("SelectImported");
            if (Document.App.Failure == "CreateSelection") throw new InvalidOperationException("Injected selection failure");
            Document.App.ActiveSelectionRange = new FakeRange(Document.App); Document.App.ActiveSelectionRange.Items.Add(this);
        }
    }
    public sealed class FakeLayer
    {
        public readonly FakeDocument Document;
        public readonly FakePage Page;
        public readonly List<FakeShape> Items = new List<FakeShape>();
        public readonly string Name;
        public bool Deleted;
        public FakeLayer(FakeDocument document, FakePage page, string name) { Document = document; Page = page; Name = name; }
        public FakeRange Shapes { get { var range = new FakeRange(Document.App); range.Items.AddRange(Items.Where(shape => !shape.Deleted)); return range; } }
        public FakeImportFilter ImportEx(object path, int filter, object options)
        {
            Document.App.Events.Add("ImportEx");
            var bstr = path as BStrWrapper;
            if (bstr == null || !(bstr.WrappedObject is string) || !Path.IsPathRooted((string)bstr.WrappedObject)) throw new Exception("Missing BSTR absolute path");
            if (filter != 0 || !(options is FakeOptions)) throw new Exception("Incorrect import filter/options");
            if (Document.App.Failure == "ImportEx") throw new InvalidOperationException("Injected import failure");
            return new FakeImportFilter(this);
        }
        public void Delete()
        {
            Document.App.Events.Add("DeleteTemp");
            if (Document.App.Failure == "DeleteTemp") throw new InvalidOperationException("Injected temp delete failure");
            Deleted = true; Page.Layers.Remove(this); Document.Mutated();
            if (Document.App.Failure == "DeleteTempAfter") throw new InvalidOperationException("Injected temp delete after mutation");
        }
    }
    public sealed class FakeImportFilter
    {
        private readonly FakeLayer Layer;
        public FakeImportFilter(FakeLayer layer) { Layer = layer; }
        public void Finish()
        {
            var app = Layer.Document.App; app.Events.Add("Finish");
            if (app.Failure == "FinishBefore") throw new InvalidOperationException("Injected early Finish");
            new FakeShape(Layer.Document, Layer, 900, "imported", -999, 999, 1, 1, true); Layer.Document.Mutated();
            if (app.Failure == "FinishAfter") throw new InvalidOperationException("Injected late Finish");
        }
    }
    public sealed class FakePage
    {
        public readonly FakeDocument Document;
        public int IndexValue;
        public bool IndexThrows;
        public int Index
        {
            get { if (IndexThrows) throw new InvalidOperationException("Captured page deleted"); return IndexValue; }
        }
        public readonly List<FakeLayer> Layers = new List<FakeLayer>();
        public bool HideFindResults;
        public FakePage(FakeDocument document, int index) { Document = document; IndexValue = index; }
        public FakeLayer CreateLayer(string name)
        {
            Document.App.Events.Add("CreateLayer");
            if (Document.App.Failure == "CreateLayer") throw new InvalidOperationException("Injected layer failure");
            if (Document.App.Failure == "CreateLayerNull") return null;
            var layer = new FakeLayer(Document, this, name); Layers.Add(layer); Document.Mutated(); return layer;
        }
        public FakeShape FindShape(string name, int type, int id, bool recursive)
        {
            Document.App.Events.Add("Find:" + id);
            return HideFindResults ? null : Layers.SelectMany(layer => layer.Items).FirstOrDefault(shape => !shape.Deleted && shape.StaticID == id);
        }
    }
    // An automation facade may represent the same page without sharing managed/COM identity.
    public sealed class FakeCapturedPageFacade
    {
        private readonly FakePage Page;
        public FakeCapturedPageFacade(FakePage page) { Page = page; }
        public int Index => Page.Index;
    }
    // Destructive insertion must validate this captured reference but mutate the object
    // resolved on the active page by StaticID, rather than a possibly stale wrapper.
    public sealed class FakeCapturedShapeFacade
    {
        private readonly FakeShape Shape;
        public string NameOverride;
        public FakeCapturedShapeFacade(FakeShape shape) { Shape = shape; }
        public int StaticID => Shape.StaticID;
        public int Type => Shape.Type;
        public string Name => NameOverride ?? Shape.Name;
        public double LeftX => Shape.LeftX;
        public double TopY => Shape.TopY;
        public double SizeWidth => Shape.SizeWidth;
        public double SizeHeight => Shape.SizeHeight;
        public object Layer => Shape.Layer;
        public void Move(double x, double y) { throw new Exception("Captured wrapper was mutated instead of active-page result"); }
        public void Delete() { throw new Exception("Captured wrapper was deleted instead of active-page result"); }
    }
    public sealed class FakeDocument
    {
        public readonly FakeApp App;
        public FakePage ActivePage;
        public FakeLayer ActiveLayer;
        public string FullFileName;
        public bool Dirty;
        public int Unit = 7;
        public int ReferencePoint = 9;
        public int BeginCount, EndCount, UndoCount, Mutations, Depth;
        private Snapshot Before;
        public FakeDocument(FakeApp app, string path) { App = app; FullFileName = path; }
        public void BeginCommandGroup(string label)
        {
            if (Depth != 0) throw new Exception("Nested command groups");
            App.Events.Add("Begin"); BeginCount++; Depth = 1; Mutations = 0; Before = new Snapshot(this);
        }
        public void EndCommandGroup() { if (Depth != 1) throw new Exception("Unbalanced end"); App.Events.Add("End"); EndCount++; Depth = 0; }
        public void Mutated() { Dirty = true; if (Depth == 1) Mutations++; }
        public void Undo()
        {
            App.Events.Add("Undo"); UndoCount++;
            if (Depth != 0) throw new Exception("Undo inside command group");
            if (Mutations == 0) throw new Exception("WOULD UNDO AN UNRELATED USER COMMAND");
            Before.Restore(this);
        }
        private sealed class Snapshot
        {
            private readonly FakePage Page;
            private readonly FakeLayer ActiveLayer;
            private readonly FakeLayer[] Layers;
            private readonly State[] States;
            private readonly bool Dirty;
            private readonly FakeRange Selection;
            internal Snapshot(FakeDocument document)
            {
                Page = document.ActivePage; ActiveLayer = document.ActiveLayer; Layers = Page.Layers.ToArray();
                States = Layers.SelectMany(layer => layer.Items).Select(shape => new State(shape)).ToArray();
                Dirty = document.Dirty; Selection = document.App.ActiveSelectionRange;
            }
            internal void Restore(FakeDocument document)
            {
                foreach (var layer in Page.Layers) layer.Items.Clear();
                foreach (var layer in Layers) layer.Items.Clear();
                Page.Layers.Clear(); Page.Layers.AddRange(Layers);
                foreach (var state in States) state.Restore();
                document.ActivePage = Page; document.ActiveLayer = ActiveLayer; document.Dirty = Dirty; document.App.ActiveSelectionRange = Selection;
            }
        }
        private sealed class State
        {
            private readonly FakeShape Shape;
            private readonly FakeLayer Layer;
            private readonly double Left, Top, Width, Height;
            private readonly bool Deleted;
            private readonly string Name;
            internal State(FakeShape shape) { Shape = shape; Layer = shape.Owner; Left = shape.Left; Top = shape.Top; Width = shape.Width; Height = shape.Height; Deleted = shape.Deleted; Name = shape.RawName; }
            internal void Restore() { Shape.Owner = Layer; Shape.Left = Left; Shape.Top = Top; Shape.Width = Width; Shape.Height = Height; Shape.Deleted = Deleted; Shape.RawName = Name; Layer.Items.Add(Shape); }
        }
    }
    internal sealed class Fixture
    {
        internal readonly string Root, Input, Output;
        internal readonly FakeApp App;
        internal readonly FakeDocument Document;
        internal readonly FakePage Page;
        internal readonly FakeLayer Layer;
        internal readonly FakeShape A, B, Other;
        internal RasterInsertionSource Source;
        internal Fixture(string root, string name)
        {
            Root = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
            string cdr = Path.Combine(Root, "project.cdr"); File.WriteAllText(cdr, "fixture saved project");
            App = new FakeApp(); Document = new FakeDocument(App, cdr); App.ActiveDocument = Document;
            Page = new FakePage(Document, 1); Document.ActivePage = Page;
            Layer = new FakeLayer(Document, Page, "original"); Page.Layers.Add(Layer); Document.ActiveLayer = Layer;
            A = new FakeShape(Document, Layer, 11, "source A", 100, 200, 40, 50);
            B = new FakeShape(Document, Layer, 12, "source B", 160, 180, 40, 20);
            Other = new FakeShape(Document, Layer, 13, "today selection", 800, 900, 10, 10);
            App.ActiveSelectionRange = new FakeRange(App); App.ActiveSelectionRange.Items.Add(A); App.ActiveSelectionRange.Items.Add(B);
            CorelApp.Current = App;
            Source = RasterResultInsertion.CaptureSelection();
            Input = Path.Combine(Root, "input.png"); Output = Path.Combine(Root, "output.png");
            Program.Png(Input, 400, 200, 101.6, 101.6); Program.Png(Output, 440, 240, 101.6, 101.6);
            RasterResultInsertion.SetInputRaster(Source, Input);
        }
        internal void Save(RasterInsertionMode mode, bool preserve = false, bool diagnostic = false)
            => RasterResultInsertion.SaveResult(Output, Source, mode, 5, 5, preserve, diagnostic);
        internal RasterInsertionPlacement Placement() => new JavaScriptSerializer().Deserialize<RasterInsertionPlacement>(File.ReadAllText(Output + ".insertion.json"));
        internal FakeShape Imported => Page.Layers.SelectMany(layer => layer.Items).Single(shape => shape.Imported && !shape.Deleted);
        internal void SelectOther() { App.ActiveSelectionRange = new FakeRange(App); App.ActiveSelectionRange.Items.Add(Other); }
    }
    internal static class Program
    {
        private static readonly List<string> Passed = new List<string>(), Failed = new List<string>();
        private static string Root;
        [STAThread] private static int Main()
        {
            Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cases"); Directory.CreateDirectory(Root);
            Run("SaveResult offsets and physical size", () => { var f = New("geometry"); f.Save(RasterInsertionMode.ReplaceOriginal); var p = f.Placement(); Eq(p.LeftMm, 95); Eq(p.TopMm, 205); Eq(p.WidthMm, 110); Eq(p.HeightMm, 60); Check(!File.ReadAllText(f.Output + ".insertion.json").Contains("NativeShapes"), "Native refs leaked into JSON"); });
            Run("Preserve source canvas overrides output DPI/size", () => { var f = New("preserve"); Png(f.Output, 800, 400, 300, 150); f.Save(RasterInsertionMode.ReplaceOriginal, true); var p = f.Placement(); Eq(p.WidthMm, 100); Eq(p.HeightMm, 50); });
            Run("Anisotropic captured scale compensates physical output and offsets", () => { var f = New("scale"); Png(f.Input, 400, 200, 100, 200); RasterResultInsertion.SetInputRaster(f.Source, f.Input); Png(f.Output, 200, 100, 200, 100); RasterResultInsertion.SaveResult(f.Output, f.Source, RasterInsertionMode.ReplaceOriginal, 2, 3); var p = f.Placement(); Eq(p.WidthMm, 25, .002); Eq(p.HeightMm, 50, .002); Eq(p.LeftMm, 100 - 2 * (100 / 101.6), .002); Eq(p.TopMm, 200 + 3 * (50 / 25.4), .002); });
            foreach (RasterInsertionMode mode in new[] { RasterInsertionMode.ReplaceOriginal, RasterInsertionMode.MoveOriginalAside, RasterInsertionMode.NewAside })
                Run("Mode " + mode + " uses captured source and one Undo group", () => {
                    var f = New(mode.ToString()); f.Save(mode); f.SelectOther();
                    RasterResultInsertion.Import(f.Output, "QA", mode, f.Source);
                    var image = f.Imported; Eq(image.Left, mode == RasterInsertionMode.NewAside ? 105 : 95); Eq(image.Top, mode == RasterInsertionMode.NewAside ? 215 : 205); Eq(image.Width, 110); Eq(image.Height, 60);
                    Check(f.Document.BeginCount == 1 && f.Document.EndCount == 1 && f.Document.UndoCount == 0 && f.Document.Depth == 0, "Wrong transaction counts");
                    Check(f.Document.Unit == 7 && f.Document.ReferencePoint == 9 && !f.App.Optimization, "State not restored");
                    Check(!f.Other.Deleted && f.Other.Left == 800 && f.Other.Top == 900, "Current selection was changed instead of captured source");
                    if (mode == RasterInsertionMode.ReplaceOriginal) { Check(f.A.Deleted && f.B.Deleted, "Originals not replaced"); LateDeletion(f.App.Events); }
                    else { Check(!f.A.Deleted && !f.B.Deleted, "Unexpected original deletion"); Eq(f.B.Left - f.A.Left, 60); Eq(f.B.Top - f.A.Top, -20); Eq(f.A.Left, mode == RasterInsertionMode.MoveOriginalAside ? 215 : 100); Eq(f.A.Top, 200); }
                    Check(f.Page.Layers.Count == 1, "Temporary layer leaked");
                });
            Run("Diagnostic always forces NewAside", () => { var f = New("diagnostic"); f.Save(RasterInsertionMode.ReplaceOriginal, false, true); RasterResultInsertion.Import(f.Output, "QA", RasterInsertionMode.ReplaceOriginal, f.Source); Eq(f.Imported.Left, 105); Check(!f.A.Deleted && !f.B.Deleted, "Diagnostic replaced source"); });
            foreach (string stage in new[] { "ImportEx", "FinishBefore", "FinishAfter", "SetSize", "Move", "Name", "MoveToLayer", "DeleteTemp", "DeleteTempAfter", "DeleteOriginalSecond", "CreateSelection" })
                Run("Rollback " + stage, () => {
                    var f = New(stage); f.Save(RasterInsertionMode.ReplaceOriginal); f.App.Failure = stage;
                    Throws(() => RasterResultInsertion.Import(f.Output, "QA", null, f.Source));
                    Check(f.Document.BeginCount == 1 && f.Document.EndCount == 1 && f.Document.UndoCount == 1 && f.Document.Depth == 0, "Rollback was not one ended owned group + Undo");
                    Restored(f); Check(f.App.Events.IndexOf("End") < f.App.Events.IndexOf("Undo"), "Undo before End");
                });
            Run("Rollback partial move of multiple originals", () => { var f = New("move originals"); f.Save(RasterInsertionMode.MoveOriginalAside); f.App.Failure = "MoveOriginalSecond"; Throws(() => RasterResultInsertion.Import(f.Output, "QA", null, f.Source)); Check(f.Document.UndoCount == 1, "No rollback"); Restored(f); });
            Run("CreateLayer throw never undoes previous user command", () => { var f = New("layer throw"); f.Save(RasterInsertionMode.ReplaceOriginal); f.App.Failure = "CreateLayer"; Throws(() => RasterResultInsertion.Import(f.Output, "QA", null, f.Source)); Check(f.Document.UndoCount == 0 && f.Document.EndCount == 1, "Unowned Undo"); Restored(f); });
            Run("CreateLayer null never undoes previous user command", () => { var f = New("layer null"); f.Save(RasterInsertionMode.ReplaceOriginal); f.App.Failure = "CreateLayerNull"; Throws(() => RasterResultInsertion.Import(f.Output, "QA", null, f.Source)); Check(f.Document.UndoCount == 0, "Unowned Undo after null layer"); Restored(f); });
            Run("Changed document identity rejected before mutation", () => { var f = New("wrong doc"); f.Save(RasterInsertionMode.ReplaceOriginal); var replacement = new FakeDocument(f.App, f.Document.FullFileName) { ActivePage = f.Page, ActiveLayer = f.Layer }; f.App.ActiveDocument = replacement; Throws(() => RasterResultInsertion.Import(f.Output, "QA", null, f.Source)); NoMutation(f); });
            Run("Different active page index rejected before mutation", () => { var f = New("wrong page"); f.Save(RasterInsertionMode.ReplaceOriginal); f.Document.ActivePage = new FakePage(f.Document, 2); Throws(() => RasterResultInsertion.Import(f.Output, "QA", null, f.Source)); NoMutation(f); });
            foreach (string change in new[] { "bounds", "name", "deleted" })
                Run("Destructive changed source " + change + " rejected before mutation", () => { var f = New(change); f.Save(RasterInsertionMode.ReplaceOriginal); if (change == "bounds") f.A.Left += 1; else if (change == "name") f.A.RawName = "changed"; else { f.A.Deleted = true; f.Layer.Items.Remove(f.A); } Throws(() => RasterResultInsertion.Import(f.Output, "QA", null, f.Source)); NoMutation(f); });
            Run("Live NewAside tolerates deleted source and ignores current selection", () => { var f = New("live aside"); f.Save(RasterInsertionMode.NewAside); f.A.Deleted = true; f.Layer.Items.Remove(f.A); f.SelectOther(); RasterResultInsertion.Import(f.Output, "QA", null, f.Source); Eq(f.Imported.Left, 105); Eq(f.Other.Left, 800); Check(!f.Other.Deleted, "Today's selection modified"); });
            Run("Restored destructive retry accepted with exact saved file fingerprint", () => { var f = New("restart valid"); f.Save(RasterInsertionMode.ReplaceOriginal); RasterResultInsertion.Import(f.Output, "QA"); Check(f.A.Deleted && f.B.Deleted, "Restored source not resolved"); });
            foreach (string reason in new[] { "dirty", "file changed", "unsaved" })
                Run("Restored destructive retry rejects " + reason, () => { var f = New(reason); if (reason == "unsaved") f.Source.SavedDocument = false; f.Save(RasterInsertionMode.ReplaceOriginal); if (reason == "dirty") f.Document.Dirty = true; else if (reason == "file changed") File.AppendAllText(f.Document.FullFileName, "changed"); Throws(() => RasterResultInsertion.Import(f.Output, "QA")); NoMutation(f); });
            Run("Restored NewAside remains safe without fingerprint or source objects", () => { var f = New("restart aside"); f.Source.SavedDocument = false; f.Save(RasterInsertionMode.NewAside); f.Page.HideFindResults = true; f.Document.Dirty = true; f.SelectOther(); RasterResultInsertion.Import(f.Output, "QA"); Eq(f.Imported.Left, 105); Check(!f.A.Deleted && !f.B.Deleted && !f.Other.Deleted, "Nondestructive retry changed source"); });
            Run("Failed replacement then explicit NewAside retry preserves changed originals", () => {
                var f = New("failed then safe retry"); f.Save(RasterInsertionMode.ReplaceOriginal); f.SelectOther();
                f.App.Failure = "SetSize"; Throws(() => RasterResultInsertion.Import(f.Output, "QA first attempt", null, f.Source));
                Check(f.Document.UndoCount == 1, "First failed attempt did not roll back"); Restored(f);
                f.App.Failure = ""; f.A.Deleted = true; f.Layer.Items.Remove(f.A); f.B.Left = 163; f.Document.Dirty = true;
                Check(f.Placement().Mode == RasterInsertionMode.ReplaceOriginal, "Saved mode unexpectedly changed");
                RasterResultInsertion.Import(f.Output, "QA safe retry", RasterInsertionMode.NewAside, f.Source);
                Eq(f.Imported.Left, 105); Eq(f.Imported.Top, 215); Eq(f.B.Left, 163); Eq(f.B.Top, 180); Eq(f.Other.Left, 800);
                Check(f.A.Deleted && !f.B.Deleted && !f.Other.Deleted, "Safe retry moved/deleted remaining originals or selection");
                Check(f.Document.BeginCount == 2 && f.Document.EndCount == 2 && f.Document.UndoCount == 1, "Unexpected retry Undo behavior");
                Check(f.Page.Layers.Count == 1, "Retry leaked temporary layer");
            });
            foreach (RasterInsertionMode mode in new[] { RasterInsertionMode.NewAside, RasterInsertionMode.ReplaceOriginal })
                Run("Fresh wrapper for same runtime page allowed: " + mode, () => {
                    var f = New("same page wrapper " + mode); f.Save(mode); f.Source.NativePage = new FakeCapturedPageFacade(f.Page);
                    RasterResultInsertion.Import(f.Output, "QA page wrapper", null, f.Source);
                    Eq(f.Imported.Left, mode == RasterInsertionMode.NewAside ? 105 : 95);
                    Check(f.A.Deleted == (mode == RasterInsertionMode.ReplaceOriginal), "Wrong wrapper-case insertion mode");
                });
            Run("Same native page identity permits a readable reordered page", () => {
                var f = New("same page reordered"); f.Save(RasterInsertionMode.ReplaceOriginal);
                Check(f.Source.Page == 1, "Fixture captured index changed"); f.Page.IndexValue = 2;
                RasterResultInsertion.Import(f.Output, "QA same reordered page", null, f.Source);
                Check(f.A.Deleted && f.B.Deleted, "Same original page rejected after reordering");
            });
            Run("Following page facade cannot alias a changed captured index", () => {
                var f = New("page proxy alias"); f.Save(RasterInsertionMode.ReplaceOriginal);
                Check(f.Source.Page == 1, "Fixture captured index changed");
                f.Page.IndexValue = 2; f.Source.NativePage = new FakeCapturedPageFacade(f.Page);
                Throws(() => RasterResultInsertion.Import(f.Output, "QA page alias", null, f.Source)); NoMutation(f);
            });
            Run("Deleted captured page rejects even identical object reference", () => {
                var f = New("deleted page getter"); f.Save(RasterInsertionMode.NewAside); f.Page.IndexThrows = true;
                Throws(() => RasterResultInsertion.Import(f.Output, "QA deleted page", null, f.Source)); NoMutation(f);
            });
            Run("Different captured UI thread rejected before mutation", () => {
                var f = New("wrong captured thread"); f.Save(RasterInsertionMode.NewAside); f.Source.ThreadId++;
                Throws(() => RasterResultInsertion.Import(f.Output, "QA wrong thread", null, f.Source)); NoMutation(f);
            });
            Run("Destructive lookup mutates active-page results rather than captured wrappers", () => {
                var f = New("shape wrappers"); f.Save(RasterInsertionMode.ReplaceOriginal);
                f.Source.NativeShapes[0] = new FakeCapturedShapeFacade(f.A); f.Source.NativeShapes[1] = new FakeCapturedShapeFacade(f.B);
                RasterResultInsertion.Import(f.Output, "QA shape wrappers", null, f.Source);
                Check(f.A.Deleted && f.B.Deleted, "Active-page source not replaced");
                Check(f.App.Events.Contains("Find:11") && f.App.Events.Contains("Find:12"), "StaticID lookup was skipped");
            });
            Run("Destructive lookup rejects source moved to foreign page", () => {
                var f = New("foreign original"); f.Save(RasterInsertionMode.ReplaceOriginal);
                var foreignPage = new FakePage(f.Document, 2); var foreignLayer = new FakeLayer(f.Document, foreignPage, "foreign"); foreignPage.Layers.Add(foreignLayer);
                f.Layer.Items.Remove(f.A); foreignLayer.Items.Add(f.A); f.A.Owner = foreignLayer;
                Throws(() => RasterResultInsertion.Import(f.Output, "QA moved source", null, f.Source)); NoMutation(f);
                Check(!f.A.Deleted && f.A.Owner == foreignLayer, "Foreign source changed");
            });
            Run("Destructive lookup also rejects changed captured-reference metadata", () => {
                var f = New("captured metadata"); f.Save(RasterInsertionMode.ReplaceOriginal);
                f.Source.NativeShapes[0] = new FakeCapturedShapeFacade(f.A) { NameOverride = "changed captured reference" };
                Throws(() => RasterResultInsertion.Import(f.Output, "QA metadata", null, f.Source)); NoMutation(f);
            });
            foreach (bool missing in new[] { true, false })
                Run("Destructive lookup rejects " + (missing ? "null" : "incomplete") + " captured shape refs", () => {
                    var f = New("missing captured refs " + missing); f.Save(RasterInsertionMode.ReplaceOriginal);
                    if (missing) f.Source.NativeShapes = null; else f.Source.NativeShapes.RemoveAt(1);
                    Throws(() => RasterResultInsertion.Import(f.Output, "QA missing refs", null, f.Source)); NoMutation(f);
                });
            Run("NewAside never reads source originals and uses current active layer", () => {
                var f = New("aside source not read"); f.Save(RasterInsertionMode.NewAside); f.SelectOther();
                var currentLayer = new FakeLayer(f.Document, f.Page, "current target"); f.Page.Layers.Add(currentLayer); f.Document.ActiveLayer = currentLayer;
                f.A.ReadAttempts = f.B.ReadAttempts = 0; f.A.ThrowOnRead = f.B.ThrowOnRead = true; f.Source.NativeShapes = null;
                RasterResultInsertion.Import(f.Output, "QA non-reading aside", null, f.Source);
                Eq(f.Imported.Left, 105); Eq(f.Imported.Top, 215);
                Check(f.Imported.Owner == currentLayer, "NewAside used captured layer");
                Check(f.A.ReadAttempts == 0 && f.B.ReadAttempts == 0, "NewAside accessed source properties");
                Check(!f.A.Deleted && !f.B.Deleted && !f.Other.Deleted, "NewAside altered originals or current selection");
            });
            Run("NewAside allows fresh document automation identity for exact saved path", () => {
                var captured = New("same file captured"); captured.Save(RasterInsertionMode.NewAside);
                var current = New("same file current"); current.Document.FullFileName = captured.Document.FullFileName; current.SelectOther();
                RasterResultInsertion.Import(captured.Output, "QA same-path facade", null, captured.Source);
                Eq(current.Imported.Left, 105); Eq(current.Imported.Top, 215);
                Check(current.Document.BeginCount == 1 && captured.Document.BeginCount == 0, "Wrong document was mutated");
                Check(!current.A.Deleted && !current.B.Deleted && !current.Other.Deleted && !captured.A.Deleted && !captured.B.Deleted, "NewAside changed originals");
            });
            foreach (RasterInsertionMode mode in new[] { RasterInsertionMode.ReplaceOriginal, RasterInsertionMode.MoveOriginalAside })
                Run("Same file path never relaxes destructive document identity: " + mode, () => {
                    var captured = New("strict doc captured " + mode); captured.Save(mode);
                    var current = New("strict doc current " + mode); current.Document.FullFileName = captured.Document.FullFileName;
                    Throws(() => RasterResultInsertion.Import(captured.Output, "QA strict document", null, captured.Source)); NoMutation(current); NoMutation(captured);
                });
            Run("NewAside rejects different document path despite matching page index", () => {
                var captured = New("different path captured"); captured.Save(RasterInsertionMode.NewAside);
                var current = New("different path current");
                Throws(() => RasterResultInsertion.Import(captured.Output, "QA different path", null, captured.Source)); NoMutation(current); NoMutation(captured);
            });
            Run("NewAside never matches two empty document paths", () => {
                var captured = New("empty path captured"); captured.Source.DocumentPath = ""; captured.Save(RasterInsertionMode.NewAside);
                var current = New("empty path current"); current.Document.FullFileName = "";
                Throws(() => RasterResultInsertion.Import(captured.Output, "QA empty path", null, captured.Source)); NoMutation(current); NoMutation(captured);
            });
            Run("Preexisting Optimization true restored", () => { var f = New("optimization"); f.Save(RasterInsertionMode.NewAside); f.App.Optimization = true; RasterResultInsertion.Import(f.Output, "QA", null, f.Source); Check(f.App.Optimization, "Optimization not restored"); });
            var report = new { Passed = Passed.Count, Failed = Failed.Count, PassedChecks = Passed, Failures = Failed, Runtime = Environment.Version.ToString(), Apartment = Thread.CurrentThread.GetApartmentState().ToString(), FakeOnly = true };
            string json = new JavaScriptSerializer().Serialize(report); File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "results.json"), json); Console.WriteLine(json); return Failed.Count == 0 ? 0 : 1;
        }
        private static Fixture New(string name) => new Fixture(Root, name);
        private static void Run(string name, Action check) { try { check(); Passed.Add(name); } catch (Exception error) { Failed.Add(name + ": " + error.GetBaseException().Message); } }
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Eq(double actual, double expected, double tolerance = .000001) { Check(Math.Abs(actual - expected) <= tolerance, "Expected " + expected.ToString("R", CultureInfo.InvariantCulture) + ", got " + actual.ToString("R", CultureInfo.InvariantCulture)); }
        private static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected failure"); }
        private static void NoMutation(Fixture f) { Check(f.Document.BeginCount == 0 && f.Document.UndoCount == 0 && !f.App.Events.Contains("ImportEx"), "Mutation began before validation"); }
        private static void Restored(Fixture f) { Check(!f.A.Deleted && !f.B.Deleted && !f.Other.Deleted, "Originals not restored"); Eq(f.A.Left, 100); Eq(f.B.Left, 160); Eq(f.A.Top, 200); Eq(f.B.Top, 180); Check(f.Page.Layers.Count == 1 && !f.Layer.Items.Any(shape => shape.Imported), "Imported/temp objects leaked"); Check(f.Document.Unit == 7 && f.Document.ReferencePoint == 9 && !f.App.Optimization, "App/document state not restored"); }
        private static void LateDeletion(List<string> events) { int firstDelete = events.FindIndex(value => value.StartsWith("DeleteOriginal:")); foreach (string prerequisite in new[] { "Finish", "SetSize", "MoveImported", "MoveToLayer", "DeleteTemp" }) Check(events.IndexOf(prerequisite) >= 0 && events.IndexOf(prerequisite) < firstDelete, "Deleted originals before " + prerequisite); }
        internal static void Png(string path, int width, int height, double dpiX, double dpiY)
        { byte[] pixels = new byte[width * height * 4]; for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255; var bitmap = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Bgra32, null, pixels, width * 4); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(path)) encoder.Save(stream); }
    }
}