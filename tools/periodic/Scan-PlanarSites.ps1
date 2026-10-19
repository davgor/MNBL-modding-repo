param([string]$GameRoot = 'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord', [string]$Out)
# MMR-65 audit (tools/periodic/build_spatial_table.py consumes it): per method, the calls that measure, difference or compare campaign
# positions in planar terms, direct LocatorGrid use, the locator wrappers, the point-around-position helpers, native scene calls and Vec3
# distances; then, for every small method holding such a call (an inlining candidate), its callers, closed over small callers. Read-only
# over the installed assemblies. Writes <Out> (one row per method and callee: assembly, type::method, parameters, kind, callee, count, IL
# size of the method) and <Out>.inputs.tsv (every scanned assembly with its SHA-256).
$ErrorActionPreference = 'Stop'
$client = Join-Path $GameRoot 'bin/Win64_Shipping_Client'
Add-Type -Path (Join-Path $client 'mono/lib/mono/gac/Mono.Cecil/0.11.0.0__0738eb9f132ed756/Mono.Cecil.dll')
$files = @((Join-Path $client 'TaleWorlds.CampaignSystem.dll'), (Join-Path $client 'TaleWorlds.CampaignSystem.ViewModelCollection.dll')) +
         @(Get-ChildItem (Join-Path $GameRoot 'Modules') -Filter '*.dll' -Recurse | Where-Object { $_.FullName -match 'Win64_Shipping_Client' -and $_.Name -match '^(SandBox|SandBox\.View|NavalDLC|NavalDLC\.View|StoryMode)\.dll$' } | ForEach-Object FullName)
$patterns = @(
  @{ k='distance'; re='^System\.Single TaleWorlds\.(CampaignSystem\.CampaignVec2|Library\.Vec2)::(Distance|DistanceSquared)\(' },
  @{ k='subtract'; re='^TaleWorlds\.(Library\.Vec2|CampaignSystem\.CampaignVec2) TaleWorlds\.(Library\.Vec2|CampaignSystem\.CampaignVec2)::op_Subtraction\(' },
  @{ k='nearly';   re='^System\.Boolean TaleWorlds\.(CampaignSystem\.CampaignVec2|Library\.Vec2)::NearlyEquals\(' },
  @{ k='vec3';     re='^System\.Single TaleWorlds\.Library\.Vec3::(Distance|DistanceSquared)\(' },
  @{ k='locator';  re='TaleWorlds\.CampaignSystem\.Map\.LocatorGrid`1<.*>::(StartFindingLocatablesAroundPosition|FindNextLocatable)\(' },
  @{ k='locatorWrapper'; re='TaleWorlds\.CampaignSystem\.(Party\.MobileParty|Settlements\.Settlement)::(StartFindingLocatablesAroundPosition|FindNextLocatable)\(' },
  @{ k='pointAround'; re='Helpers\.NavigationHelper::(FindPointAroundPosition|FindPointInsideArea|FindReachablePointAroundPosition)\(' },
  @{ k='native';   re='^[^ ]+ (TaleWorlds\.Engine\.Scene|TaleWorlds\.MountAndBlade\.MBMapScene|TaleWorlds\.CampaignSystem\.Map\.IMapScene)::' }
)
$InlineLimit = 100   # RyuJIT's default IL size limit for inlining (larger methods inline only when marked AggressiveInlining)
$rows = New-Object System.Collections.Generic.List[string]
$inputs = New-Object System.Collections.Generic.List[string]
$methods = New-Object System.Collections.Generic.List[object]   # every method with a body: @{ asm; m; key }
$small = New-Object 'System.Collections.Generic.HashSet[string]'
$assemblies = @()
foreach ($file in $files) {
  $inputs.Add([IO.Path]::GetFileNameWithoutExtension($file) + "`t" + $file + "`t" + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant())
  $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($file)
  $assemblies += $asm
  $stack = New-Object System.Collections.Generic.Stack[object]
  foreach ($t in $asm.MainModule.Types) { $stack.Push($t) }
  while ($stack.Count -gt 0) {
    $t = $stack.Pop(); foreach ($n in $t.NestedTypes) { $stack.Push($n) }
    foreach ($m in $t.Methods) {
      if (-not $m.HasBody) { continue }
      $methods.Add(@{ asm = $asm.Name.Name; m = $m; t = $t })
      $counts = @{}
      foreach ($i in $m.Body.Instructions) {
        if ($i.OpCode.Code -ne 'Call' -and $i.OpCode.Code -ne 'Callvirt') { continue }
        $callee = $i.Operand.FullName
        foreach ($p in $patterns) {
          if ($callee -match $p.re) {
            $key = $p.k + "`t" + ($callee -replace '^[^ ]+ ', '')
            if ($counts.ContainsKey($key)) { $counts[$key]++ } else { $counts[$key] = 1 }
          }
        }
      }
      $size = $m.Body.CodeSize
      $inlinable = $size -le $InlineLimit -or (([int]$m.ImplAttributes -band 0x0100) -ne 0)   # 0x0100: AggressiveInlining
      foreach ($key in $counts.Keys) {
        $rows.Add($asm.Name.Name + "`t" + $t.FullName + "::" + $m.Name + "`t" + $m.FullName.Split('(')[1].TrimEnd(')') + "`t" + $key + "`t" + $counts[$key] + "`t" + $size)
        if ($inlinable -and $key -notmatch '^native') { [void]$small.Add($m.FullName) }
      }
    }
  }
}
# Callers of the small methods, closed over small callers (a caller compiled before a patch may hold an inlined copy of its callee).
$callers = @{}
$frontier = New-Object 'System.Collections.Generic.HashSet[string]' (,[string[]]@($small))
while ($frontier.Count -gt 0) {
  $next = New-Object 'System.Collections.Generic.HashSet[string]'
  foreach ($entry in $methods) {
    $m = $entry.m
    foreach ($i in $m.Body.Instructions) {
      if ($i.OpCode.Code -ne 'Call' -and $i.OpCode.Code -ne 'Callvirt') { continue }
      $callee = $i.Operand.FullName
      if (-not $frontier.Contains($callee)) { continue }
      $key = $entry.asm + "`t" + $entry.t.FullName + "::" + $m.Name + "`t" + $m.FullName.Split('(')[1].TrimEnd(')') + "`tcaller`t" + ($callee -replace '^[^ ]+ ', '')
      if ($callers.ContainsKey($key)) { $callers[$key][0]++ } else { $callers[$key] = @(1, $m.Body.CodeSize) }
      $inlinable = $m.Body.CodeSize -le $InlineLimit -or (([int]$m.ImplAttributes -band 0x0100) -ne 0)
      if ($inlinable -and $small.Add($m.FullName)) { [void]$next.Add($m.FullName) }
    }
  }
  $frontier = $next
}
foreach ($key in $callers.Keys) { $rows.Add($key + "`t" + $callers[$key][0] + "`t" + $callers[$key][1]) }
foreach ($asm in $assemblies) { $asm.Dispose() }
$rows | Sort-Object | Set-Content -Encoding UTF8 $Out
$inputs | Set-Content -Encoding UTF8 ($Out + '.inputs.tsv')
"$($rows.Count) rows -> $Out"
