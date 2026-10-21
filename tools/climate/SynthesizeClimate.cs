using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using MNBL.ProceduralWorld.Surface;

// MMR-39: writes a periodic fixture's synthesized climate (GeneratedClimateSystem) next to a copy of its placement:
//   SynthesizeClimate SOURCE_PLACEMENT WORLD_JSON OUT_DIR POLICY
// OUT_DIR receives placement.json (the source placement plus a mnbl.generated-climate.v2 block), climate-wind.f32 and
// climate-precipitation.u8 in SandBox.MapScene's layouts, climate-colorgrade.u8 in MapColorGradeManager's, climate-snow.rgba (MMR-256: the
// terrain snow texture, 512 x 512 RGBA), and the source placement's
// build receipt. The in-game binding reads
// them exactly as MMR-59's files (GeneratedClimate.FromPlacement), and checks the v2 identity against the files.
internal static class SynthesizeClimate
{
    private static int Main(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("usage: SynthesizeClimate SOURCE_PLACEMENT WORLD_JSON OUT_DIR POLICY"); return 2; }
        var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        var placement = (Dictionary<string, object>)serializer.DeserializeObject(File.ReadAllText(args[0]).TrimStart('﻿'));
        var coordinates = PeriodicWorldCoordinates.FromPlacement(placement);
        if (coordinates == null) { Console.Error.WriteLine("The placement has no periodic block: the climate synthesis needs a source-grid layout."); return 3; }
        var source = GeneratedClimateSystem.ReadSource(args[1]);
        var result = GeneratedClimateSystem.Synthesize(source, coordinates, GeneratedClimateSystem.Policy.FromBytes(File.ReadAllBytes(args[3])));
        Directory.CreateDirectory(args[2]);
        byte[] wind = GeneratedClimateSystem.Bytes(result.Wind);
        File.WriteAllBytes(Path.Combine(args[2], "climate-wind.f32"), wind);
        File.WriteAllBytes(Path.Combine(args[2], "climate-precipitation.u8"), result.Precipitation);
        File.WriteAllBytes(Path.Combine(args[2], "climate-colorgrade.u8"), result.ColorGrade);
        File.WriteAllBytes(Path.Combine(args[2], "climate-snow.rgba"), result.SnowTexture);
        placement["climate"] = new Dictionary<string, object> {
            ["schema"] = "mnbl.generated-climate.v2", ["pattern"] = "source:" + GeneratedClimateSystem.Version + ":" + result.Identity.Substring(0, 16),
            ["version"] = GeneratedClimateSystem.Version, ["identity"] = result.Identity, ["sourceSha256"] = source.Sha256, ["coordinatePolicy"] = coordinates.Identity,
            ["policy"] = result.PolicyText,
            ["wind"] = new Dictionary<string, object> { ["file"] = "climate-wind.f32", ["dimension"] = GeneratedClimateSystem.WindDimension, ["layout"] = "row-major (x, y) float32 pairs",
                ["sha256"] = result.WindSha256, ["vector"] = new object[] { GeneratedClimateSystem.WindScale, 0.0 } },
            ["precipitation"] = new Dictionary<string, object> { ["file"] = "climate-precipitation.u8", ["dimension"] = GeneratedClimateSystem.WeatherDimension, ["layout"] = "row-major (snow, rain) byte pairs",
                ["sha256"] = result.PrecipitationSha256 },
            ["colorGrade"] = new Dictionary<string, object> { ["file"] = "climate-colorgrade.u8", ["dimension"] = GeneratedClimateSystem.ColorGradeDimension, ["layout"] = "row-major grade bytes (worldmap_color_grades.xml values)",
                ["sha256"] = result.ColorGradeSha256 },
            ["snowTexture"] = new Dictionary<string, object> { ["file"] = "climate-snow.rgba", ["dimension"] = GeneratedClimateSystem.SnowTextureDimension, ["layout"] = "row-major RGBA8, row 0 at y = extent",
                ["sha256"] = result.SnowTextureSha256, ["route"] = result.SnowRoute, ["name"] = result.SnowTextureName },
            ["record"] = result.Record };
        File.WriteAllText(Path.Combine(args[2], "placement.json"), serializer.Serialize(placement), new UTF8Encoding(false));
        string receipt = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), "build-receipt.json");
        if (File.Exists(receipt)) File.Copy(receipt, Path.Combine(args[2], "build-receipt.json"), true);
        Console.WriteLine("climate " + result.Identity + " wind " + result.WindSha256.Substring(0, 12) + " precipitation " + result.PrecipitationSha256.Substring(0, 12) + " grade " + result.ColorGradeSha256.Substring(0, 12) + " -> " + args[2]);
        return 0;
    }
}
