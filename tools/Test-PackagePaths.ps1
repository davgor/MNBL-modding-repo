# MMR-57: the packaged executables under paths with spaces and non-ASCII characters. For each case the package is extracted into
# build/mmr57/<the case's folder>/, the inputs the module would copy into a job are laid out beside it (the repository's files, in the
# job's layout), and the packaged worldbridge.exe runs the job's three steps there with the packaged fwg.exe: --new-request, --generate
# and --bundle into a bundle root on the same path.
#   ansi:    spaces, '&' and accented Latin letters the system's ANSI code page can spell. The seed's bundle must publish with the world
#            content id the game published for it (content-addressed, so the paths must not change it).
#   unicode: the same plus letters outside the ANSI code page. worldbridge refuses such a path (unrepresentable_path: the producer's
#            argv is narrow); the case records that refusal, it does not expect a world.
# Writes build/mmr57/paths.json.
param(
    [Parameter(Mandatory=$true)][string]$Zip,
    [uint32]$Seed = 42,
    [string]$ExpectedWorldContentId = 'f596b58cdcfc18798542fdef5a4d9c9f6d84abddca1425af8d0b87f521191d2d'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
function Test-Case([string]$name, [bool]$ExpectWorld) {
    $root = Join-Path $repo "build/mmr57/$name"
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    New-Item -ItemType Directory -Force $root | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $Zip).Path, $root)
    $bin = Join-Path $root 'MNBL.ProceduralWorld/bin/Win64_Shipping_Client'
    $bridge = Join-Path $bin 'worldbridge.exe'; $producer = Join-Path $bin 'fwg.exe'
    $inputs = Join-Path $root 'job/inputs'
    $files = @('worldgen/producer.lock.json','worldgen/contracts/producer-seed-width.json','worldgen/contracts/source-compatibility.json','worldgen/policies/product-policy.json',
        'worldgen/policies/world-quality.json','worldgen/policies/projection.json','worldgen/policies/terrain-materials.json','worldgen/policies/campaign-content.json',
        'worldgen/policies/culture-presentations.json','worldgen/policies/playability.json','worldgen/calibration/terrain-import/terrain-import.json',
        'worldgen/calibration/axis-sea-datum/calibration.json','worldgen/calibration/naval-transitions/naval-transitions.json','config/runtime-api-lock.json')
    foreach ($f in $files) { $d = Join-Path $inputs $f; New-Item -ItemType Directory -Force (Split-Path $d) | Out-Null; Copy-Item -LiteralPath (Join-Path $repo $f) -Destination $d }
    $job = Join-Path $root 'job'
    $utf8 = New-Object Text.UTF8Encoding $false
    [IO.File]::WriteAllText((Join-Path $job 'overrides.json'), '{"phase":16,"size":129}', $utf8)
    function In([string]$f) { Join-Path $inputs $f }
    $policies = 'product-policy=' + (In 'worldgen/policies/product-policy.json') + ',world-quality=' + (In 'worldgen/policies/world-quality.json') + ',source-compatibility=' + (In 'worldgen/contracts/source-compatibility.json') +
        ',projection=' + (In 'worldgen/policies/projection.json') + ',terrain-materials=' + (In 'worldgen/policies/terrain-materials.json') + ',terrain-import=' + (In 'worldgen/calibration/terrain-import/terrain-import.json') +
        ',campaign-content=' + (In 'worldgen/policies/campaign-content.json') + ',culture-presentations=' + (In 'worldgen/policies/culture-presentations.json') + ',playability=' + (In 'worldgen/policies/playability.json')
    $steps = [ordered]@{}
    function Run-Step([string]$Name, [string[]]$Arguments) {
        $ErrorActionPreference = 'Continue'
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $out = & $bridge @Arguments 2> (Join-Path $job "$Name.err")
        $code = $LASTEXITCODE
        $steps[$Name] = [ordered]@{ exit = $code; seconds = [math]::Round($watch.Elapsed.TotalSeconds, 2); stderr = [string](Get-Content -LiteralPath (Join-Path $job "$Name.err") -Raw -ErrorAction SilentlyContinue); stdout = [string]($out -join "`n") }
        return $code
    }
    $request = Join-Path $job 'request.json'
    $ok = (Run-Step 'request' @('--new-request','--out',$request,'--instance',([guid]::NewGuid().ToString()),'--seed',"$Seed",'--seed-source','explicit','--overrides',(Join-Path $job 'overrides.json'),'--lock',(In 'worldgen/producer.lock.json'),'--policies',$policies,'--max-attempts','3')) -eq 0
    if ($ok) { $ok = (Run-Step 'generate' @('--generate','--request',$request,'--state',(Join-Path $job 'state.json'),'--work',(Join-Path $job 'work'),'--lock',(In 'worldgen/producer.lock.json'),'--producer',$producer,'--seed-width',(In 'worldgen/contracts/producer-seed-width.json'),'--policies',$policies)) -eq 0 }
    $world = $null; $bundle = $null
    if ($ok) {
        $state = Get-Content -LiteralPath (Join-Path $job 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $i = [int]$state.chosen.attempt
        $run = Join-Path (Join-Path $job 'work') ($state.requestId.Substring(0, 16) + "/attempt-$i-run-$($state.attempts[$i].runs)")
        $world = Join-Path $run 'world'
        $bundles = Join-Path $root 'bundles'
        $ok = (Run-Step 'bundle' @('--bundle',$world,'--contract',(In 'worldgen/contracts/source-compatibility.json'),'--projection-policy',(In 'worldgen/policies/projection.json'),'--materials',(In 'worldgen/policies/terrain-materials.json'),
            '--import',(In 'worldgen/calibration/terrain-import/terrain-import.json'),'--content',(In 'worldgen/policies/campaign-content.json'),'--cultures',(In 'worldgen/policies/culture-presentations.json'),
            '--playability',(In 'worldgen/policies/playability.json'),'--producer-lock',(In 'worldgen/producer.lock.json'),'--runtime-lock',(In 'config/runtime-api-lock.json'),'--recipe',(Join-Path $run 'request.json'),
            '--root',$bundles,'--diagnostics',(Join-Path $job 'diagnostics'))) -eq 0
        if ($ok) { try { $bundle = ($steps['bundle'].stdout | ConvertFrom-Json) } catch { $bundle = $null } }
    }
    $published = if ($bundle -and $bundle.worldContentId) { $bundle.worldContentId } elseif ($ok -and (Test-Path -LiteralPath (Join-Path $root 'bundles'))) { (Get-ChildItem -LiteralPath (Join-Path $root 'bundles') -Directory | Select-Object -First 1).Name } else { $null }
    $record = [ordered]@{
        schema = 'mnbl.package-paths'; version = 1; ticket = 'MMR-57'; utc = [DateTime]::UtcNow.ToString('O')
        folder = $name; hasSpace = $name.Contains(' '); hasAmpersand = $name.Contains('&'); nonAscii = [bool]($name.ToCharArray() | Where-Object { [int]$_ -gt 127 })
        zipSha256 = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant(); seed = $Seed
        steps = $steps; worldContentId = $published; expectedWorldContentId = $ExpectedWorldContentId
        bundlePublished = [bool]($published -and (Test-Path -LiteralPath (Join-Path $root "bundles/$published/DATA_READY")))
        expectWorld = $ExpectWorld
        refusal = $(if ($steps.Contains('generate') -and $steps['generate'].exit -ne 0 -and $steps['generate'].stderr -match '"code"\s*:\s*"([^"]+)"') { $Matches[1] } else { $null })
        passed = $(if ($ExpectWorld) { [bool]($ok -and $published -eq $ExpectedWorldContentId) } else { [bool](-not $ok -and $steps.Contains('generate') -and $steps['generate'].exit -ne 0 -and $steps['generate'].stderr -match 'unrepresentable_path') })
    }
    return $record
}

