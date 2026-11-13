using System;
using System.Linq;
using VanyaTools.Native;

internal static class Program
{
    private static int _checks;
    [STAThread] private static int Main()
    {
        try
        {
            var setup = new FrameBagSetup { Top = 400, Seat = 300, Down = 400, Head = 300, Diagonal = 500,
                Clearance = 5, Height = 100, CornerRadius = 0 };
            var frame = FrameBagGeometry.Frame(setup);
            Near(Distance(frame[0], frame[1]), 400); Near(Distance(frame[1], frame[2]), 300);
            Near(Distance(frame[2], frame[3]), 400); Near(Distance(frame[3], frame[0]), 300); Near(Distance(frame[0], frame[2]), 500);
            var plan = FrameBagGeometry.Build(setup);
            Check(plan.Parts.Length == 6 && plan.Bag.Length == 4, "Rectangle segment needs two sides and four walls.");
            Near(plan.AvailableHeight, 290); Near(plan.ActualHeight, 100); Near(FrameBagGeometry.Area(plan.Bag), 39000);
            Near(plan.VolumeLitres, 1.95);
            Near(plan.Parts[0].Seam.Max(p => p.Y), 100 * Math.Sqrt(1.01));
            Near(FrameBagGeometry.Area(plan.Parts[0].Seam), FrameBagGeometry.Area(plan.Parts[1].Seam));
            Near(FrameBagGeometry.Perimeter(plan.Parts[0].Seam), FrameBagGeometry.Perimeter(plan.Parts[1].Seam));
            WallMatches(plan);
            foreach (double radius in new[] { 0.1, 5, 15, 30 })
            {
                setup.CornerRadius = radius;
                plan = FrameBagGeometry.Build(setup);
                Near(FrameBagGeometry.Perimeter(plan.Bag), 980 - 8 * radius + 2 * Math.PI * radius, .06);
                Near(FrameBagGeometry.Area(plan.Bag), 39000 - (4 - Math.PI) * radius * radius, 2);
                WallMatches(plan);
                Check(plan.Parts.All(part => FrameBagGeometry.Area(part.Cut) > FrameBagGeometry.Area(part.Seam)), "Allowance shrank a part.");
                Check(plan.Bag.All(p => p.X >= 5 - 1e-8 && p.X <= 395 + 1e-8 && p.Y >= 5 - 1e-8 && p.Y <= 105 + 1e-8),
                    "Rounding extends outside the selected segment.");
            }
            var original = new FrameBagSetup();
            plan = FrameBagGeometry.Build(original); WallMatches(plan);
            Check(plan.Breaks.Length == 5 && plan.Parts.Length == 7, "A segment below the head corner must retain its fifth edge.");
            original.TopDepth = 20; original.BottomDepth = 80;
            WallMatches(FrameBagGeometry.Build(original));
            original.TopDepth = original.BottomDepth = 50;
            plan = FrameBagGeometry.Build(original); WallMatches(plan);
            Near(plan.Parts[0].Seam.Max(p => p.Y) - plan.Parts[0].Seam.Min(p => p.Y), plan.ActualHeight);
            Check(plan.Parts.All(part => part.Cut.All(p => !Double.IsNaN(p.X + p.Y))), "Non-finite cut points.");
            foreach (var part in plan.Parts)
            {
                Check(part.Cut.Length >= 3 && FrameBagGeometry.Area(part.Cut) > 0, "Cut must be one positive closed contour.");
                Check(part.Seam.All(p => Inside(part.Cut, p)), "A seam point falls outside its cutting contour.");
            }
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            var restored = serializer.Deserialize<FrameBagSetup>(serializer.Serialize(original));
            Near(restored.CornerRadius, original.CornerRadius); Near(restored.Diagonal, original.Diagonal);
            Near(restored.TopDepth, original.TopDepth); Near(restored.BottomDepth, original.BottomDepth);
            WallMatches(FrameBagGeometry.Build(restored));
            foreach(var culture in new[] { "en-US", "ru-RU" })
            {
                var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
                try
                {
                    System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
                    Dxf(plan);
                }
                finally { System.Threading.Thread.CurrentThread.CurrentCulture = previous; }
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for(int n=0;n<40;n++)
            {
                original.Height=100+n*4;original.CornerRadius=n%25;original.TopDepth=20+n*2;
                var varied=FrameBagGeometry.Build(original);WallMatches(varied);
                Check(varied.Parts.All(part => part.Seam.All(point => Inside(part.Cut,point))),
                    "A varying-radius/taper seam escaped its allowance.");
            }
            Console.WriteLine("40 interactive geometry rebuilds: " + clock.ElapsedMilliseconds + " ms.");
            foreach (BagZipper left in Enum.GetValues(typeof(BagZipper)))
            foreach (BagZipper right in Enum.GetValues(typeof(BagZipper)))
            {
                var zipSetup = new FrameBagSetup { LeftZipper = left, RightZipper = right };
                var closed = FrameBagGeometry.Build(zipSetup);
                int expected = closed.Breaks.Length + (left == BagZipper.Split ? 2 : 1) + (right == BagZipper.Split ? 2 : 1) +
                    (left == BagZipper.Flap ? 1 : 0) + (right == BagZipper.Flap ? 1 : 0);
                Check(closed.Parts.Length == expected, "A closure lost or fabricated a piece.");
                int holes = (left == BagZipper.Slot || left == BagZipper.Flap ? 1 : 0) +
                    (right == BagZipper.Slot || right == BagZipper.Flap ? 1 : 0);
                Check(closed.Parts.Sum(part => part.Cutouts.Length) == holes, "Missing zipper slot.");
                foreach(var part in closed.Parts)
                    Check(part.Cutouts.All(hole => hole.All(point => FrameBagClosures.Inside(part.Seam, point))),
                        "A zipper slot leaves its own side.");
                WallMatches(closed); Dxf(closed); InsertChecks.Run(closed);
            }
            foreach(double front in new[]{120.0,220.0,330.0})
            {
                var sloped = FrameBagGeometry.Build(new FrameBagSetup { SplitHeights = true, Height = 160, FrontHeight = front,
                    LeftZipper = BagZipper.Slot, RightZipper = BagZipper.Split });
                WallMatches(sloped); Dxf(sloped);
                Check(sloped.Setup.FrontHeight == front, "Independent front height was ignored.");
            }
            var badZip = new FrameBagSetup { LeftZipper = BagZipper.Slot, LeftZipAX = 0, LeftZipAY = 0 };
            Reject(() => FrameBagGeometry.Build(badZip));
            Check(FrameBagGeometry.Build(badZip, false).Bag.Length > 3, "Invalid zipper blocked the editable body preview.");
            var reversed = new FrameBagSetup { LeftZipper = BagZipper.Split, LeftZipAX = 87, LeftZipBX = 12,
                LeftZipAY = 30, LeftZipBY = 20 };
            WallMatches(FrameBagGeometry.Build(reversed));
            var sample = FrameBagGeometry.Build(new FrameBagSetup { LeftZipper = BagZipper.Flap, RightZipper = BagZipper.Split,
                SplitHeights = true, Height = 130, FrontHeight = 210 });
            string folder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "artifacts");
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "frame-bag.dxf"), FrameBagDxf.Create(sample), System.Text.Encoding.ASCII);

