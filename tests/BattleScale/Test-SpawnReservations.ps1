$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Add-Type -Path @((Join-Path $repo 'src/MNBL.BattleScale/Core/SpawnAdmission.cs'),
                (Join-Path $PSScriptRoot 'SpawnReservationTests.cs'))
$count=[SpawnReservationTests]::Run()
Write-Output "PASS: $count reservation assertions; actual Core implementation, no game providers loaded."
