using System;
using System.IO;
using System.Text;
using MNBL.ProceduralWorld.Diagnostics;

internal static class HeightfieldTests
{
    private static void Equal(double actual, double expected)
    {
        if (Math.Abs(actual - expected) > 1e-9) throw new Exception(actual + " != " + expected);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidDataException) { return; }
        catch (EndOfStreamException) { return; }
        throw new Exception("Invalid input accepted");
    }

    private static void Main(string[] args)
    {
        var path = Path.Combine(args[0], "asymmetric.bin");
        Directory.CreateDirectory(args[0]);
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(Encoding.ASCII.GetBytes("MNBLHF01"));
            writer.Write(3);
            foreach (double h in new double[] {1,2,4,3,8,5,6,7,9}) writer.Write(h);
        }
        var grid = DiagnosticHeightfield.Read(path);
        Equal(grid.Sample(.25,.25), 1.75);
        Equal(grid.Sample(.75,.75), 5.25);
        Equal(grid.Sample(1,1), 8);
        Equal(grid.Sample(2,2), 9);
        double t;
        if (!grid.Intersect(.25,.25,10,.25,.25,-10,out t)) throw new Exception("Downward ray missed");
        Equal(t,.4125);
        if (!grid.Intersect(.25,.25,-10,.25,.25,10,out t)) throw new Exception("Upward ray missed");
        Equal(t,.5875);
        if (!grid.Intersect(0,0,10,2,2,-10,out t)) throw new Exception("Oblique ray missed");
        Equal(t,13.0/42);
        if (!grid.Intersect(0,1,5,2,1,5,out t)) throw new Exception("Horizontal ray missed ridge");
        Equal(t,.2);
        if (grid.Intersect(0,0,10,2,2,10,out t)) throw new Exception("Above-grid ray hit");
        if (grid.Intersect(3,3,10,3,3,-10,out t)) throw new Exception("Outside-grid ray hit");
        if (grid.Intersect(1,1,20,1,1,19,out t)) throw new Exception("Ray hit beyond segment");
        Reject(() => grid.Sample(-.1,1));
        Reject(() => grid.Sample(double.NaN,1));
        Reject(() => grid.Intersect(0,0,0,double.PositiveInfinity,0,0,out t));
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, new byte[3]);
        Reject(() => DiagnosticHeightfield.Read(path));
        File.WriteAllBytes(path, bytes);
        using (var stream = File.OpenWrite(path)) { stream.Position = 12; stream.Write(BitConverter.GetBytes(double.NaN),0,8); }
        Reject(() => DiagnosticHeightfield.Read(path));
        File.WriteAllBytes(path, bytes);
        using (var stream = File.OpenWrite(path)) { stream.Position = 8; stream.Write(BitConverter.GetBytes(int.MaxValue),0,4); }
        Reject(() => DiagnosticHeightfield.Read(path));
        File.WriteAllBytes(path, bytes);
        using (var stream = new FileStream(path, FileMode.Append)) stream.WriteByte(0);
        Reject(() => DiagnosticHeightfield.Read(path));
        Console.WriteLine("PASS: asymmetric surface sampling; downward/upward/oblique/nearest segment intersections; bounds, nonfinite and corrupt-grid refusal.");
    }
}
