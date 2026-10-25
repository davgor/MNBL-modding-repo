# Static string recovery

The original hash-pinned DLL stores a 164,608-byte encrypted constant field at RVA 8264/file offset 584 (field token 04000002). Module method 06000017 initializes its key with 20020359, uses 16 uint32 xorshift-derived lanes, XORs each 16-word block and feeds each plaintext word back into its lane key. The decoded buffer contains five LZMA properties, a four-byte output length, then raw LZMA1. The recovered pool is 625,952 bytes. No target code or module initializer is executed.

Five generic decoder methods (06000018–0600001C) transform a literal id by multiplication/XOR, take its upper two bits as a type tag and multiply its remaining 30 bits by four for the pool offset. The string arm reads a little-endian length and UTF-8 bytes. `StringDecode.py` records their exact constants and expected string tags.

`StringExport.cs` reads the original through dnlib, extracts the exact initialization field, and examines **all incoming CFG predecessors**, following only nop/unconditional branches until the integer argument literal. Any nonliteral predecessor or conflicting literal is recorded as a failure, and the Python decoder refuses failures. This avoids assuming the previous physical instruction necessarily supplies the argument.

From repository root (existing .NET 8 SDK; dnlib package pinned to 4.5.0):

```powershell
$sdk = 'C:/Users/davgo/AppData/Local/SummerEngine/current/dotnet/dotnet.exe'
$target = 'D:/SteamLibrary/steamapps/workshop/content/261550/3584621421/bin/Win64_Shipping_Client/AIInfluence.dll'
$output = Join-Path (Get-Location) 'build/research-ai-influence/strings'
& $sdk run --project evidence/ai-influence/recovery/StringExport.csproj -- $target $output
python evidence/ai-influence/recovery/StringDecode.py $output
python evidence/ai-influence/recovery/StringRender.py build/research-ai-influence
```

The last command uses the earlier `original-methods.json` and decompiled `/readable` files. Raw plaintext and recovered C# remain in ignored build output. The export/decoder only needs the original DLL; it does not require decompiled source.

Verified result: 18,701 direct encrypted-string call sites, 11,086 distinct plaintext values, no predecessor/tag/range/UTF-8 failures. Each result carries original method token, IL offset, decoder token, integer argument, pool offset and plaintext. Restoring literals to the earlier 588 AIInfluence C# files replaces 18,654 expressions; remaining recovered calls occur outside that presentation scope.

Limit: this exhausts the statically present direct generic string uses of these five decoders. It does not establish absence of hypothetical reflection-only/computed uses, runtime equivalence, or correctness of every decompiled statement. All outputs are analysis artifacts, not replacement mod files.
