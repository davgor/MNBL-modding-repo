using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Writer;
var m = ModuleDefMD.Load(args[0]);
Console.WriteLine($"Module={m.Name}; MVID={m.Mvid}; Types={m.Types.Count}");
foreach(var t in m.GetTypes()) Console.WriteLine(t.FullName);
var methods = m.GetTypes().SelectMany(t => t.Methods).Select(x => new {
 token=x.MDToken.ToString(), type=x.DeclaringType.FullName, name=x.Name.ToString(), signature=x.FullName,
 instructions=x.HasBody ? x.Body.Instructions.Select(i=>new {offset=i.Offset, op=i.OpCode.Name, operand=i.Operand?.ToString()}).ToArray() : null
}).ToArray();
File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[1])!, "original-methods.json"), JsonSerializer.Serialize(methods));
var options = new ModuleWriterOptions(m);
options.Logger = DummyLogger.NoThrowInstance;
m.Write(args[1], options);
var copy=ModuleDefMD.Load(args[1]);
var originalMethods=m.GetTypes().SelectMany(t=>t.Methods).ToArray();
var copyMethods=copy.GetTypes().SelectMany(t=>t.Methods).ToArray();
string Fingerprint(MethodDef x)=>x.FullName+"\n"+string.Join("\n",x.Body?.Instructions.Select(i=>i.OpCode.Name+" "+i.Operand?.ToString()) ?? Array.Empty<string>());
var originalFingerprints=originalMethods.Select(Fingerprint).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
var copyFingerprints=copyMethods.Select(Fingerprint).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[1])!,"normalization-audit.json"),JsonSerializer.Serialize(new { originalMethods=originalMethods.Length,derivedMethods=copyMethods.Length, methodSignaturesAndDecodedInstructionsEqual=originalFingerprints.SequenceEqual(copyFingerprints), limitation="Comparison covers signatures and decoded instruction operands, not whole-assembly equivalence or execution."}, new JsonSerializerOptions {WriteIndented=true}));
