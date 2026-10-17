param([string[]]$Patterns=@('CookTrianglePhysxMesh','ImportNavigationMeshPrefab','SetTerrain','SaveNavMeshPrefab'), [string]$GameRoot='D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord')
$ErrorActionPreference='Stop'
$client=Join-Path $GameRoot 'bin/Win64_Shipping_Client'
Add-Type -Path (Join-Path $client 'mono/lib/mono/gac/Mono.Cecil/0.11.0.0__0738eb9f132ed756/Mono.Cecil.dll')
function Get-AllTypes($types) { foreach ($type in $types) { $type; Get-AllTypes $type.NestedTypes } }
$files=@(Get-ChildItem $client -Filter 'TaleWorlds*.dll')+@(Get-ChildItem (Join-Path $GameRoot 'Modules') -Filter '*.dll' -Recurse | Where-Object FullName -match 'Win64_Shipping_Client')
$results=@(foreach($file in $files) {
    try { $asm=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($file.FullName) } catch { continue }
    foreach($type in (Get-AllTypes $asm.MainModule.Types)) {
        foreach($method in $type.Methods) {
            $matches=@($method.Body.Instructions | Where-Object { $text=[string]$_.Operand; foreach($pattern in $Patterns) { if($text.Contains($pattern)) { $true; break } } })
            if($matches.Count) { @{assembly=$file.FullName;caller=$method.FullName;body=@($method.Body.Instructions | ForEach-Object ToString)} }
        }
    }
    $asm.Dispose()
})
$results|ConvertTo-Json -Depth 8 |Set-Content evidence/runtime/call-traces.json
$results | ForEach-Object { $_.caller }
