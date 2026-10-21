param()
# MMR-247 (K): the hang dump tooling on a process this harness starts and blocks itself (tests/Runtime/HangDumpTarget.cs): for each scenario
# (a Monitor deadlock, a native wait, a GC whose runtime suspension never completes, an exception held mid-dispatch) the target is dumped with
# tools/Write-ProcessDump.ps1 (rundll32 comsvcs.dll MiniDump ... full, as Run-Map2Acceptance.ps1 dumps a blocked game) and killed, then
# tools/hangdump/HangDump.cs reads the dump; the checks are on its JSON summary and report. No game process; only processes it started.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$out = Join-Path $repo ('build/hangdump/test-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $out | Out-Null
$analyzer = & (Join-Path $PSScriptRoot 'hangdump/Build-HangDump.ps1')
$target = & (Join-Path $PSScriptRoot 'hangdump/Build-HangDump.ps1') -Source (Join-Path $repo 'tests/Runtime/HangDumpTarget.cs') -OutDir $out
$script:pass = 0; $script:fail = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { $script:pass++; Write-Output "  ok   $Name" } else { $script:fail++; Write-Output "  FAIL $Name $Detail" }
}

function Run-Scenario([string]$Scenario) {
    $heartbeat = Join-Path $out "heartbeat-$Scenario.txt"
    $psi = New-Object Diagnostics.ProcessStartInfo $target, "$Scenario $heartbeat"
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
    $p = [Diagnostics.Process]::Start($psi)
    try {
        $read = $p.StandardOutput.ReadLineAsync()
        if (!$read.Wait(20000)) { throw "$Scenario`: no ready line in 20 s" }
        $m = [regex]::Match([string]$read.Result, '^ready (\S+) (\d+)$')
        if (!$m.Success) { throw "$Scenario`: $($read.Result)" }
        Start-Sleep -Milliseconds 1500
        $beat1 = Get-Content -LiteralPath $heartbeat -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 1000; $beat2 = Get-Content -LiteralPath $heartbeat -ErrorAction SilentlyContinue
        $dump = Join-Path $out "$Scenario.dmp"
        $written = & (Join-Path $PSScriptRoot 'Write-ProcessDump.ps1') -ProcessId $p.Id -Path $dump
    }
    finally { if (!$p.HasExited) { $p.Kill(); $null = $p.WaitForExit(10000) } }
    $report = Join-Path $out "$Scenario.txt"; $json = Join-Path $out "$Scenario.json"
    if ($written['ok']) {
        $saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try { $null = & $analyzer $dump --out $report --json $json 2>&1; $code = $LASTEXITCODE } finally { $ErrorActionPreference = $saved }
    }
    else { $code = -1 }
    $doc = $(if (Test-Path -LiteralPath $json) { Get-Content -LiteralPath $json -Raw | ConvertFrom-Json } else { $null })
    return [pscustomobject]@{ Scenario = $Scenario; Watched = [uint32]$m.Groups[2].Value; Moving = ($beat1 -ne $beat2); Dump = $written; Exit = $code; Doc = $doc
        Report = $(if (Test-Path -LiteralPath $report) { [IO.File]::ReadAllText($report) } else { '' }) }
}

function Thread-Of($r) { @($r.Doc.threads | Where-Object { $_.osid -eq $r.Watched })[0] }
function Findings($r) { @($r.Doc.findings) -join "`n" }

Write-Output 'deadlock'
$r = Run-Scenario 'deadlock'
Check 'dump written, full memory' ($r.Dump['ok'] -and $r.Dump['bytes'] -gt 10MB) "$($r.Dump['error']) $($r.Dump['flags'])"
Check 'analyzer exit 0, summary read' ($r.Exit -eq 0 -and $null -ne $r.Doc) "exit $($r.Exit)"
Check 'both locks held, one waiter each' (@($r.Doc.locks | Where-Object { $_.ownerIndex -ne $null -and $_.waiters -eq 1 }).Count -eq 2) (Findings $r)
Check 'two wait edges' (@($r.Doc.waits).Count -eq 2) (Findings $r)
Check 'the deadlock named with both lock types' ((Findings $r) -match 'DEADLOCK \(Monitor\): .*LockBeta.*LockAlpha|DEADLOCK \(Monitor\): .*LockAlpha.*LockBeta') (Findings $r)
Check 'the watched thread is in the cycle' (@($r.Doc.waits | Where-Object { $_.thread -eq (Thread-Of $r).index }).Count -eq 1)
Check 'no GC in progress' (-not $r.Doc.gcInProgress)
Check 'managed stack reaches the lock statement' ((@((Thread-Of $r).managed) -match 'HangDumpTarget.AlphaThenBeta').Count -gt 0)
Check 'runtime still running (heartbeat moved)' $r.Moving

