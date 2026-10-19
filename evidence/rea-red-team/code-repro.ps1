param(
    [string]$Snapshot = 'C:/Users/davgo/AppData/Local/Temp/mnbl-rea-review-01a14795',
    [string]$GameRoot = 'D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord'
)
$ErrorActionPreference = 'Stop'
# Coordinator-owned execution. This does not launch Bannerlord or install patches.
# Extract the original predicate block verbatim; only world-context setup and
# invocation are scaffolded. Vec2 is the installed game's actual type.
$sourcePath = Join-Path $Snapshot 'src/MNBL.ProceduralWorld/CampaignBootstrap/PeriodicPatches.cs'
$source = [IO.File]::ReadAllText($sourcePath)
$begin = $source.IndexOf('        private const int ImageSlots = 32, SlotSize = 5;')
$end = $source.IndexOf('        // The nested FindNextLocatable calls below')
if ($begin -lt 0 -or $end -le $begin) { throw 'Snapshot extraction anchors changed.' }
$methods = $source.Substring($begin, $end - $begin)
$prefix = 'using System; using TaleWorlds.Library; namespace CodeLensRepro { public sealed class PeriodicWorld { public static PeriodicWorld Current; public float Width, OriginX; public float MaxX { get { return OriginX + Width; } } } public static class ExtractedPeriodicPatches {'
$suffix = @'
public static string Run() {
    // Valid terrain-like context: canonical [1000,3000), half-width strips.
    PeriodicWorld.Current = new PeriodicWorld { Width = 2000, OriginX = 1000 };
    float imageX;
    var primary = new Vec2(1001,1500);
    const float r2 = 100;
    bool first = TryImage(primary,r2,out imageX);
    float expanded = ImageRadius(r2), expandedSquared = expanded*expanded;
    var image = new Vec2(imageX,primary.Y);
    // Positive control: an unmarked search in that strip can continue canonically.
    bool unmarked = TryImage(image,expandedSquared,out imageX);
    MarkImage(image,expandedSquared,primary,r2);
    bool imageStops = !TryImage(image,expandedSquared,out imageX);
    // An independent later search can have the exact same centre/radius.
    bool independentSearchContinues = TryImage(image,expandedSquared,out imageX);
    // One primary list can be suspended while 32 independent image passes occur.
    for(int i=0;i<32;i++) MarkImage(new Vec2(3200+i,1500),121,new Vec2(1200+i,1500),121);
    bool originalImageRestartsAfterEviction = TryImage(image,expandedSquared,out imageX);
    return "source=verbatim snapshot predicate block; scaffold=world constants and invocations; first="+first+
      "; unmarkedControl="+unmarked+"; markedImageStops="+imageStops+
      "; independentSearchContinues="+independentSearchContinues+
      "; originalImageRestartsAfter32Marks="+originalImageRestartsAfterEviction+
      "; primaryRadiusSquared="+r2.ToString("R")+"; expandedRadius="+expanded.ToString("R")+
      "; expandedRadiusSquared="+expandedSquared.ToString("R");
}
} }
'@
$library = Join-Path $GameRoot 'bin/Win64_Shipping_Client/TaleWorlds.Library.dll'
Add-Type -Path $library
$frameworkFacade = 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/netstandard.dll'
if (!(Test-Path -LiteralPath $frameworkFacade)) { throw 'Run with Windows PowerShell and the installed .NET Framework netstandard facade.' }
Add-Type -TypeDefinition ($prefix + $methods + $suffix) -ReferencedAssemblies @($library,$frameworkFacade)
[CodeLensRepro.ExtractedPeriodicPatches]::Run()
Get-FileHash -LiteralPath $sourcePath,$library -Algorithm SHA256 | Format-Table -AutoSize