            var triangleSetup = new FrameBagSetup { TriangleFrame = true, Top = 400, Seat = 300, Down = 500,
                Head = Double.NaN, Diagonal = Double.PositiveInfinity, Height = 100, CornerRadius = 10 };
            var triangle = FrameBagGeometry.Frame(triangleSetup);
            Check(triangle.Length == 3, "Triangle mode retained a fake head edge.");
            Near(Distance(triangle[0],triangle[1]),400);Near(Distance(triangle[1],triangle[2]),500);Near(Distance(triangle[2],triangle[0]),300);
            var trianglePlan=FrameBagGeometry.Build(triangleSetup);
            Check(trianglePlan.Breaks.Length==4 && trianglePlan.Parts.Length==6,"A partial triangle needs a clipped four-corner bag.");
            WallMatches(trianglePlan);Dxf(trianglePlan);InsertChecks.Run(trianglePlan);
            triangleSetup.Height=trianglePlan.AvailableHeight;triangleSetup.CornerRadius=5;
            var fullTriangle=FrameBagGeometry.Build(triangleSetup);
            Check(fullTriangle.Breaks.Length==3 && fullTriangle.Parts.Length==5,"Full triangular bag gained a zero-length fourth wall.");
            WallMatches(fullTriangle);Dxf(fullTriangle);
            foreach(BagZipper left in Enum.GetValues(typeof(BagZipper)))
            foreach(BagZipper right in Enum.GetValues(typeof(BagZipper)))
            {
                var source = new FrameBagSetup { TriangleFrame=true, LeftZipper=left, RightZipper=right,
                    SplitHeights=true,Height=140,FrontHeight=210 };
                var tri=FrameBagGeometry.Build(source);WallMatches(tri);Dxf(tri);InsertChecks.Run(tri);
                Check(tri.Frame.Length==3 && tri.Setup.TriangleFrame,"Triangle mode lost while applying closures.");
                var copy=serializer.Deserialize<FrameBagSetup>(serializer.Serialize(source));
                Check(copy.TriangleFrame && FrameBagGeometry.Build(copy).Frame.Length==3,"Saved measurements forgot the frame type.");
            }
            var oldPreset=serializer.Deserialize<FrameBagSetup>("{\"Top\":550,\"Seat\":461,\"Down\":594.1,\"Head\":104.4,\"Diagonal\":588.6}");
            Check(!oldPreset.TriangleFrame && FrameBagGeometry.Frame(oldPreset).Length==4,"Old presets changed their frame type.");
            var badTriangle = new FrameBagSetup {TriangleFrame=true,Top=100,Seat=100,Down=300};
            Reject(()=>FrameBagGeometry.Build(badTriangle));
            var triangleSample=FrameBagGeometry.Build(new FrameBagSetup {TriangleFrame=true,LeftZipper=BagZipper.Flap,RightZipper=BagZipper.Split});
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder,"frame-bag-triangle.dxf"),FrameBagDxf.Create(triangleSample),System.Text.Encoding.ASCII);

            InsertChecks.Run(plan);
            var invalid = new FrameBagSetup { Diagonal = 20 }; Reject(() => FrameBagGeometry.Build(invalid));
            invalid = new FrameBagSetup { Height = 2000 }; Reject(() => FrameBagGeometry.Build(invalid));
            invalid = new FrameBagSetup { CornerRadius = 200 }; Reject(() => FrameBagGeometry.Build(invalid));
            invalid = new FrameBagSetup { Clearance = 100, Height = 10 }; Reject(() => FrameBagGeometry.Build(invalid));
            invalid = new FrameBagSetup { TopDepth = Double.NaN }; Reject(() => FrameBagGeometry.Build(invalid));
            Console.WriteLine("Frame bag checks passed: " + _checks + ". Geometry and matching seam lengths; no live Corel.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Dxf(FrameBagPlan plan)
    {
        string text = FrameBagDxf.Create(plan);
        var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
        Check(lines.Length % 2 == 1 && lines[lines.Length - 2] == "EOF", "Incomplete DXF.");
        Check(text.Contains("$INSUNITS\r\n70\r\n4\r\n"), "DXF must declare millimetres.");
        int piece=0;
        var expected=plan.Parts.SelectMany(part=>new[]{part.Cut}.Concat(part.Cutouts).Select(nodes=>Tuple.Create(part,nodes))).ToArray();
        for(int i=0;i+1<lines.Length;i+=2)
        {
            if(lines[i]!="0" || lines[i+1]!="LWPOLYLINE")continue;
            int end=i+2;while(end+1<lines.Length && lines[end]!="0")end+=2;
            var pairs=Enumerable.Range(0,(end-i)/2).Select(n=>Tuple.Create(lines[i+n*2],lines[i+n*2+1])).ToArray();
            var pair=expected[piece++];var part=pair.Item1;var nodes=pair.Item2;
            Check(pairs.Single(p=>p.Item1=="70").Item2=="1","Open cutting path.");
            Check(Int32.Parse(pairs.Single(p=>p.Item1=="90").Item2)==nodes.Length,"DXF vertex count changed.");
            var xs=pairs.Where(p=>p.Item1=="10").Select(p=>Double.Parse(p.Item2,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var ys=pairs.Where(p=>p.Item1=="20").Select(p=>Double.Parse(p.Item2,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Near(xs.Max()-xs.Min(),nodes.Max(p=>p.X)-nodes.Min(p=>p.X),.00001);Near(ys.Max()-ys.Min(),nodes.Max(p=>p.Y)-nodes.Min(p=>p.Y),.00001);
            Check(xs.Min()>=0 && ys.Min()>=0,"Negative layout origin.");
        }
        Check(piece==expected.Length && !text.Contains("\r\nTEXT\r\n"),"DXF lost a piece or included guide text.");
    }
    private static void WallMatches(FrameBagPlan plan)
    {
        double scale = Math.Sqrt(1 + Math.Pow((plan.Setup.BottomDepth - plan.Setup.TopDepth) / plan.ActualHeight / 2, 2));
        double total = 0;
        for (int n = 0; n < plan.Breaks.Length; n++)
        {
            int index = plan.Breaks[n], last = plan.Breaks[(n + 1) % plan.Breaks.Length];
            double side = 0;
            do
            {
                int next = (index + 1) % plan.Bag.Length;
                var delta = BagPoint.Sub(plan.Bag[next], plan.Bag[index]);
                side += Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y * scale * scale);
                index = next;
            } while(index != last);
            var wall = plan.Parts.Single(p => p.Edge == n).Seam;
            int half = wall.Length / 2;
            double left = Enumerable.Range(0, half - 1).Sum(i => Distance(wall[i], wall[i + 1]));
            double right = Enumerable.Range(half, half - 1).Sum(i => Distance(wall[i], wall[i + 1]));
            Near(left, side); Near(right, side);
            var start = plan.Bag[plan.Breaks[n]];
            double depth = plan.Setup.TopDepth + (plan.Setup.BottomDepth - plan.Setup.TopDepth) *
                (start.Y - plan.Bag.Min(p => p.Y)) / plan.ActualHeight;
            Near(Distance(wall[0], wall[wall.Length - 1]), depth);
            total += side;
        }
        Near(total, FrameBagGeometry.Perimeter(plan.SideSeam));
    }
    private static bool Inside(BagPoint[] polygon, BagPoint p)
    {
        bool inside = false;
        for(int i=0,j=polygon.Length-1;i<polygon.Length;j=i++)
            if((polygon[i].Y>p.Y)!=(polygon[j].Y>p.Y) &&
                p.X < (polygon[j].X-polygon[i].X)*(p.Y-polygon[i].Y)/(polygon[j].Y-polygon[i].Y)+polygon[i].X) inside=!inside;
        return inside;
    }
    private static double Distance(BagPoint a, BagPoint b) => BagPoint.Sub(b,a).Length;
    private static void Near(double a, double b, double tolerance = .00001) => Check(Math.Abs(a-b)<=tolerance, "Mismatch " + a + " / " + b);
    private static void Check(bool ok, string message) { _checks++; if(!ok)throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch(ArgumentException) { _checks++; return; } throw new Exception("Invalid measurements accepted."); }
}