Write-Output 'nativewait'
$r = Run-Scenario 'nativewait'
$t = Thread-Of $r
Check 'dump written and analyzed' ($r.Dump['ok'] -and $r.Exit -eq 0 -and $null -ne $t) "$($r.Dump['error'])"
Check 'native top is the wait' ([string]$t.native[0] -match 'WaitForSingleObject') "$($t.native[0])"
Check 'managed stack names the P/Invoke caller' ((@($t.managed) -match 'HangDumpTarget.NativeWait').Count -gt 0)
Check 'preemptive GC mode in the wait' ($t.gcMode -eq 'Preemptive') $t.gcMode
Check 'no deadlock, no GC' (-not ((Findings $r) -match 'DEADLOCK') -and -not $r.Doc.gcInProgress) (Findings $r)

Write-Output 'gcblock'
$r = Run-Scenario 'gcblock'
$t = Thread-Of $r
Check 'precondition: the runtime stopped (heartbeat frozen)' (-not $r.Moving)
Check 'dump written and analyzed' ($r.Dump['ok'] -and $r.Exit -eq 0 -and $null -ne $t) "$($r.Dump['error'])"
Check 'GC / runtime suspension in progress' ([bool]$r.Doc.gcInProgress) (Findings $r)
Check 'the suspending thread is named' ((Findings $r) -match 'IN PROGRESS: thread \d+ .*SuspendEE') (Findings $r)
Check 'the spinner is the cooperative thread it waits for' (@($r.Doc.cooperative) -contains $t.index -and (Findings $r) -match ("waits for the threads still in cooperative mode.*os 0x{0:x}" -f $r.Watched)) (Findings $r)
Check 'the spinner''s managed stack' ((@($t.managed) -match 'HangDumpTarget.(Step|Spin)').Count -gt 0)
Check 'the spinner is named as stopped outside a system call' ((Findings $r) -match ("stopped outside a system call .*os 0x{0:x}" -f $r.Watched)) (Findings $r)
Check 'before the dump: a thread in Wait/Suspended, no debugger' ($r.Dump['threadStates'].Contains('Wait/Suspended') -and $r.Dump['debuggerAttached'] -eq $false) "$($r.Dump['threadStates'] | ConvertTo-Json -Compress) debugger $($r.Dump['debuggerAttached'])"
Check 'the dump reads no debugger attached' ((Findings $r) -match 'debugger attached to the dumped process: no') (Findings $r)

Write-Output 'exception'
$r = Run-Scenario 'exception'
$t = Thread-Of $r
Check 'dump written and analyzed' ($r.Dump['ok'] -and $r.Exit -eq 0 -and $null -ne $t) "$($r.Dump['error'])"
Check 'managed exception in flight named' ((Findings $r) -match ("managed exception on thread {0} .*InvalidOperationException" -f $t.index)) (Findings $r)
Check 'native exception dispatch named' ((Findings $r) -match ("exception dispatch on the native stack of thread {0} .*RaiseException" -f $t.index)) (Findings $r)
Check 'managed stack reaches the filter' ((@($t.managed) -match 'HangDumpTarget.(Hold|Throw)').Count -gt 0)
Check 'report has the findings and every stack' ($r.Report -match '== findings' -and $r.Report -match '== stacks' -and $r.Report -match '== !syncblk')

