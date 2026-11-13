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

            DetailChecks();
            WindowChecks.Run();
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
    private static void DetailChecks()
    {
        var setup = new FrameBagSetup { Top = 400, Seat = 300, Down = 400, Head = 300, Diagonal = 500,
            Clearance = 5, Height = 100, CornerRadius = 0, CornerRadii = new[] { 0d, 4, 12, 25 } };
        var rounded = FrameBagGeometry.Build(setup);
        Near(FrameBagGeometry.Area(rounded.Bag), 39000 - (1 - Math.PI / 4) * setup.CornerRadii.Sum(r => r * r), 1);
        WallMatches(rounded); NotchMatches(rounded); Dxf(rounded); InsertChecks.Run(rounded);
        var copy = setup.Copy(); copy.CornerRadii[0] = 80;
        Check(setup.CornerRadii[0] == 0, "Editing a saved radius mutated another setup.");
        copy.CornerRadii = new[] { 80d, 0, 0, 0 };
        WallMatches(FrameBagGeometry.Build(copy)); // One large radius may exceed the shared-radius limit.
        copy.CornerRadii = new[] { 100d, 100, 100, 100 };
        Reject(() => FrameBagGeometry.Build(copy));
        copy.CornerRadii[0] = Double.NaN; Reject(() => FrameBagGeometry.Build(copy));
        var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
        var restored = serializer.Deserialize<FrameBagSetup>(serializer.Serialize(setup));
        Check(restored.CornerRadii.SequenceEqual(setup.CornerRadii), "Individual corner radii were lost in preset JSON.");

        var zipper = FrameBagGeometry.Build(new FrameBagSetup { LeftZipper = BagZipper.Slot, RightZipper = BagZipper.Flap });
        Check(zipper.Setup.FixedZipperAnchors, "Legacy percentage anchors were not migrated.");
        var anchors = Enumerable.Range(0, 4).Select(i => FrameBagClosures.Anchor(zipper, i / 2, i % 2)).ToArray();
        var taller = zipper.Setup.Copy(); taller.Height = 260; taller.FrontHeight = 220; taller.SplitHeights = true;
        taller.CornerRadii = new[] { 5d, 20, 8, 12, 6 };
        var resized = FrameBagGeometry.Build(taller);
        for (int n = 0; n < 4; n++) Near(Distance(anchors[n], FrameBagClosures.Anchor(resized, n / 2, n % 2)), 0);
        var reload = serializer.Deserialize<FrameBagSetup>(serializer.Serialize(resized.Setup));
        var again = FrameBagGeometry.Build(reload);
        for (int n = 0; n < 4; n++) Near(Distance(anchors[n], FrameBagClosures.Anchor(again, n / 2, n % 2)), 0);
        var tooShort = resized.Setup.Copy(); tooShort.Height = tooShort.FrontHeight = 30;
        tooShort.CornerRadii = new[] { 0d, 0, 0, 0, 0 }; tooShort.CornerRadius = 0;
        Reject(() => FrameBagGeometry.Build(tooShort));
        var editable = FrameBagGeometry.Build(tooShort, false);
        Near(Distance(anchors[0], FrameBagClosures.Anchor(editable, 0, 0)), 0);
        Check(!new FrameBagSetup().FixedZipperAnchors, "Migration altered source defaults in place.");

        foreach (bool triangle in new[] { false, true })
        foreach (BagZipper left in Enum.GetValues(typeof(BagZipper)))
        foreach (BagZipper right in Enum.GetValues(typeof(BagZipper)))
        {
            var source = new FrameBagSetup { TriangleFrame = triangle, LeftZipper = left, RightZipper = right,
                SplitHeights = true, Height = 140, FrontHeight = 230, CornerRadii = new[] { 0d, 12, 25, 6, 9 } };
            var notched = FrameBagGeometry.Build(source); NotchMatches(notched); WallMatches(notched);
            var plainSource = source.Copy(); plainSource.Notches = false;
            var plain = FrameBagGeometry.Build(plainSource);
            Check(plain.Parts.All(part => part.Notches.Length == 0), "Disabled notches changed cut outlines.");
            for (int n = 0; n < plain.Parts.Length; n++)
            {
                Near(FrameBagGeometry.Area(plain.Parts[n].Seam), FrameBagGeometry.Area(notched.Parts[n].Seam));
                if (notched.Parts[n].Notches.Length != 0)
                    Check(FrameBagGeometry.Area(notched.Parts[n].Cut) < FrameBagGeometry.Area(plain.Parts[n].Cut), "A notch was not cut out of the allowance.");
            }
        }
        foreach (double allowance in new[] { 0d, .3, .5, 1, 10 })
        {
            var small = FrameBagGeometry.Build(new FrameBagSetup { Allowance = allowance, NotchDepth = 5 });
            if (allowance < .4) Check(small.Parts.All(part => part.Notches.Length == 0), "A zero/tiny allowance was notched.");
            else NotchMatches(small);
        }
        Reject(() => FrameBagGeometry.Build(new FrameBagSetup { NotchDepth = -1 }));
    }

    private static void NotchMatches(FrameBagPlan plan)
    {
        foreach (var part in plan.Parts)
        {
            Check(FrameBagGeometry.Area(part.Cut) > 0, "Notched outline changed orientation.");
            Check(part.Cut.All(p => !Double.IsNaN(p.X + p.Y)), "Non-finite notch.");
            Check(!CrossesItself(part.Cut), "A V-notch created a self-intersecting cutting outline.");
            foreach (var mark in part.Notches)
            {
                Check(part.Cut.Any(point => Distance(point, mark.Tip) < 1e-7), "Notch tip absent from cutting path.");
                Check(!FrameBagClosures.Inside(part.Seam, mark.Tip), "Notch reaches the sewing line.");
                Check(part.Seam.All(point => FrameBagClosures.Inside(part.Cut, point)), "A notch damaged the seam.");
            }
        }
        for (int edge = 0; edge < plan.Breaks.Length; edge++)
        {
            var path = new System.Collections.Generic.List<BagPoint>();
            int i = plan.Breaks[edge], last = plan.Breaks[(edge + 1) % plan.Breaks.Length];
            while (true) { path.Add(plan.SideSeam[i]); if (i == last) break; i = (i + 1) % plan.SideSeam.Length; }
            var wall = plan.Parts.Single(part => part.Edge == edge);
            foreach (var side in plan.Parts.Where(part => part.Side >= 0))
            foreach (var mark in side.Notches.Where(mark => mark.Edge == edge))
            {
                bool upper = side.Side == 0;
                var matching = wall.Notches.Single(mark2 => mark2.Index == mark.Index && (upper ? mark2.SeamPoint.Y < 0 : mark2.SeamPoint.Y > 0));
                var seamPoint = new BagPoint(side.Side == 0 ? mark.SeamPoint.X : -mark.SeamPoint.X, mark.SeamPoint.Y);
                var wallPath = upper ? wall.Seam.Take(wall.Seam.Length / 2).ToArray() : wall.Seam.Skip(wall.Seam.Length / 2).Reverse().ToArray();
                Near(Along(path.ToArray(), seamPoint), Along(wallPath, matching.SeamPoint), .001);
            }
            Check(wall.Notches.Length == plan.Parts.Where(part => part.Side >= 0).Sum(part => part.Notches.Count(mark => mark.Edge == edge)), "Unpaired side/wall notch.");
            if (FrameBagGeometry.Perimeter(plan.SideSeam) > 100 && path.Zip(path.Skip(1), Distance).Sum() > 30)
                Check(wall.Notches.Length >= 2, "A normal-sized wall has no orientation marks.");
        }
    }
    private static double Along(BagPoint[] path, BagPoint point)
    {
        double length = 0;
        for (int i = 0; i + 1 < path.Length; i++)
        {
            var d = BagPoint.Sub(path[i + 1], path[i]); var r = BagPoint.Sub(point, path[i]);
            double t = (r.X * d.X + r.Y * d.Y) / (d.X * d.X + d.Y * d.Y);
            if (t >= -.00001 && t <= 1.00001 && Distance(point, BagPoint.Add(path[i], BagPoint.Mul(d, t))) < .00001)
                return length + t * d.Length;
            length += d.Length;
        }
        throw new Exception("Matching notch is not on its developed seam.");
    }
    private static bool CrossesItself(BagPoint[] points)
    {
        for (int a = 0; a < points.Length; a++)
        for (int b = a + 2; b < points.Length; b++)
        {
            if (a == 0 && b == points.Length - 1) continue;
            var u = BagPoint.Sub(points[(a + 1) % points.Length], points[a]);
            var v = BagPoint.Sub(points[(b + 1) % points.Length], points[b]);
            var d = BagPoint.Sub(points[b], points[a]); double cross = BagPoint.Cross(u, v);
            if (Math.Abs(cross) < 1e-10) continue;
            double t = BagPoint.Cross(d, v) / cross, q = BagPoint.Cross(d, u) / cross;
            if (t > .000001 && t < .999999 && q > .000001 && q < .999999) return true;
        }
        return false;
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
