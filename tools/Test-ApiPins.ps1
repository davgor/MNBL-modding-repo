param(
    [string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    [string]$RepoRoot = (Split-Path $PSScriptRoot),
    [string]$ResolvedReferences,
    [string]$ReportPath,
    [switch]$VerifyBuild
)
$ErrorActionPreference = 'Stop'

function Get-MnblHash([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing required file: $Path. Restore the pinned installation or rebuild owned outputs." }
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-MnblSourceInputs([string]$Repo) {
    $paths = @('tools.lock.json','config/runtime-api-lock.json','Modules/MNBL.ProceduralWorld/SubModule.xml',
        'tools/Build.ps1','tools/Test-ApiPins.ps1','src/MNBL.ProceduralWorld/MNBL.ProceduralWorld.csproj')
    $paths += @(Get-ChildItem -LiteralPath "$Repo/src/MNBL.ProceduralWorld" -Recurse -File -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } |
        ForEach-Object { $_.FullName.Substring($Repo.Length + 1).Replace('\','/') })
    # Every document the module embeds (the csproj's EmbeddedResource items): editing one, such as the setup screen's waiting-time
    # budget, makes the build receipt stale like a source change.
    $project = [xml](Get-Content -LiteralPath "$Repo/src/MNBL.ProceduralWorld/MNBL.ProceduralWorld.csproj" -Raw)
    foreach ($item in @($project.Project.ItemGroup | ForEach-Object { $_.EmbeddedResource } | Where-Object { $_ })) {
        $full = [IO.Path]::GetFullPath((Join-Path "$Repo/src/MNBL.ProceduralWorld" $item.Include))
        if (!$full.StartsWith([IO.Path]::GetFullPath($Repo) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Embedded resource outside the repository: $($item.Include)" }
        $paths += $full.Substring([IO.Path]::GetFullPath($Repo).Length + 1).Replace('\','/')
    }
    # MMR-43: the module's Gauntlet prefabs ship with the build.
    if (Test-Path -LiteralPath "$Repo/Modules/MNBL.ProceduralWorld/GUI") {
        $paths += @(Get-ChildItem -LiteralPath "$Repo/Modules/MNBL.ProceduralWorld/GUI" -Recurse -File |
            ForEach-Object { $_.FullName.Substring($Repo.Length + 1).Replace('\','/') })
    }
    $paths += @(Get-ChildItem -LiteralPath "$Repo/src/WorldBridge" -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](obj|bin|build)[\\/]' } |
        ForEach-Object { $_.FullName.Substring($Repo.Length + 1).Replace('\','/') })
    @($paths | Sort-Object -Unique | ForEach-Object { [pscustomobject]@{path=$_;sha256=Get-MnblHash (Join-Path $Repo $_)} })
}

function Assert-MnblSameInputs($Expected, $Actual, [string]$Label) {
    $a = @($Expected | ForEach-Object { "$($_.path)|$($_.sha256)" } | Sort-Object)
    $b = @($Actual | ForEach-Object { "$($_.path)|$($_.sha256)" } | Sort-Object)
    if (($a -join "`n") -cne ($b -join "`n")) { throw "$Label mismatch. Run tools/Build.ps1 against the pinned API before deployment." }
}

function Test-MnblApiPins([string]$Repo, [string]$Game) {
    $pins = Get-Content -LiteralPath "$Repo/config/runtime-api-lock.json" -Raw | ConvertFrom-Json
    $lock = Get-Content -LiteralPath "$Repo/tools.lock.json" -Raw | ConvertFrom-Json
    if ($pins.schema -ne 'mnbl.runtime-api-lock' -or $pins.version -ne 1 -or
        $pins.gameVersion -ne $lock.game.version -or $pins.navalVersion -ne $lock.game.navalVersion) {
        throw 'Runtime API lock differs from tools.lock.json. Review and recapture pins for the intended exact game/DLC build.'
    }
    $rows = @(foreach ($entry in @($pins.assemblies) + @($pins.descriptors)) {
        $base = if ($entry.scope -eq 'framework') { $pins.frameworkRoot } elseif ($entry.scope -eq 'workshop') { $pins.workshopRoot } else { $Game }
        $base = [IO.Path]::GetFullPath($base).TrimEnd('\','/')
        $path = [IO.Path]::GetFullPath((Join-Path $base $entry.path))
        if (!$path.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "API pin escapes its root: $($entry.path)" }
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing pinned API file: $path. Restore game $($pins.gameVersion) / NavalDLC $($pins.navalVersion); do not substitute another DLL." }
        $hash = Get-MnblHash $path
        if ($hash -ne $entry.sha256) { throw "Pinned API bytes differ: $path. Expected SHA256 $($entry.sha256); actual $hash. Restore the locked game/DLC/framework installation or explicitly review new pins." }
        if ($entry.identity -and [Reflection.AssemblyName]::GetAssemblyName($path).FullName -cne $entry.identity) { throw "Pinned API identity differs: $path; expected $($entry.identity)." }
        [pscustomobject]@{path=$entry.path;scope=$entry.scope;resolvedPath=$path;sha256=$hash;identity=$entry.identity;compile=($entry.compile -eq $true)}
    })
    # Prevent an added/redirected project reference from escaping lock coverage.
    $project = [xml](Get-Content -LiteralPath "$Repo/src/MNBL.ProceduralWorld/MNBL.ProceduralWorld.csproj" -Raw)
    $references = @($project.Project.ItemGroup.Reference | Where-Object { $_ })
    $expected = @($pins.assemblies | Where-Object { $_.compile -and $_.name -ne 'mscorlib' })
    if (($references.Include | Sort-Object) -join '|' -cne (($expected.name | Sort-Object) -join '|')) { throw 'Project references differ from the runtime API lock; review pins before building.' }
    foreach ($reference in $references | Where-Object { $_.HintPath }) {
        $path = [IO.Path]::GetFullPath(([string]$reference.HintPath).Replace('$(GameBin)', "$Game/bin/Win64_Shipping_Client").Replace('$(GameRoot)', $Game).Replace('$(WorkshopRoot)', [string]$pins.workshopRoot))
        $pin = $rows | Where-Object { $_.identity -and $_.identity.Split(',')[0] -eq $reference.Include }
        if ($path -ne $pin.resolvedPath -or [string]$reference.Private -ne 'false') { throw "Project reference must use the pinned local API without copying it: $($reference.Include)" }
    }
    $rows
}

function Test-MnblResolvedReferences($ApiInputs, [string[]]$Lines) {
    $expected = @($ApiInputs | Where-Object compile)
    $resolved = @(foreach ($line in $Lines | Where-Object { $_.Trim() }) {
        $parts = $line.Split('|')
        $path = [IO.Path]::GetFullPath($parts[0])
        $pin = @($expected | Where-Object { $_.resolvedPath -eq $path })
        if ($pin.Count -ne 1 -or $parts.Count -ne 2 -or $parts[1] -ne 'false') { throw "Unpinned or copy-local compiler reference: $line" }
        $hash = Get-MnblHash $path
        if ($hash -ne $pin[0].sha256) { throw "Compiler API bytes changed: $path" }
        [pscustomobject]@{path=$pin[0].path;scope=$pin[0].scope;resolvedPath=$path;sha256=$hash;identity=$pin[0].identity;copyLocal=$false}
    })
    Assert-MnblSameInputs $expected $resolved 'Resolved compiler API'
    $resolved
}

function Test-MnblBuildReceipt([string]$Repo, $ApiInputs) {
    $receiptPath = "$Repo/evidence/runtime/build-api-inputs.json"
    if (!(Test-Path -LiteralPath $receiptPath)) { throw 'Missing verified build receipt. Run tools/Build.ps1 before deployment.' }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($receipt.schema -ne 'mnbl.build-api-inputs' -or $receipt.version -ne 1 -or $receipt.status -ne 'built') { throw 'No successful pinned build recorded. Run tools/Build.ps1 before deployment.' }
    Assert-MnblSameInputs $receipt.sources (Get-MnblSourceInputs $Repo) 'Build source/input'
    Assert-MnblSameInputs $receipt.apiInputs $ApiInputs 'Build API input'
    $lines = @($receipt.resolvedReferences | ForEach-Object {
        $record = $_
        $pin = @($ApiInputs | Where-Object { $_.path -eq $record.path -and $_.scope -eq $record.scope })
        if ($pin.Count -ne 1 -or $record.sha256 -ne $pin[0].sha256 -or $record.copyLocal -ne $false) { throw 'Build compiler reference receipt mismatch; rebuild against the pinned API.' }
        "$($pin[0].resolvedPath)|false"
    })
    $null = Test-MnblResolvedReferences $ApiInputs $lines
    $outputs = @('build/module/MNBL.ProceduralWorld.dll','build/worldbridge/Release/worldbridge.exe' | ForEach-Object {
        [pscustomobject]@{path=$_;sha256=Get-MnblHash (Join-Path $Repo $_)}
    })
    Assert-MnblSameInputs $receipt.outputs $outputs 'Build output'
    $receipt
}

if ($MyInvocation.InvocationName -ne '.') {
    $RepoRoot = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\','/')
    $inputs = @(Test-MnblApiPins $RepoRoot $GameRoot)
    if ($VerifyBuild) { $null = Test-MnblBuildReceipt $RepoRoot $inputs }
    if ($ResolvedReferences) {
        $resolved = @(Test-MnblResolvedReferences $inputs (Get-Content -LiteralPath $ResolvedReferences))
        if ($ReportPath) { $resolved | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding utf8 }
    }
    Write-Output "PASS: exact runtime API pins verified ($($inputs.Count) files)"
}