Write-Output 'wrapper'
$saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
try { $lines = @(& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Analyze-HangDump.ps1') -Dump (Join-Path $out 'deadlock.dmp') 2>&1 | ForEach-Object { "$_" }); $code = $LASTEXITCODE } finally { $ErrorActionPreference = $saved }
Check 'Analyze-HangDump.ps1 writes <dump>.analysis.txt/.json' ($code -eq 0 -and (Test-Path -LiteralPath (Join-Path $out 'deadlock.dmp.analysis.txt')) -and (Test-Path -LiteralPath (Join-Path $out 'deadlock.dmp.analysis.json'))) ($lines -join ' | ')
Check 'its console output leads with the deadlock' (($lines -join "`n") -match 'DEADLOCK \(Monitor\)')

Write-Output 'runner'
# Run-Map2Acceptance.ps1's own hang rule, dump and analysis (Wait-Probe, Dump-Game, Stop-OwnedGame, Analyze-Dump, read out of the script) on
# the deadlock target, with the process-name guard pointed at it: the CPU-flat rule fires, the dump is written before the kill, the kill ends
# it, and the analysis names the deadlock. The 90 s not-responding rule cannot fire on a console process (it always reads as responding).
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Run-Map2Acceptance.ps1'), [ref]$null, [ref]$null)
$wanted = @('Invoke-Native', 'Dump-Game', 'Analyze-Dump', 'Stop-OwnedGame', 'Wait-Probe')
$defs = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $wanted -contains $n.Name }, $false))
Check 'runner functions found' ($defs.Count -eq $wanted.Count) "$($defs.Count) of $($wanted.Count)"
$run = Join-Path $out 'run'; New-Item -ItemType Directory -Force (Join-Path $run 'logs'), (Join-Path $run 'in-game') | Out-Null
$HangFlatSeconds = 10; $NoHangDump = $false
$name = [IO.Path]::GetFileNameWithoutExtension($target)
foreach ($d in $defs) { . ([ScriptBlock]::Create($d.Extent.Text.Replace("'Bannerlord'", "'$name'").Replace('$PSScriptRoot', "'$PSScriptRoot'"))) }
$psi = New-Object Diagnostics.ProcessStartInfo $target, 'deadlock'
$psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
$p = [Diagnostics.Process]::Start($psi)
try {
    $null = $p.StandardOutput.ReadLine()
    $wait = Wait-Probe $p.Id 3 @{ pollSeconds = 2 }
    $exited = $p.WaitForExit(10000)
}
finally { if (!$p.HasExited) { $p.Kill() } }
Check 'stopped by the CPU-flat rule' ([string]$wait['stopped'] -like 'CPU flat for 10 s*') ([string]$wait['stopped'])
Check 'dumped before the kill (full memory)' ($wait['dump'] -and $wait['dump']['ok'] -and $wait['dump']['path'] -eq "logs/hang-$($p.Id).dmp" -and (Test-Path -LiteralPath (Join-Path $run $wait['dump']['path']))) "$($wait['dump'] | ConvertTo-Json -Compress)"
Check 'killed (taskkill /F /T), exited' ($wait['kill'] -and $wait['kill']['exited'] -and $exited) "$($wait['kill'] | ConvertTo-Json -Compress -Depth 4)"
Check 'children recorded with start times' ($wait['kill'].Contains('children'))
$analysis = Analyze-Dump $p.Id $wait['dump']['path']
Check 'Analyze-Dump: exit 0, the deadlock in its findings' ($analysis['exit'] -eq 0 -and ((@($analysis['findings']) -join "`n") -match 'DEADLOCK \(Monitor\)')) "$($analysis | ConvertTo-Json -Compress -Depth 4)"

