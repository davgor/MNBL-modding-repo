param([string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
      [string]$Catalog = 'evidence/runtime/in-game/campaign_catalog-10212.json',
      [string]$OutDir = 'worldgen/templates/cultures')
# MMR-31: the installed culture contract and the official cultures generated worlds present through.
#   contract.json: every XML member CultureObject.Deserialize and BasicCultureObject.Deserialize read (the strings in their
#     IL), the engine property each fills, how that property can be written (setter accessibility on its declaring type) and
#     which methods write it (call sites of the setter in the IL), plus the two types' constructors. Almost every gameplay
#     reference is written only by Deserialize, so a generated world reuses the installed, XML-initialized cultures
#     (worldgen/policies/culture-presentations.json) instead of constructing cultures in code.
#   <culture>.json: each main culture's values for every contract member, from the merged campaign catalog (hash-checked),
#     with the members it leaves empty, and the naval members called out.
# Read-only: reflection on the installed assemblies out of the game process, and the catalog XML.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$bin = Join-Path $GameRoot 'bin\Win64_Shipping_Client'
$script:bin = $bin
$resolver = [ResolveEventHandler]{ param($s, $e)
    $p = Join-Path $script:bin ((New-Object Reflection.AssemblyName $e.Name).Name + '.dll')
    if (Test-Path $p) { return [Reflection.Assembly]::LoadFrom($p) } return $null }
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
$campaignDll = Join-Path $bin 'TaleWorlds.CampaignSystem.dll'; $coreDll = Join-Path $bin 'TaleWorlds.Core.dll'
$campaign = [Reflection.Assembly]::LoadFrom($campaignDll); $core = [Reflection.Assembly]::LoadFrom($coreDll)
$cultureType = $campaign.GetType('TaleWorlds.CampaignSystem.CultureObject'); $basicType = $core.GetType('TaleWorlds.Core.BasicCultureObject')
$all = [Reflection.BindingFlags]'Public,NonPublic,Instance,Static,DeclaredOnly'

# Call sites: methods whose IL holds call/callvirt (0x28/0x6F) with a setter's metadata token, in the setter's module.
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Reflection;
public static class MnblIlCalls {
    public static string[] Callers(IEnumerable<Type> types, MethodInfo target) {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var t in types) {
            var methods = new List<MethodBase>();
            try { methods.AddRange(t.GetMethods(all)); methods.AddRange(t.GetConstructors(all)); } catch { continue; }
            foreach (var m in methods) {
                if (m.Module != target.Module) continue;
                byte[] il; try { var body = m.GetMethodBody(); if (body == null) continue; il = body.GetILAsByteArray(); } catch { continue; }
                for (int i = 0; i + 4 < il.Length; i++)
                    if ((il[i] == 0x28 || il[i] == 0x6F) && BitConverter.ToInt32(il, i + 1) == target.MetadataToken) { found.Add(t.Name + "." + m.Name); break; }
            }
        }
        return new List<string>(found).ToArray();
    }
}
'@

