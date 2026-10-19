param([string]$Snapshot = 'C:/Users/davgo/AppData/Local/Temp/mnbl-rea-review-01a14795')
$ErrorActionPreference = 'Stop'
# Compile the unchanged owning methods against controlled version providers.
# No target assembly is loaded/executed; the installed game is never modified.
$src = Join-Path $Snapshot 'src/MNBL.ProceduralWorld'
function Method([string]$Text, [string]$Signature) {
    $start = $Text.IndexOf($Signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Method not found: $Signature" }
    $open = $Text.IndexOf('{', $start); $depth = 1; $end = $open + 1
    while ($depth -gt 0 -and $end -lt $Text.Length) {
        if ($Text[$end] -eq '{') { $depth++ }; if ($Text[$end] -eq '}') { $depth-- }; $end++
    }
    if ($depth -ne 0) { throw 'Unclosed method' }
    return $Text.Substring($start, $end - $start)
}
$patchSource = Get-Content -Raw (Join-Path $src 'Campaign/GeneratedPatches.cs')
$hostSource = Get-Content -Raw (Join-Path $src 'UI/WorldSetupScreen.cs')
$guard = Method $patchSource 'public static void RequireInstalledVersion(string patch)'
$versions = Method $patchSource 'public static string InstalledVersions()'
$bundle = Method $hostSource 'public static WorldDataBundle.HostProfile BundleHost()'
$prefix = @'
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using TaleWorlds.Engine;
using TaleWorlds.ModuleManager;
namespace TaleWorlds.Engine { public static class Utilities { public static string Game = "v1.4.8.119303"; public static object GetApplicationVersionWithBuildNumber() { return Game; } } }
namespace TaleWorlds.ModuleManager { public class ModuleVersion { public int Major, Minor, Revision; public ModuleVersion(int major,int minor,int revision) { Major=major; Minor=minor; Revision=revision; } public override string ToString() { return "v"+Major+"."+Minor+"."+Revision; } } public class Info { public ModuleVersion Version = new ModuleVersion(1, 2, 8); } public static class ModuleHelper { public static Info Info = new Info(); public static Info GetModuleInfo(string id) { return Info; } } }
public class WorldDataBundle { public class HostProfile { public string GameVersion, NavalVersion; public string[] Capabilities; public HostProfile(string game, string naval, IEnumerable<string> caps) { GameVersion=game; NavalVersion=naval; Capabilities=caps.ToArray(); } } }
public static class GeneratedPatches {
public const string GameVersion="v1.4.8.119303", NavalVersion="v1.2.8";
'@
$middle = @'
}
public static class WorldSetupHost {
'@
$suffix = @'
}
public static class Probe {
public static void Main() {
    foreach (string scenario in new [] { "positive_control_exact_release", "negative_control_wrong_release", "changed_dll_same_release" }) {
        TaleWorlds.ModuleManager.ModuleHelper.Info.Version = scenario == "negative_control_wrong_release" ? new ModuleVersion(1,2,9) : new ModuleVersion(1,2,8);
        string refusal=null; try { GeneratedPatches.RequireInstalledVersion("probe"); } catch (Exception e) { refusal=e.Message; }
        var profile=WorldSetupHost.BundleHost();
        Console.WriteLine(new JavaScriptSerializer().Serialize(new { scenario, refusal, profile.GameVersion, profile.NavalVersion, profile.Capabilities, artifactBytesConsulted=false }));
        if ((scenario == "negative_control_wrong_release") != (refusal != null)) throw new Exception("Version guard control failed: " + scenario);
        if (scenario != "negative_control_wrong_release" && profile.NavalVersion != "v1.2.8.119303") throw new Exception("Bundle profile control failed");
    }
}
}
'@
$dir = Join-Path ([IO.Path]::GetTempPath()) ('mnbl-principal-repro-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($dir) | Out-Null
$code = Join-Path $dir 'Probe.cs'; $exe = Join-Path $dir 'Probe.exe'
[IO.File]::WriteAllText($code, $prefix + $versions + $guard + $middle + $bundle + $suffix)
$lock = Join-Path $Snapshot 'config/runtime-api-lock.json'
$csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $csc /nologo /target:exe "/out:$exe" /r:System.Core.dll /r:System.Web.Extensions.dll "/resource:$lock,MNBL.ProceduralWorld.runtime-api-lock.json" $code
if ($LASTEXITCODE -ne 0) { throw "Probe compilation failed: $LASTEXITCODE" }
& $exe
if ($LASTEXITCODE -ne 0) { throw "Probe failed: $LASTEXITCODE" }
# 'changed_dll_same_release' is a controlled host metadata case. It proves the
# methods cannot distinguish artifacts; it does NOT simulate loading changed IL,
# Harmony patch acceptance, or successful game execution on an altered assembly.
