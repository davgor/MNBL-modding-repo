param([string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord', [string]$Workshop = 'D:\SteamLibrary\steamapps\workshop\content\261550', [string]$Out)
# MMR-30: every SaveableTypeDefiner base id installed with the game and in the Workshop modules (constructed out of the game
# process), so the module save-id base is chosen clear of them. A definer that cannot be constructed is listed under errors.
$ErrorActionPreference = 'Stop'
$installed = @("$GameRoot\bin\Win64_Shipping_Client") + @(Get-ChildItem "$GameRoot\Modules" -Directory | ForEach-Object { "$($_.FullName)\bin\Win64_Shipping_Client" } | Where-Object { Test-Path $_ })
$workshopDirs = @(if (Test-Path $Workshop) { Get-ChildItem $Workshop -Directory | ForEach-Object { "$($_.FullName)\bin\Win64_Shipping_Client" } | Where-Object { Test-Path $_ } })
$dirs = $installed + $workshopDirs
$script:dirs = $dirs
[AppDomain]::CurrentDomain.add_AssemblyResolve({ param($s, $e)
    $name = (New-Object Reflection.AssemblyName $e.Name).Name + '.dll'
    foreach ($d in $script:dirs) { $p = Join-Path $d $name; if (Test-Path $p) { return [Reflection.Assembly]::LoadFrom($p) } }
    return $null })
$save = [Reflection.Assembly]::LoadFrom("$GameRoot\bin\Win64_Shipping_Client\TaleWorlds.SaveSystem.dll")
$definer = $save.GetType('TaleWorlds.SaveSystem.SaveableTypeDefiner')
$baseFields = $definer.GetFields([Reflection.BindingFlags]'NonPublic,Instance') | Where-Object { $_.FieldType -eq [int] }
$rows = @(); $errors = @()
foreach ($d in $dirs) {
    foreach ($f in Get-ChildItem $d -Filter *.dll) {
        if ($f.Name -match '^(MNBL|0Harmony|Newtonsoft|System|Microsoft|mscorlib)') { continue }
        try { $asm = [Reflection.Assembly]::LoadFrom($f.FullName) } catch { continue }
        try { $types = $asm.GetTypes() } catch [Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ } }
        foreach ($t in $types) {
            if ($t.IsAbstract -or -not $definer.IsAssignableFrom($t) -or $t -eq $definer) { continue }
            try {
                $ctor = $t.GetConstructor([Reflection.BindingFlags]'Public,NonPublic,Instance', $null, [Type[]]@(), $null)
                $o = $ctor.Invoke(@())
                $vals = @($baseFields | ForEach-Object { $_.GetValue($o) })
                $rows += [pscustomobject]@{ source = $(if ($workshopDirs -contains $d) { 'workshop' } else { 'installed' }); assembly = $f.Name; module = (Split-Path (Split-Path $d)) | Split-Path -Leaf; type = $t.FullName; baseId = $vals[0] }
            } catch { $errors += "$($t.FullName): $($_.Exception.InnerException.Message)" }
        }
    }
}
$rows = $rows | Sort-Object baseId
$ours = 7910000
$nearest = ($rows | ForEach-Object { [Math]::Abs($_.baseId - $ours) } | Measure-Object -Minimum).Minimum
$result = [ordered]@{ schema = 'mnbl.save-definer-scan'; version = 1; gameRoot = $GameRoot; baseIdField = ($baseFields | ForEach-Object { $_.Name }) -join ',';
    definers = $rows; count = $rows.Count; errors = $errors; candidateBase = $ours; nearestInstalledDistance = $nearest;
    method = 'Every non-abstract SaveableTypeDefiner subclass in the game bin and module bin directories was constructed with its parameterless constructor (out of the game process) and its private base-id field read.' }
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Out -Encoding utf8
"definers=$($rows.Count) errors=$($errors.Count) nearest=$nearest"
$rows | Select-Object -First 3 | Format-Table -AutoSize | Out-String
$rows | Select-Object -Last 3 | Format-Table -AutoSize | Out-String