function Strings($method) {
    $il = $method.GetMethodBody().GetILAsByteArray(); $out = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $il.Length - 4; $i++) { if ($il[$i] -eq 0x72) { try { $s = $method.Module.ResolveString([BitConverter]::ToInt32($il, $i + 1)); if ($s -match '^[a-zA-Z_][a-zA-Z0-9_]*$' -and -not $out.Contains($s)) { $out.Add($s) } } catch {} } }
    return $out
}
function Nested($type) { @($type) + @($type.GetNestedTypes([Reflection.BindingFlags]'Public,NonPublic') | ForEach-Object { Nested $_ }) }
# Accessors are read on the declaring type: through a derived type a base class's private accessor is invisible.
function Access($type, $name) {
    $p = $type.GetProperty($name, [Reflection.BindingFlags]'Public,NonPublic,Instance')
    if ($p -eq $null) { return $null }
    $p = $p.DeclaringType.GetProperty($name, [Reflection.BindingFlags]'Public,NonPublic,Instance,DeclaredOnly')
    $s = $p.GetSetMethod($true)
    $setter = if ($s -eq $null) { 'none' } elseif ($s.IsPublic) { 'public' } elseif ($s.IsAssembly -or $s.IsFamilyOrAssembly) { 'internal' } elseif ($s.IsFamily) { 'protected' } else { 'private' }
    # Private setters are callable from the declaring type and its nested types; internal and wider ones from the whole assembly.
    $writers = @()
    if ($s -ne $null) {
        $scope = if ($setter -eq 'private') { Nested $p.DeclaringType } else { $p.DeclaringType.Assembly.GetTypes() }
        $writers = @([MnblIlCalls]::Callers([Type[]]$scope, $s))
    }
    return [ordered]@{ property = $p.Name; type = $p.PropertyType.Name; declaringType = $p.DeclaringType.Name; setter = $setter; writers = $writers }
}
function Ctors($type) {
    @($type.GetConstructors([Reflection.BindingFlags]'Public,NonPublic,Instance') | ForEach-Object {
        [ordered]@{ type = $type.Name; access = $(if ($_.IsPublic) { 'public' } elseif ($_.IsAssembly) { 'internal' } elseif ($_.IsFamily) { 'protected' } else { 'private' })
                    parameters = @($_.GetParameters() | ForEach-Object { $_.ParameterType.Name + ' ' + $_.Name }) } })
}
# XML member -> property where the names do not match after removing underscores (from the IL order of Deserialize).
$irregular = @{
    'settlement_patrol_template_level_1' = 'SettlementPatrolPartyTemplateWeak'; 'settlement_patrol_template_level_2' = 'SettlementPatrolPartyTemplateModerate'
    'settlement_patrol_template_level_3' = 'SettlementPatrolPartyTemplateStrong'; 'settlement_patrol_template_coastal' = 'SettlementPatrolPartyTemplateNaval'
    'vassal_reward_party_template' = 'VassalRewardTroopsPartyTemplate'; 'start_point_position_x' = 'StartingPoint'; 'start_point_position_y' = 'StartingPoint'
    'default_policies' = 'DefaultPolicyList'; 'male_names' = 'MaleNameList'; 'female_names' = 'FemaleNameList'; 'clan_names' = 'ClanNameList'
    'cultural_feats' = 'CultureFeats'; 'possible_clan_banner_icon_ids' = 'PossibleClanBannerIconsIDs'; 'rebellion_hero_templates' = 'RebelliousHeroTemplates'
    'tournament_team_templates_one_participant' = 'TournamentTeamTemplatesForOneParticipant'; 'tournament_team_templates_two_participant' = 'TournamentTeamTemplatesForTwoParticipant'
    'tournament_team_templates_four_participant' = 'TournamentTeamTemplatesForFourParticipant'; 'board_game_type' = 'BoardGame'; 'text' = 'EncyclopediaText'
    'banner_bearer_replacement_weapons' = 'BannerBearerReplacementWeapons'; 'taverngamehost' = 'TavernGamehost'; 'horseMerchant' = 'HorseMerchant'
    'faction_banner_key' = 'Banner'; 'is_main_culture' = 'IsMainCulture'; 'can_have_settlement' = 'CanHaveSettlement'; 'encounter_background_mesh' = 'EncounterBackgroundMesh'
    'cloth_alternative_color1' = 'ClothAlternativeColor'; 'cloth_alternative_color2' = 'ClothAlternativeColor2'; 'banner_background_color1' = 'BackgroundColor1'
    'banner_foreground_color1' = 'ForegroundColor1'; 'banner_background_color2' = 'BackgroundColor2'; 'banner_foreground_color2' = 'ForegroundColor2'
}
$naval = @('shipwright', 'shipyard_worker', 'fishing_party_template', 'available_ship_hulls', 'settlement_patrol_template_coastal', 'naval_factor')
$skip = @('id', 'name')   # element/attribute names reused inside child lists
$members = [ordered]@{}
function Member($name, $readBy, $access) {
    [string[]]$writers = if ($access) { $access.writers } else { @() }   # typed, so one writer stays a list in the JSON
    if ($writers -eq $null) { [string[]]$writers = @() }
    [ordered]@{ readBy = $readBy; property = $(if ($access) { $access.property } else { $null }); propertyType = $(if ($access) { $access.type } else { $null })
                declaringType = $(if ($access) { $access.declaringType } else { $null }); setter = $(if ($access) { $access.setter } else { 'unmapped' })
                writers = $writers; naval = ($naval -contains $name) }
}
foreach ($pair in @(@($cultureType, 'CultureObject.Deserialize'), @($basicType, 'BasicCultureObject.Deserialize'))) {
    $type = $pair[0]
    foreach ($name in (Strings ($type.GetMethod('Deserialize', [Reflection.BindingFlags]'Public,Instance,DeclaredOnly')))) {
        if ($skip -contains $name -or $members.Contains($name)) { continue }
        $target = if ($irregular.ContainsKey($name)) { $irregular[$name] } else { ($name -split '_' | ForEach-Object { if ($_.Length) { $_.Substring(0, 1).ToUpper() + $_.Substring(1) } }) -join '' }
        $members[$name] = Member $name $pair[1] (Access $cultureType $target)
    }
}
# Base members BasicCultureObject.Deserialize reads that its IL spells differently (colors, banner).
foreach ($name in @('color', 'color2', 'faction_banner_key', 'is_main_culture', 'can_have_settlement', 'encounter_background_mesh')) {
    if (-not $members.Contains($name)) { $a = Access $cultureType $irregular[$name]; if (-not $a) { $a = Access $cultureType ((Get-Culture).TextInfo.ToTitleCase($name)) }
        $members[$name] = Member $name 'BasicCultureObject.Deserialize' $a }
}
$deserializers = @('CultureObject.Deserialize', 'BasicCultureObject.Deserialize')
$onlyXml = @($members.Keys | Where-Object { $m = $members[$_]; $m.setter -ne 'unmapped' -and $m.setter -ne 'public' -and @($m.writers | Where-Object { $deserializers -notcontains $_ }).Count -eq 0 })
$otherWriters = [ordered]@{}
foreach ($k in $members.Keys) { $w = @($members[$k].writers | Where-Object { $deserializers -notcontains $_ }); if ($w.Count) { $otherWriters[$k] = $w } }
$receipt = Get-Content (Join-Path $repo $Catalog) -Raw | ConvertFrom-Json
$doc = @($receipt.outputs.documents | Where-Object { $_.id -eq 'SPCultures' })[0]
$xmlPath = $doc.path
if (-not (Test-Path $xmlPath)) { throw "Catalog SPCultures.xml not found: $xmlPath" }
$sha = (Get-FileHash -LiteralPath $xmlPath).Hash.ToLowerInvariant()
if ($sha -ne $doc.sha256) { throw "Catalog SPCultures.xml hash differs from the receipt" }
[xml]$xml = Get-Content -LiteralPath $xmlPath -Raw
$out = Join-Path $repo $OutDir
New-Item -ItemType Directory -Force -Path $out | Out-Null
$provenance = [ordered]@{ catalogReceipt = $Catalog; spCulturesSha256 = $sha; campaignSystemSha256 = (Get-FileHash $campaignDll).Hash.ToLowerInvariant(); coreSha256 = (Get-FileHash $coreDll).Hash.ToLowerInvariant() }
$count = { param($s) @($members.Values | Where-Object { $_.setter -eq $s }).Count }
$mains = @($xml.SPCultures.Culture | Where-Object { $_.is_main_culture -eq 'true' })
$required = @($members.Keys | Where-Object { $n = $_; @($mains | Where-Object { -not $_.GetAttribute($n) -and $_.SelectSingleNode($n) -eq $null }).Count -eq 0 })
$contract = [ordered]@{ schema = 'mnbl.culture-contract'; version = 2; provenance = $provenance; members = $members
    navalMembers = $naval
    constructors = @(@(Ctors $cultureType) + @(Ctors $basicType))
    summary = [ordered]@{ members = $members.Count; privateSetter = (& $count 'private'); noSetter = (& $count 'none'); internalSetter = (& $count 'internal'); protectedSetter = (& $count 'protected')
                          publicSetter = (& $count 'public'); writtenOnlyByDeserialize = $onlyXml.Count; otherWriters = $otherWriters
                          unmapped = @($members.Keys | Where-Object { $members[$_].setter -eq 'unmapped' }) }
    initialization = 'Setter accessibility is read on the property''s declaring type. writers are the methods whose IL calls the setter (call/callvirt with its token; private setters are searched in the declaring type and its nested types, wider ones in the whole declaring assembly). A get-only property (setter none) is filled through its backing field by Deserialize. The constructors take no culture data, so a culture is complete only after Deserialize.'
    policy = 'A generated world presents every civilization through an installed main culture (culture-presentations.json). The culture contract is filled from XML, so it is reused as installed, never constructed or patched in code.'
    required = $required
    requiredRule = 'A member is required when every installed main culture sets it; the installed game runs with the others empty (bandit-only members, optional bonuses and colours with defaults). For the installed main cultures requiredMissing is empty by construction; the check guards any template that is not an installed main culture.' }
