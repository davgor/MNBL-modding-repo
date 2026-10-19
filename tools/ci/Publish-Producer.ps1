# Publishes the pinned FWGC++ producer (tools.lock.json "producer") as this repository's fwg-producer/<commit>
# prerelease, which tools/ci/Build-ProceduralWorld.ps1 downloads on GitHub-hosted runners. Run once per producer pin.
param([string]$Binary)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$Binary) { $Binary = Join-Path $repo 'build/producer/apps/Release/fwg.exe' }
$lock = Get-Content -LiteralPath "$repo/tools.lock.json" -Raw | ConvertFrom-Json
$hash = (Get-FileHash -LiteralPath $Binary -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $lock.producer.sha256) { throw "$Binary is $hash; tools.lock.json pins $($lock.producer.sha256). Rebuild it with tools/Build-Fwg.ps1." }
$short = $lock.producer.sourceCommit.Substring(0, 12)
$tag = "fwg-producer/$short"
$existing = @(gh release list --limit 500 --json tagName --jq '.[].tagName')
if ($LASTEXITCODE) { throw 'gh release list failed; run gh auth login.' }
if ($existing -contains $tag) { Write-Output "Already published: $tag"; return }
$notes = "Pinned FWGC++ producer built from FWGC- commit $($lock.producer.sourceCommit) (SHA-256 $hash). Build input for MNBL.ProceduralWorld releases; not a mod."
gh release create $tag $Binary --title "FWGC++ producer $short" --notes $notes --prerelease --latest=false --target main
if ($LASTEXITCODE) { throw "gh release create $tag failed." }
Write-Output "Published $tag"
