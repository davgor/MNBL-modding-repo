# Static proxy and political-call recovery

These utilities operate on the original downloaded DLL as data. They do not invoke the target assembly, module initializer, proxy binder, generated dynamic methods or custom attributes. They produce an analysis-only derived DLL and presentation C#, not a replacement mod.

Prerequisites: .NET 8 SDK; original AIInfluence v6.0.2 input pinned by `../receipt.json`; dnlib 4.5.0 and ICSharpCode.Decompiler 9.1.0.7988. The existing first-pass `build/research-ai-influence/readable` enum file and the independently recovered `strings/decoded-strings.json` are inputs to the final map/render steps.

From repository root:

```powershell
$sdk = 'C:/Users/davgo/AppData/Local/SummerEngine/current/dotnet/dotnet.exe'
$target = 'D:/SteamLibrary/steamapps/workshop/content/261550/3584621421/bin/Win64_Shipping_Client/AIInfluence.dll'
$calls = 'build/research-ai-influence/calls'
# First pass exports original IL, field signature blobs/attributes and metadata references.
& $sdk run --project evidence/ai-influence/recovery/CallInspect.csproj -- $target "$calls/out" skip
python evidence/ai-influence/recovery/CallDecode.py $calls
# Second pass validates exact target/delegate signatures and names proxy fields.
& $sdk run --project evidence/ai-influence/recovery/CallInspect.csproj -- $target "$calls/out"
python evidence/ai-influence/recovery/CallMap.py $calls
python evidence/ai-influence/recovery/CallRender.py $calls
```

`CallDecode.py` is a restricted interpreter for the straight-line token construction prefixes of the sixteen RuntimeFieldHandle/byte binders, not a general IL runner. It reads field-name character values, raw signature bytes, optional modifier metadata token, and the arithmetic constructor/getter of the marker attribute. It stops at the binder's ResolveMethod call and resolves the resulting token in the original module's metadata. All arithmetic has explicit 32-bit wrapping. Unknown operations fail rather than execute or guess. The encoded opcode is independently checked as call, callvirt or newobj. Every incoming CFG predecessor is checked to establish the same field and literal argument at every binder site, traversing only nop/unconditional-branch predecessors; cycles or unexpected producers fail. Initializers with exception regions are rejected. All 1,420 sites pass these input-flow checks (971 initializer methods, no EH regions); two initializers contain flattened control flow, so physical adjacency alone is not used as proof.

`CallInspect.cs` compares each decoded target's parameter and return types with its delegate's Invoke signature. Instance calls add the receiver; constructors use their constructed type as the return. It separately records complete original token/offset/branch information before any name changes. All control-format names and problematic presentation names are replaced with token aliases; proxy fields receive target-based aliases. Recovery of names is not recovery of the author's original source names.

`CallMap.py` expands exact pass-through proxy wrappers and identifies every delegate field-load reference. These are static references, not execution observations. Its political boundary seeds all methods under AIInfluence.Diplomacy, AIInfluence.DynamicEvents, AIInfluence.ResponseActions and AIInfluence.AIInfluenceBehavior, then follows resolvable in-module method references, excluding recursion into the global module's decoder machinery. This deliberately broad closure includes dependencies outside politics; it does not claim each referenced method executes during diplomacy.

The political dispatcher is original token `06002A60`, called from `DiplomacyManager.ExecuteDiplomaticAction` (`060023AA`) at IL_0137. Its first switch at IL_000F indexes DiplomaticAction minus one. All 27 non-None cases are mapped to handler tokens and original offsets in `diplomatic-routes.json`. None/default handling remains separate; artificial control-flow flattening is retained in readable C#.

`CallRender.py` combines this broader recovered source with the string agent's original-token decoded strings. `calls/recovered/` is the combined human-readable output, including the formerly omitted non-AIInfluence helper types. Ordinary delegate/reflection behavior, runtime branch feasibility, game state, compatibility and whole-program semantic equivalence are not established by these static transformations.

Observed v6.0.2 results: 1,420 bound fields, 1,420 resolved targets, 1,420 exact signature matches; 385 exact pass-through wrappers; 25,004 static proxy references, of which 10,138 are in the broad political closure. All 931 reached top-level types (including the renamed global decoder type) decompiled with zero per-type failures; 16,241 matching encrypted-string expressions restored in that output with zero unmapped expressions. The original first-pass export's 588 types was a namespace filter, not the complete helper inventory.

For an independent export/decode/signature-only verification, use a separate output folder, e.g. `build/research-ai-influence/calls-verify/out`, and pass `skip` on **both** CallInspect invocations. The second invocation reads that folder's `proxy-bindings.json` and writes `proxy-signature-validation.json` before exiting. This avoids repeating C# presentation work. CallMap's enum input path expects the existing sibling `readable` first-pass directory, as in the commands above. There are no observed `calli` instructions or binder calls outside static constructors in this assembly; other runtime reflection/virtual dispatch is not thereby proven resolved.