$contract | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out 'contract.json') -Encoding utf8
$cultures = @()
foreach ($e in $mains) {
    $values = [ordered]@{}; $empty = @()
    foreach ($name in $members.Keys) {
        $attr = $e.GetAttribute($name)
        $child = $e.SelectSingleNode($name)
        if ($child -ne $null) { $values[$name] = @($child.ChildNodes | Where-Object { $_.NodeType -eq 'Element' } | ForEach-Object { if ($_.GetAttribute('id')) { $_.GetAttribute('id') } else { $_.GetAttribute('name') } }) }
        elseif ($attr) { $values[$name] = $attr }
        else { $empty += $name }
    }
    $navalMissing = @($naval | Where-Object { $empty -contains $_ -or ($values.Contains($_) -and $values[$_] -is [array] -and $values[$_].Count -eq 0) })
    $requiredMissing = @($required | Where-Object { $empty -contains $_ })
    [ordered]@{ schema = 'mnbl.culture-template'; version = 1; culture = $e.id; provenance = $provenance; values = $values; empty = $empty; requiredMissing = $requiredMissing; navalMissing = $navalMissing } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out ($e.id + '.json')) -Encoding utf8
    $cultures += "$($e.id): $($values.Count) set, $($empty.Count) empty ($($empty -join ',')), naval missing: $($navalMissing -join ',')"
}
"contract members=$($members.Count) required=$($required.Count) private=$(& $count 'private') none=$(& $count 'none') internal=$(& $count 'internal') public=$(& $count 'public') onlyDeserialize=$($onlyXml.Count) unmapped=$($contract.summary.unmapped -join ',')"
"other writers: " + (($otherWriters.Keys | ForEach-Object { "$_=" + ($otherWriters[$_] -join '/') }) -join '; ')
$cultures
[AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)
