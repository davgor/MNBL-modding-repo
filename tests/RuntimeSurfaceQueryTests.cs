using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using Collision = MNBL.ProceduralWorld.Diagnostics.RuntimeSurfaceQueries.CollisionReference;

// Managed geometry/serialization only. No Scene, SceneView, Camera or engine method is invoked.
internal static class RuntimeSurfaceQueryTests
{
    private static int checks;
    public static int Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 2) throw new ArgumentException("Installed shipping assembly directory and optional saved runtime receipt required.");
        AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => {
            string path = Path.Combine(args[0], new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        int result = Run();
        if (args.Length == 2) VerifySavedRays(args[1]);
        return result;
    }

    private static int Run()
    {
        string path = Path.Combine(Path.GetTempPath(), "mnbl-query-math-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(Encoding.ASCII.GetBytes("MNBLHF01")); w.Write(17);
                for (int y = 0; y < 17; y++) for (int x = 0; x < 17; x++) w.Write(2 + .02 * (x * 16) + .05 * (y * 16));
            }
            var f = MNBL.ProceduralWorld.Diagnostics.DiagnosticHeightfield.Read(path);
            var type = typeof(MNBL.ProceduralWorld.Diagnostics.RuntimeSurfaceQueries);
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            var sample = type.GetMethod("Sample", flags);
            double h = (double)sample.Invoke(null, new object[] { f, 256f, 128d, 64d });
            Check(Math.Abs(h - 7.76) < 1e-9, "World-to-grid scaling at interior point");
            h = (double)sample.Invoke(null, new object[] { f, 256f, 256d, 256d });
            Check(Math.Abs(h - 19.92) < 1e-9, "Far corner remains in source domain");
            var normal = (TaleWorlds.Library.Vec3)type.GetMethod("GeometricNormal", flags).Invoke(null,
                new object[] { f, 256f, new TaleWorlds.Library.Vec2(128, 64) });
            double norm = Math.Sqrt(1 + .02 * .02 + .05 * .05);
            Check(Math.Abs(normal.x + .02 / norm) < 1e-6 && Math.Abs(normal.y + .05 / norm) < 1e-6
                && Math.Abs(normal.z - 1 / norm) < 1e-6, "World-space slope normal uses metre spacing");
            var planar = type.GetMethod("PlanarReference", flags);
            foreach (var point in new[] { new TaleWorlds.Library.Vec2(20, 210), new TaleWorlds.Library.Vec2(20.5f, 210.5f), new TaleWorlds.Library.Vec2(21.5f, 210.5f) })
            {
                object[] values = { f, 256f, point, .001d, 0d, 0d, 0d };
                Check((bool)planar.Invoke(null, values), "Independent plane gate accepts analytic plane");
                Check((double)values[4] < 1e-12 && (double)values[6] < 1e-12, "Plane errors use world-space slopes and height");
            }
            object[] wrongScale = { f, 800f, new TaleWorlds.Library.Vec2(100, 210), .001d, 0d, 0d, 0d };
            Check(!(bool)planar.Invoke(null, wrongScale), "Plane gate rejects incorrect extent/slope");
            object[] edge = { f, 256f, new TaleWorlds.Library.Vec2(0, 0), .001d, 0d, 0d, 0d };
            Check(!(bool)planar.Invoke(null, edge), "Plane gate rejects incomplete edge neighbourhood");
            var landmarks = type.GetMethod("Landmarks", flags);
            foreach (float extent in new[] { 256f, 800f, 640f })
            {
                int count = 0;
                foreach (KeyValuePair<string, TaleWorlds.Library.Vec2> p in (IEnumerable)landmarks.Invoke(null, new object[] { 129, extent }))
                {
                    double x = p.Value.x * 128d / extent, y = p.Value.y * 128d / extent;
                    if (p.Key.StartsWith("prospective_grid_", StringComparison.Ordinal))
                        Check((Math.Abs(x - 50.25) < .0001 && Math.Abs(y - 64.6) < .0001)
                            || (Math.Abs(x - 80.75) < .0001 && Math.Abs(y - 40.2) < .0001), "Preselected nonvertex landmarks retain grid coordinates at each extent");
                    else Check(x > 0 && x < 128 && y > 0 && y < 128 && Math.Abs(x - Math.Round(x)) < 1e-6
                        && Math.Abs(y - Math.Round(y)) < 1e-6, "Landmark is an inset quantized grid node"); count++;
                }
                Check(count == (extent == 256 ? 12 : 9), "Calibration and source landmark counts");
            }
            object nan = type.GetMethod("N", flags).Invoke(null, new object[] { double.NaN });
            object infinity = type.GetMethod("N", flags).Invoke(null, new object[] { double.PositiveInfinity });
            var json = new JavaScriptSerializer();
            string encoded = json.Serialize(new { nan, infinity });
            var decoded = json.Deserialize<Dictionary<string, object>>(encoded);
            Check(decoded["nan"] is string && decoded["infinity"] is string, "Nonfinite native results remain valid JSON strings");
            bool failed = false;
            try { MNBL.ProceduralWorld.Diagnostics.RuntimeSurfaceQueries.RequirePassed(new Dictionary<string, object> { { "passed", false } }); }
            catch (InvalidOperationException) { failed = true; }
            Check(failed, "Persisted failed report cannot be accepted by RequirePassed");
            MNBL.ProceduralWorld.Diagnostics.RuntimeSurfaceQueries.RequirePassed(new Dictionary<string, object> { { "passed", true } });
            Check(Math.Abs(Collision.Quantize(29.215401235980778) - 29.19) < 1e-12, "Positive collision vertex truncation");
            Check(Math.Abs(Collision.Quantize(-23.178153978789958) + 23.16) < 1e-12, "Negative collision vertex truncates toward zero, not floor");
            Check(Collision.Quantize(.03 - 1e-10) == 0 && Collision.Quantize(-.03 + 1e-10) == 0, "Inside positive and negative bin edges");
            Check(Math.Abs(Collision.Quantize(.03 + 1e-10) - .03) < 1e-12
                && Math.Abs(Collision.Quantize(-.03 - 1e-10) + .03) < 1e-12, "Outside bin edges follow explicit double model, not asserted engine float rounding");
            var quad = Fixture(path, 3, (x, y) => x == 0 && y == 0 ? .01 : x == 1 && y == 0 ? .04 : x == 0 && y == 1 ? .07 : .19);
            var collision = new Collision(quad, 2);
            Check(Math.Abs(collision.Sample(.75, .25) - .06) < 1e-12, "Main-diagonal lower triangle uses snapped vertices");
            Check(Math.Abs(collision.Sample(.25, .75) - .075) < 1e-12, "Main-diagonal upper triangle uses snapped vertices");
            double main = (double)type.GetMethod("NativeSourceSampleMain", flags).Invoke(null, new object[] { quad, 2f, .75d, .25d });
            Check(Math.Abs(main - .07) < 1e-12, "Untruncated source main triangle remains distinct from collision");
            Check(Math.Abs(quad.Sample(.75, .25) - .0475) < 1e-12 && quad.At(1, 1) == .19, "Shared source interpolation and vertices remain unchanged");
            double fraction;
            Check(collision.Intersect(new TaleWorlds.Library.Vec3(.75f, .25f, 1), new TaleWorlds.Library.Vec3(.75f, .25f, -1), out fraction)
                && Math.Abs(fraction - .47) < 1e-12, "Ray intersects the main-diagonal snapped surface");
            var waves = Fixture(path, 5, (x, y) => x == 1 ? .91 : x == 3 ? .61 : .001);
            collision = new Collision(waves, 4);
            Check(collision.Intersect(new TaleWorlds.Library.Vec3(0, .4f, .3f), new TaleWorlds.Library.Vec3(4, .4f, .3f), out fraction)
                && Math.Abs(fraction - 1.0 / 12) < 1e-7, "Multiple crossings select earliest forward hit");
            Check(collision.Intersect(new TaleWorlds.Library.Vec3(4, .4f, .3f), new TaleWorlds.Library.Vec3(0, .4f, .3f), out fraction)
                && Math.Abs(fraction - .125) < 1e-7, "Reverse ray selects its own earliest hit, not triangle iteration order");
            Check(!collision.Intersect(new TaleWorlds.Library.Vec3(0, 5, .3f), new TaleWorlds.Library.Vec3(4, 5, .3f), out fraction), "Outside-extent segment misses");
            Console.WriteLine("PASS " + checks + " managed checks; no engine/native calls.");
            return 0;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    private static MNBL.ProceduralWorld.Diagnostics.DiagnosticHeightfield Fixture(string path, int side, Func<int, int, double> height)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(Encoding.ASCII.GetBytes("MNBLHF01")); w.Write(side);
            for (int y = 0; y < side; y++) for (int x = 0; x < side; x++) w.Write(height(x, y));
        }
        return MNBL.ProceduralWorld.Diagnostics.DiagnosticHeightfield.Read(path);
    }
    private static void VerifySavedRays(string path)
    {
        var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var receipt = (Dictionary<string, object>)json.DeserializeObject(File.ReadAllText(path));
        var outputs = (Dictionary<string, object>)receipt["outputs"];
        int rays = 0, hits = 0; double maximum = 0;
        foreach (Dictionary<string, object> stage in (object[])outputs["stages"])
        {
            var field = MNBL.ProceduralWorld.Diagnostics.DiagnosticHeightfield.Read(Path.Combine((string)stage["jobRoot"], "surface", "quantized-heightfield.bin"));
            var reference = new Collision(field, Convert.ToSingle(stage["extent"]));
            var groups = new List<object>((object[])stage["measurements"]);
            groups.AddRange((object[])stage["cursorPicks"]);
            foreach (Dictionary<string, object> group in groups)
                foreach (Dictionary<string, object> row in (object[])group["measurements"])
                {
                    if ((string)row["operation"] != "ray") continue;
                    var a = Vector((object[])row["start"]); var b = Vector((object[])row["end"]);
                    double fraction; bool hit = reference.Intersect(a, b, out fraction); rays++;
                    Check(hit == (bool)row["hit"], "Saved native hit/miss agrees with independent collision field");
                    if (!hit) continue;
                    hits++;
                    double dx = (double)b.x - a.x, dy = (double)b.y - a.y, dz = (double)b.z - a.z;
                    double error = Math.Abs(fraction * Math.Sqrt(dx * dx + dy * dy + dz * dz) - Convert.ToDouble(row["distance"]));
                    maximum = Math.Max(maximum, error);
                    Check(error <= .001, "Saved ray regression within 1mm; prospective runtime tolerance remains 0.1m");
                }
        }
        Console.WriteLine("PASS saved receipt: " + rays + " rays, " + hits + " hits, maximum distance error "
            + maximum.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " m; " + checks + " total managed checks; no native calls or historical result rewrite.");
    }
    private static TaleWorlds.Library.Vec3 Vector(object[] v) { return new TaleWorlds.Library.Vec3(Convert.ToSingle(v[0]), Convert.ToSingle(v[1]), Convert.ToSingle(v[2])); }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
}
