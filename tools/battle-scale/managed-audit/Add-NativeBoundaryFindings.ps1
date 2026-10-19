param([string]$ReportPath="$PSScriptRoot/../../../evidence/engine-extension/native-pool-module-boundaries.json")
$ErrorActionPreference='Stop'
$r=Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
$bin=$r.scope
Add-Type -Path "$bin/mono/lib/mono/gac/Mono.Cecil/0.11.0.0__0738eb9f132ed756/Mono.Cecil.dll"
function Types($items){foreach($t in $items){$t;Types $t.NestedTypes}}
$pinvoke=@()
foreach($artifact in $r.managedIdentities){
 $a=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($artifact.path)
 foreach($t in (Types $a.MainModule.Types)){
  foreach($m in $t.Methods){
   if($m.HasPInvokeInfo -and $m.PInvokeInfo.Module.Name -match 'TaleWorlds|Engine|Native'){
    $pinvoke += [ordered]@{assembly=$a.Name.Name;signature=$m.FullName;token=('0x{0:x8}' -f $m.MetadataToken.ToInt32());dll=$m.PInvokeInfo.Module.Name;entrypoint=$m.PInvokeInfo.EntryPoint;attributes=$m.PInvokeInfo.Attributes.ToString()}
   }
  }
 }
 $a.Dispose()
}
$r | Add-Member -Force -NotePropertyMembers @{managedNativePInvoke=$pinvoke}
$r.findings=@(
 @{id='inventory';status='observed-static';claim='Only the selected Win64_Shipping_Client directory was enumerated:92 PE files parsed,30 native/non-IL-only and62 IL-only, no skipped files. TaleWorlds.Native.dll is the only non-IL-only TaleWorlds-named DLL in this directory; native starter executables and third-party DLLs are retained in the inventory.';evidence='nativeModules,managedIdentities'},
 @{id='direct-native-import-boundary';status='observed-static';claim='The only normal/delay import of TaleWorlds.Native.dll found among these92 PE files is Bannerlord.Native.exe importing WotsMainNativeSDLL, at IAT VA0x140002088. No named Mission or Agent C++ import/export was identified in this bounded metadata search.';evidence='internalModuleEdges; missionAgentImportExportHits'},
 @{id='export-surface';status='observed-static';claim='TaleWorlds.Native.dll exports36 names:26 NVSDK_NGX names,5 WotsMain variants, create_game_application, get_ftdn_managed_interface, pass_controller_methods, pass_managed_initialize_method_pointer, and pass_managed_library_callback_method_pointers. It does not export the previously analyzed Mission/Agent functions by name.';evidence='nativeModules[TaleWorlds.Native.dll].exports'},
 @{id='search-candidates';status='observed-static';claim='The broad case-insensitive agent|mission substring search yielded21 candidates: ImageNtHeader, permission APIs, Mono debugger_agent APIs, and Steam HTTP UserAgentInfo. These names do not identify Bannerlord Mission or combat Agent layout access; raw hits are retained to make this classification reviewable.';evidence='missionAgentImportExportHits'},
 @{id='callback-boundary';status='observed-static';claim='The existing native registration procedure0x180643720 publishes internal native function pointers through RBX+0x108 to managed callbacks. Exported pointer-registration entrypoints and managed P/Invoke declarations provide real opaque interfaces; absence of direct C++ imports cannot clear shared layout or pointer lifetime risk.';evidence='evidence/engine-extension/native-registration.json ev_9e9945bb81c2694f4088be078e0583675691e0d2cfd346c408cc24fb86f5c2b8; nativeModules[TaleWorlds.Native.dll].exports; managedNativePInvoke'},
 @{id='pdata-and-pdb';status='observed-static';claim='TaleWorlds.Native.dll has29085 PE exception entries. All9 selected previously analyzed pool/mission/callback procedure RVAs lie at matching .pdata range beginnings. Debug RSDS identifies TaleWorlds.Native.pdb GUID2029ba6e-176d-498a-903c-b56d1af1370c age1. These corroborate compiled code locations and build identity; no symbols were loaded.';evidence='nativeModules[TaleWorlds.Native.dll].knownPoolProcedureBounds and .debug'},
 @{id='ownership-conclusion';status='inference-bounded';claim='Normal/delay native linking does not reveal another installed game DLL with named Mission/Agent member accesses. This narrows the external-module audit, but does not establish that TaleWorlds.Native owns every layout consumer. Managed AgentHelper direct dereferences already disprove treating all access as private to the native DLL.';evidence='internalModuleEdges; evidence/engine-extension/pool-managed-expansion-audit.json pointer-lifetime finding';requiredFollowup='Trace controller/function-pointer registrations, native callbacks into third-party physics/render interfaces, user-data storage, and every managed direct native pointer contract. Modules outside selected directory remain unaudited.'}
)
foreach($artifact in @($r.nativeModules)+@($r.managedIdentities)){if((Get-FileHash -LiteralPath $artifact.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $artifact.sha256){throw "Artifact changed: $($artifact.path)"}}
$native=$r.nativeModules | Where-Object name -eq 'TaleWorlds.Native.dll'
if($native.sha256 -ne '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a'){throw 'Native profile mismatch'}
if(@($r.internalModuleEdges | Where-Object importedDll -eq 'TaleWorlds.Native.dll').Count -ne 1){throw 'Import boundary changed'}
if($native.exports.Count -ne 36 -or $native.knownPoolProcedureBounds.Count -ne 9){throw 'Unexpected metadata counts'}
foreach($bound in $native.knownPoolProcedureBounds){
 if($bound.runtimeFunctions.Count -ne 1 -or $bound.runtimeFunctions[0].beginRva -ne $bound.requestedRva){throw "Unexpected .pdata boundary at $($bound.requestedRva)"}
 if([Convert]::ToInt64($bound.runtimeFunctions[0].endRvaExclusive.Substring(2),16) -le [Convert]::ToInt64($bound.requestedRva.Substring(2),16)){throw 'Invalid .pdata interval'}
}
$r.validation | Add-Member -Force -NotePropertyMembers @{all92HashesRechecked=$true;nativeProfileMatched=$true;selectedPdataRangesVerified=$true;verifiedUtc=[DateTime]::UtcNow.ToString('o')}
$r | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $ReportPath -Encoding UTF8
@{findings=$r.findings.Count;pinvokes=$pinvoke.Count;validation='static-only-passed';targetExecuted=$false} | ConvertTo-Json