Write-Output 'runner: hangrepro phase (stubbed launches)'
# HangRepro, Repro-Outcomes and Mark read out of the script; Launch replaced by a stub that records its arguments and files a launch the
# way Launch does (war: stopped as hung in its step, with a dump; castle: ran its frames). The lab save is copied from -LabSaveFrom.
$wanted = @('HangRepro', 'Repro-Outcomes', 'Mark', 'Sha', 'Now')
$defs = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $wanted -contains $n.Name }, $false))
foreach ($d in $defs) { . ([ScriptBlock]::Create($d.Extent.Text)) }
$reproLine = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$reproVariants' }, $false))[0]
. ([ScriptBlock]::Create($reproLine.Extent.Text))
function Get-Lock { } function Save-State { } function Wait-GameFree { 0 } function Return-Saves { @() }
$script:calls = @()
function Launch([string]$Phase, [hashtable]$ProbeArgs, [int]$TimeoutSeconds, [string[]]$ExtraModules = @(), [hashtable]$Watch = @{}) {
    $script:calls += , ([pscustomobject]@{ Phase = $Phase; Args = $ProbeArgs; Timeout = $TimeoutSeconds; Watch = $Watch })
    $id = 900 + $script:calls.Count; $v = @('w1.hangRepro.war', 'w1.hangRepro.castle' | Where-Object { @($ProbeArgs['skip']) -notcontains $_ })[0]
    $l = [ordered]@{ phase = $Phase; pid = $id; args = $ProbeArgs; result = $null; status = 'no result'; progress = [ordered]@{ lastStage = "map2_acceptance:$v`:settle"; hungStep = $null } }
    if ($v -eq 'w1.hangRepro.war') { $l['wait'] = [ordered]@{ stopped = 'CPU flat for 90 s, not responding in all 18 polls'; dump = [ordered]@{ ok = $true; path = "logs/hang-$id.dmp" }; kill = @{ exited = $true } }; $l['progress']['hungStep'] = $v; $l['analysis'] = [ordered]@{ report = "logs/hang-$id.dmp.analysis.txt" } }
    else {
        $l['wait'] = [ordered]@{ stopped = $null }; $l['result'] = "in-game/map2_acceptance-$id.json"; $l['status'] = 'done'
        $doc = @{ outputs = @{ steps = @{ $v = @{ status = 'done'; blocked = $false; frames = 300; outcome = 'ran: 300 frames' } } } }
        [IO.File]::WriteAllText((Join-Path $run $l['result']), ($doc | ConvertTo-Json -Depth 6))
    }
    $state['launches'] = @($state['launches']) + @($l)
    return $null
}
$saveRoot = Join-Path $out 'saves'; $labFrom = Join-Path $out 'lab-run-saves'; New-Item -ItemType Directory -Force $saveRoot, $labFrom | Out-Null
[IO.File]::WriteAllText((Join-Path $labFrom 'mnbl_acc_test_w1_lab.sav'), 'lab save')
$state = [ordered]@{ phases = [ordered]@{}; launches = @(); saves = [ordered]@{} }
$LabSave = 'mnbl_acc_test_w1_lab'; $LabSaveFrom = $labFrom; $ReproKinds = @('war', 'castle'); $ReproFlatSeconds = 90; $ReproTimeoutSeconds = 900; $Skip = @(); $LabSchool = 'fire'; $Redo = @(); $script:fatal = $null
$null = HangRepro
$c = $script:calls
Check 'one launch per variant' ($c.Count -eq 2) "$($c.Count)"
Check 'war launch: probe phase decallab from the lab save, the lab and castle skipped' ($c[0].Phase -eq 'hangrepro' -and $c[0].Args['phase'] -eq 'decallab' -and $c[0].Args['fromSave'] -eq $LabSave -and (@($c[0].Args['skip']) -contains 'w1.decalLab') -and (@($c[0].Args['skip']) -contains 'w1.hangRepro.castle') -and (@($c[0].Args['skip']) -notcontains 'w1.hangRepro.war')) "$(@($c[0].Args['skip']) -join ',')"
Check 'castle launch: war skipped, castle not' ((@($c[1].Args['skip']) -contains 'w1.hangRepro.war') -and (@($c[1].Args['skip']) -notcontains 'w1.hangRepro.castle')) "$(@($c[1].Args['skip']) -join ',')"
Check 'the 90 s / 5 s rule passed to each launch' ($c[0].Watch['flatSeconds'] -eq 90 -and $c[1].Watch['pollSeconds'] -eq 5 -and $c[0].Timeout -eq 900)
Check 'the lab save copied into Game Saves and recorded' ((Test-Path -LiteralPath (Join-Path $saveRoot 'mnbl_acc_test_w1_lab.sav')) -and $state['labSaveCopied']['save'] -eq 'mnbl_acc_test_w1_lab.sav')
$h = $state['phases']['hangrepro']
Check 'phase done: war engine blocked with its dump and analysis, castle ran' ($h['status'] -eq 'done' -and $h['detail']['hangRepro']['w1.hangRepro.war']['outcome'] -eq 'engine blocked' -and $h['detail']['hangRepro']['w1.hangRepro.war']['dump']['path'] -eq 'logs/hang-901.dmp' -and $h['detail']['hangRepro']['w1.hangRepro.castle']['outcome'] -eq 'ran') "$($h | ConvertTo-Json -Depth 8 -Compress)"

