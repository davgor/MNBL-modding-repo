# MMR-247 step 5: builds the synthetic content module the acceptance run deploys (build/acceptance-content/MNBL.AcceptanceContent), as a
# content mod would ship it: extra items (copies of an installed helmet and an installed one-handed weapon under new ids), an extra troop
# (a copy of imperial_recruit carrying the new helmet) and one extra battle scene (a copy of SandBoxCore's battle_terrain_001 under a new
# scene id, listed in the module's sp_battle_scenes.xml at a map index no installed scene uses, as a high-forest Plain scene). No DLL.
# Everything is derived from the installed game at build time and stays under build/ (git-ignored); it is never committed and never
# packaged. tools/Run-Map2Acceptance.ps1 deploys it for the mods phase and removes it after. Prints the manifest path.
# (PowerShell names are case-insensitive: no variable here may be called $id, which is $Id.)
param(
    [string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord',
    [string]$Out,
    [string]$Id = 'MNBL.AcceptanceContent'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
if (!$Out) { $Out = Join-Path $repo 'build/acceptance-content' }
$utf8 = New-Object Text.UTF8Encoding $false
$module = Join-Path $Out $Id
if (Test-Path -LiteralPath $module) { Remove-Item -LiteralPath $module -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $module 'ModuleData'), (Join-Path $module 'SceneObj') | Out-Null
$modules = Join-Path $GameRoot 'Modules'
function Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Load([string]$Path) { $x = New-Object Xml.XmlDocument; $x.XmlResolver = $null; $x.PreserveWhitespace = $false; $x.Load($Path); $x }
$sources = [ordered]@{}

# Items: the first installed HeadArmor and the first OneHandedWeapon of SandBoxCore's item files, cloned under new ids.
$itemFiles = @(Get-ChildItem -LiteralPath (Join-Path $modules 'SandBoxCore/ModuleData/items') -Filter *.xml -File | Sort-Object Name)
$helmet = $null; $weapon = $null
foreach ($f in $itemFiles) {
    $doc = Load $f.FullName
    if (!$helmet) { $n = $doc.SelectSingleNode("//Item[@Type='HeadArmor']"); if ($n) { $helmet = $n; $sources['helmet'] = @{ file = $f.FullName; sha256 = (Sha $f.FullName); id = $n.GetAttribute('id') } } }
    if (!$weapon) { $n = $doc.SelectSingleNode("//CraftedItem[@crafting_template='OneHandedSword']"); if (!$n) { $n = $doc.SelectSingleNode("//Item[@Type='OneHandedWeapon']") }; if ($n) { $weapon = $n; $sources['weapon'] = @{ file = $f.FullName; sha256 = (Sha $f.FullName); id = $n.GetAttribute('id'); element = $n.Name } } }
    if ($helmet -and $weapon) { break }
}
if (!$helmet) { throw 'No installed HeadArmor item found under SandBoxCore/ModuleData/items.' }
$items = New-Object Xml.XmlDocument; $items.AppendChild($items.CreateXmlDeclaration('1.0', 'utf-8', $null)) | Out-Null
$root = $items.AppendChild($items.CreateElement('Items'))
$h = $items.ImportNode($helmet, $true); $h.SetAttribute('id', 'mnbl_acc_helmet'); $h.SetAttribute('name', '{=!}Acceptance Helmet'); $root.AppendChild($h) | Out-Null
if ($weapon) { $w = $items.ImportNode($weapon, $true); $w.SetAttribute('id', 'mnbl_acc_sword'); $w.SetAttribute('name', '{=!}Acceptance Sword'); $root.AppendChild($w) | Out-Null }
$items.Save((Join-Path $module 'ModuleData/acc_items.xml'))

# Troop: imperial_recruit under a new id, carrying the new helmet in every battle equipment set.
$npcPath = Join-Path $modules 'SandBoxCore/ModuleData/spnpccharacters.xml'
$npcs = Load $npcPath
$recruit = $npcs.SelectSingleNode("//NPCCharacter[@id='imperial_recruit']")
if (!$recruit) { throw "imperial_recruit is not in $npcPath." }
$sources['troop'] = @{ file = $npcPath; sha256 = (Sha $npcPath); id = 'imperial_recruit' }
$troops = New-Object Xml.XmlDocument; $troops.AppendChild($troops.CreateXmlDeclaration('1.0', 'utf-8', $null)) | Out-Null
$troopRoot = $troops.AppendChild($troops.CreateElement('NPCCharacters'))
$t = $troops.ImportNode($recruit, $true); $t.SetAttribute('id', 'mnbl_acc_recruit'); $t.SetAttribute('name', '{=!}Acceptance Recruit')
foreach ($e in @($t.SelectNodes(".//equipment[@slot='Head']"))) { $e.SetAttribute('id', 'Item.mnbl_acc_helmet') }
$troopRoot.AppendChild($t) | Out-Null
$troops.Save((Join-Path $module 'ModuleData/acc_troops.xml'))

# Battle scene: battle_terrain_001 copied under a new scene id (its ShaderCache left out, the scene name rewritten), at an unused map index.
$sceneId = 'mnbl_acc_battle_glade'
$sceneSource = Join-Path $modules 'SandBoxCore/SceneObj/battle_terrain_001'
if (!(Test-Path -LiteralPath (Join-Path $sceneSource 'scene.xscene'))) { throw "No installed scene at $sceneSource." }
$sceneDest = Join-Path $module "SceneObj/$sceneId"
New-Item -ItemType Directory -Force $sceneDest | Out-Null
foreach ($f in Get-ChildItem -LiteralPath $sceneSource -File) { Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $sceneDest $f.Name) }
$xscene = Join-Path $sceneDest 'scene.xscene'
$text = [IO.File]::ReadAllText($xscene)
$renamed = [regex]::Replace($text, '<scene name="battle_terrain_001"', "<scene name=`"$sceneId`"", 1)
if ($renamed -eq $text) { throw 'The copied scene.xscene does not start with <scene name="battle_terrain_001".' }
[IO.File]::WriteAllText($xscene, $renamed, $utf8)
$sources['scene'] = @{ folder = $sceneSource; sceneSha256 = (Sha (Join-Path $sceneSource 'scene.xscene')) }
$used = New-Object 'System.Collections.Generic.HashSet[int]'
foreach ($tableModule in @('SandBox', 'NavalDLC')) {
    $table = Join-Path $modules "$tableModule/ModuleData/sp_battle_scenes.xml"
    if (!(Test-Path -LiteralPath $table)) { continue }
    foreach ($s in (Load $table).SelectNodes('//Scene')) { foreach ($v in ($s.GetAttribute('map_indices') -split ',')) { $n = 0; if ([int]::TryParse($v.Trim(), [ref]$n)) { [void]$used.Add($n) } } }
}
$index = (1..255 | Where-Object { -not $used.Contains($_) } | Select-Object -First 1)
if (!$index) { throw 'Every map index 1..255 is used by an installed battle scene.' }
[IO.File]::WriteAllText((Join-Path $module 'ModuleData/sp_battle_scenes.xml'), "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n<SPBattleScenes>`n`t<Scene id=`"$sceneId`" terrain=`"Plain`" forest_density=`"High`" map_indices=`"$index`" is_naval=`"false`" />`n</SPBattleScenes>`n", $utf8)

$version = ([xml](Get-Content -LiteralPath (Join-Path $modules 'Native/SubModule.xml') -Raw)).Module.Version.value
$naval = ([xml](Get-Content -LiteralPath (Join-Path $modules 'NavalDLC/SubModule.xml') -Raw)).Module.Version.value
$descriptor = @"
<?xml version="1.0" encoding="utf-8"?>
<Module>
  <Name value="MNBL Acceptance Content (synthetic test module)" />
  <Id value="$Id" />
  <Version value="v0.0.1" />
  <SingleplayerModule value="true" />
  <MultiplayerModule value="false" />
  <ModuleCategory value="Singleplayer" />
  <DependedModules>
    <DependedModule Id="Native" DependentVersion="$version" />
    <DependedModule Id="SandBoxCore" DependentVersion="$version" />
    <DependedModule Id="Sandbox" DependentVersion="$version" />
    <DependedModule Id="NavalDLC" DependentVersion="$naval" />
  </DependedModules>
  <SubModules />
  <Xmls>
    <XmlNode><XmlName id="Items" path="acc_items" /><IncludedGameTypes><GameType value="Campaign" /><GameType value="CampaignStoryMode" /><GameType value="CustomGame" /><GameType value="EditorGame" /></IncludedGameTypes></XmlNode>
    <XmlNode><XmlName id="NPCCharacters" path="acc_troops" /><IncludedGameTypes><GameType value="Campaign" /><GameType value="CampaignStoryMode" /><GameType value="CustomGame" /><GameType value="EditorGame" /></IncludedGameTypes></XmlNode>
  </Xmls>
</Module>
"@
[IO.File]::WriteAllText((Join-Path $module 'SubModule.xml'), $descriptor, $utf8)

$files = @(Get-ChildItem -LiteralPath $module -Recurse -File | Sort-Object FullName | ForEach-Object { [ordered]@{ path = $_.FullName.Substring($module.Length + 1).Replace('\', '/'); sha256 = (Sha $_.FullName); bytes = $_.Length } })
$manifest = [ordered]@{ schema = 'mnbl.acceptance-content-module'; version = 1; ticket = 'MMR-247'; id = $Id; built = [DateTime]::UtcNow.ToString('O'); gameRoot = $GameRoot
    items = @('mnbl_acc_helmet') + $(if ($weapon) { @('mnbl_acc_sword') } else { @() }); troops = @('mnbl_acc_recruit'); battleScene = [ordered]@{ id = $sceneId; mapIndex = $index; terrain = 'Plain'; forestDensity = 'High' }
    sources = $sources; files = $files; note = 'Derived from installed TaleWorlds content at build time; test only. Never commit, package or publish it.' }
$manifestPath = Join-Path $Out "$Id.manifest.json"
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 8), $utf8)
Write-Output $manifestPath
