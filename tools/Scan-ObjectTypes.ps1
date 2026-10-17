param([string]$Out)
# MMR-30: lists MBObjectManager.RegisterType<T>(elementName, listName, ...) calls in the installed assemblies (read-only IL scan, out of the game).
$root = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord'
$dirs = @("$root\bin\Win64_Shipping_Client", "$root\Modules\SandBox\bin\Win64_Shipping_Client", "$root\Modules\SandBoxCore\bin\Win64_Shipping_Client", "$root\Modules\NavalDLC\bin\Win64_Shipping_Client", "$root\Modules\StoryMode\bin\Win64_Shipping_Client")
$script:dirs = $dirs
[AppDomain]::CurrentDomain.add_AssemblyResolve({ param($s, $e)
    $n = (New-Object Reflection.AssemblyName $e.Name).Name + '.dll'
    foreach ($d in $script:dirs) { $p = Join-Path $d $n; if (Test-Path $p) { return [Reflection.Assembly]::LoadFrom($p) } }
    return $null }) | Out-Null
$want = 'Culture','NPCCharacter','Hero','Kingdom','Faction','Settlement','Town','Village','MobileParty','WorkshopType','LocationComplexTemplate','SkillSet','VillageType','Item','EquipmentRoster','BodyProperty','ItemCategory','PartyTemplate','Skill','BuildingType','Trait','Policy','CharacterAttribute','Feat','SiegeEngineType','SkeletonScale'
$found = @{}
$asms = 'TaleWorlds.Core.dll','TaleWorlds.CampaignSystem.dll','TaleWorlds.MountAndBlade.dll','SandBox.dll','SandBoxCore.dll','NavalDLC.dll','TaleWorlds.ObjectSystem.dll'
foreach ($d in $dirs) { foreach ($n in $asms) { $p = Join-Path $d $n; if (-not (Test-Path $p)) { continue }
    $a = [Reflection.Assembly]::LoadFrom($p)
    try { $types = $a.GetTypes() } catch [Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ } }
    foreach ($t in $types) { foreach ($m in $t.GetMethods([Reflection.BindingFlags]'Public,NonPublic,Instance,Static,DeclaredOnly') + $t.GetConstructors([Reflection.BindingFlags]'Public,NonPublic,Instance,Static')) {
        try { $body = $m.GetMethodBody() } catch { continue }
        if ($body -eq $null) { continue }
        $il = $body.GetILAsByteArray(); $last = $null
        for ($i = 0; $i -lt $il.Length - 4; $i++) {
            if ($il[$i] -eq 0x72) { try { $last = $m.Module.ResolveString([BitConverter]::ToInt32($il, $i + 1)) } catch {} }
            elseif ($il[$i] -eq 0x6F -or $il[$i] -eq 0x28) {
                try { $r = $m.Module.ResolveMethod([BitConverter]::ToInt32($il, $i + 1)) } catch { continue }
                if ($r.Name -eq 'RegisterType' -and $r.IsGenericMethod) {
                    $arg = $r.GetGenericArguments()[0].FullName
                    # the element name is the first string argument; the list name follows it, so remember the one before the last
                    $found[$arg] = "$prev2 $prev"
                }
            }
            if ($il[$i] -eq 0x72) { $prev2 = $prev; $prev = $last }
        } } } } }
$lines = $found.GetEnumerator() | Sort-Object Value | ForEach-Object { "{0,-44} {1}" -f $_.Value, $_.Key }
$header = "# MMR-30: MBObjectManager.RegisterType<T>(elementName, listName, ...) calls in the installed v1.4.8.119303 / War Sails v1.2.8.119303 assemblies (element name, list name, class). tools/Scan-ObjectTypes.ps1"
if ($Out) { (@($header) + $lines) | Set-Content -LiteralPath $Out -Encoding utf8 }
$lines