Write-Output 'runner: crashed variants and the bisect list (MMR-247 X)'
# The fault event Windows logged for acc5rep's castle launch (Application Error 1000), as Crash-Event reads it.
$wanted = @('Parse-CrashEvent')
$defs = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $wanted -contains $n.Name }, $false))
foreach ($d in $defs) { . ([ScriptBlock]::Create($d.Extent.Text)) }
$sample = "Faulting application name: Bannerlord.exe, version: 1.0.0.0, time stamp: 0x6a732508`r`nFaulting module name: TaleWorlds.Native.dll, version: 0.0.0.0, time stamp: 0x6a732505`r`nException code: 0xc0000005`r`nFault offset: 0x000000000055e625`r`nFaulting process id: 0x1FFC`r`nFaulting application start time: 0x1DD615EB7238757"
$parsed = Parse-CrashEvent $sample
Check 'Parse-CrashEvent: module, time stamp, code, offset, process id' ($parsed['application'] -eq 'Bannerlord.exe' -and $parsed['module'] -eq 'TaleWorlds.Native.dll' -and $parsed['moduleTimeStamp'] -eq '0x6a732505' -and $parsed['exceptionCode'] -eq '0xc0000005' -and $parsed['faultOffset'] -eq '0x000000000055e625' -and $parsed['processId'] -eq 8188) "$($parsed | ConvertTo-Json -Compress)"
# The bisect's default list: every name a known probe variant, the control first.
$param = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.ParameterAst] -and $n.Name.VariablePath.UserPath -eq 'ReproKinds' }, $true))[0]
$defaults = @(& ([ScriptBlock]::Create($param.DefaultValue.Extent.Text)))
Check 'default -ReproKinds: control, fix and three bisect variants and the castle fix, all known' ($defaults.Count -eq 6 -and $defaults[0] -eq 'warControl' -and @($defaults | Where-Object { $reproVariants -notcontains "w1.hangRepro.$_" }).Count -eq 0) "$($defaults -join ',')"
# A launch whose game exited by itself with the control's step open is 'crashed', with the fault event; the war (fix) ran.
function Launch([string]$Phase, [hashtable]$ProbeArgs, [int]$TimeoutSeconds, [string[]]$ExtraModules = @(), [hashtable]$Watch = @{}) {
    $script:calls += , ([pscustomobject]@{ Phase = $Phase; Args = $ProbeArgs; Timeout = $TimeoutSeconds; Watch = $Watch })
    $id = 900 + $script:calls.Count; $v = @($reproVariants | Where-Object { @($ProbeArgs['skip']) -notcontains $_ })[0]
    $l = [ordered]@{ phase = $Phase; pid = $id; args = $ProbeArgs; result = $null; status = 'running'; progress = [ordered]@{ lastStage = "map2_acceptance:$v`:settle"; hungStep = $null; openStep = $null }; wait = [ordered]@{ stopped = $null } }
    if ($v -eq 'w1.hangRepro.warControl') { $l['progress']['openStep'] = $v; $l['crash'] = Parse-CrashEvent $sample }
    else {
        $l['result'] = "in-game/map2_acceptance-$id.json"; $l['status'] = 'done'
        $doc = @{ outputs = @{ steps = @{ $v = @{ status = 'done'; blocked = $false; frames = 300; outcome = 'ran: 300 frames' } } } }
        [IO.File]::WriteAllText((Join-Path $run $l['result']), ($doc | ConvertTo-Json -Depth 6))
    }
    $state['launches'] = @($state['launches']) + @($l)
    return $null
}
$script:calls = @(); $state = [ordered]@{ phases = [ordered]@{}; launches = @(); saves = [ordered]@{} }; $ReproKinds = @('warControl', 'war')
$null = HangRepro
$c = $script:calls; $h = $state['phases']['hangrepro']['detail']['hangRepro']
Check 'two launches, each skipping every other variant' ($c.Count -eq 2 -and @($c[0].Args['skip']) -contains 'w1.hangRepro.war' -and @($c[0].Args['skip']) -notcontains 'w1.hangRepro.warControl' -and @($c[1].Args['skip']) -contains 'w1.hangRepro.warControl' -and @($c[1].Args['skip']) -contains 'w1.hangRepro.warNoMagic') "$(@($c[0].Args['skip']) -join ',')"
Check 'warControl crashed: the fault module and offset; war ran' ($h['w1.hangRepro.warControl']['outcome'] -eq 'crashed' -and $h['w1.hangRepro.warControl']['detail'] -eq '0xc0000005 in TaleWorlds.Native.dll+0x000000000055e625' -and $h['w1.hangRepro.war']['outcome'] -eq 'ran' -and $state['phases']['hangrepro']['status'] -eq 'done') "$($h | ConvertTo-Json -Depth 6 -Compress)"

# The dumps are tens of MB each: removed when everything passed.
if ($script:fail -eq 0) { Get-ChildItem -LiteralPath $out -Filter '*.dmp' -Recurse | ForEach-Object { [IO.File]::Delete($_.FullName) } }
Write-Output "Hang dump harness: $($script:pass)/$($script:pass + $script:fail) ($out)"
if ($script:fail -gt 0) { exit 1 }
