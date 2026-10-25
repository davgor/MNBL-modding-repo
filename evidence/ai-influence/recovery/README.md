# Static recovery reproduction

Run from the repository root using a .NET 8 SDK. These utilities parse metadata/IL and reconstruct C#; they do not CLR-load or execute the target. Packages are dnlib 4.5.0 and ICSharpCode.Decompiler 9.1.0.7988. Keep output under ignored `build/`; never overwrite the downloaded DLL or deploy the normalized copy.

```powershell
$sdk = 'C:/Users/davgo/AppData/Local/SummerEngine/current/dotnet/dotnet.exe'
$target = 'D:/SteamLibrary/steamapps/workshop/content/261550/3584621421/bin/Win64_Shipping_Client/AIInfluence.dll'
$analysis = Join-Path (Get-Location) 'build/research-ai-influence-repro'
New-Item -ItemType Directory -Force $analysis | Out-Null
& $sdk run --project evidence/ai-influence/recovery/normalize.csproj -- $target "$analysis/AIInfluence.normalized.dll" > "$analysis/types.txt"
& $sdk run --project evidence/ai-influence/recovery/decompile.csproj -- "$analysis/AIInfluence.normalized.dll" "$analysis/readable" > "$analysis/decompile-log.txt"
Get-Content "$analysis/normalization-audit.json"
Select-String -Path "$analysis/decompile-log.txt" -Pattern '^FAIL'
```

Verify the input SHA-256 against `../receipt.json` first. The normalizer writes original-token instruction JSON and checks all decoded instruction/signature fingerprints against its output. This comparison does not cover exception regions, metadata equivalence or runtime semantics. Writer diagnostics are tolerated for reconstruction; the output is analysis-only. Decompiled C# may retain obfuscated expressions, unknown types and encrypted-string helper calls.

REA invocation on original: `inspect_managed_artifact` with explicit original path and summary detail. It rejects duplicate #GUID. Invoke the same operation on the derived path to inspect its normalized metadata, preserving its distinct SHA-256 and provenance. Never present the derived copy's Evidence as direct proof of original runtime behavior.

The archived hook inventory is a selected original-IL call filter over `call`, `callvirt`, `newobj` for TaleWorlds action/event/decision/model APIs; it is not complete call-graph coverage. Original method JSON contains the rest for follow-up inspection.