$latin = [string][char]0x00DC + 'n' + [char]0x00EF + 'c' + [char]0x00F6 + 'd' + [char]0x00E9
$cases = [ordered]@{
    ansi = (Test-Case ("Pfad mit Leerzeichen & " + $latin) $true)
    unicode = (Test-Case ("Pfad mit Leerzeichen & " + $latin + ' ' + [char]0x4E16 + [char]0x754C) $false)
}
$utf8 = New-Object Text.UTF8Encoding $false
$doc = [ordered]@{ schema = 'mnbl.package-paths'; version = 2; ticket = 'MMR-57'; utc = [DateTime]::UtcNow.ToString('O'); ansiCodePage = [Text.Encoding]::Default.CodePage
    zipSha256 = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant(); seed = $Seed; expectedWorldContentId = $ExpectedWorldContentId; cases = $cases
    passed = [bool]($cases.ansi.passed -and $cases.unicode.passed) }
# The record carries no user path: the extraction roots are reported relative to the repository.
$json = ($doc | ConvertTo-Json -Depth 7).Replace($repo.Replace('\','\\'), '<repo>').Replace($repo, '<repo>')
[IO.File]::WriteAllText((Join-Path $repo 'build/mmr57/paths.json'), $json, $utf8)
Write-Output ("paths: ansi " + $(if ($cases.ansi.passed) { 'passed' } else { 'FAILED' }) + " (world $($cases.ansi.worldContentId)); unicode " + $(if ($cases.unicode.passed) { 'refused as expected' } else { 'UNEXPECTED' }) + " ($($cases.unicode.refusal))")
if ($doc.passed) { exit 0 } else { exit 1 }
