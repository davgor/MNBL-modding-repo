using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
var m=ModuleDefMD.Load(args[0]); var output=Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
var methods=m.GetTypes().SelectMany(t=>t.Methods).ToArray();
var all=methods.Select(x=> new {token=x.MDToken.ToString(),type=x.DeclaringType.FullName,name=x.Name.ToString(),signature=x.FullName,exceptionHandlerCount=x.Body?.ExceptionHandlers.Count??0,instructions=x.HasBody?x.Body.Instructions.Select(i=>new {offset=i.Offset,op=i.OpCode.Name,operand=i.Operand?.ToString(),targetToken=(i.Operand as IMDTokenProvider)?.MDToken.ToString(),branch=i.Operand is Instruction j?(uint?)j.Offset:null,branches=i.Operand is IList<Instruction> a?a.Select(v=>v.Offset).ToArray():null}).ToArray():null}).ToArray();
File.WriteAllText(Path.Combine(output,"original-instructions.json"),JsonSerializer.Serialize(all));
var seeds=methods.Where(x=>x.DeclaringType.FullName.StartsWith("AIInfluence.Diplomacy")||x.DeclaringType.FullName.StartsWith("AIInfluence.DynamicEvents")||x.DeclaringType.FullName.StartsWith("AIInfluence.ResponseActions")||x.DeclaringType.FullName=="AIInfluence.AIInfluenceBehavior").ToArray();
var visited=new HashSet<MethodDef>();var todo=new Queue<MethodDef>(seeds);var edges=new List<object>();
while(todo.Count>0) {var x=todo.Dequeue();if(!visited.Add(x)||!x.HasBody)continue;foreach(var i in x.Body.Instructions){if(i.Operand is dnlib.DotNet.IMethod im){var to=im.ResolveMethodDef();if(to?.Module==m){edges.Add(new{source=x.MDToken.ToString(),offset=i.Offset,op=i.OpCode.Name,target=to.MDToken.ToString(),targetSignature=to.FullName});if(to.DeclaringType.FullName!="<Module>")todo.Enqueue(to);}}}}
File.WriteAllText(Path.Combine(output,"political-reachable-edges.json"),JsonSerializer.Serialize(edges,new JsonSerializerOptions{WriteIndented=true}));
string FieldSigHex(FieldDef f){m.Metadata.TablesStream.TryReadFieldRow(f.Rid,out var row);return Convert.ToHexString(m.Metadata.BlobStream.Read(row.Signature));}
var fs=m.GetTypes().SelectMany(t=>t.Fields).Where(f=>f.CustomAttributes.Count>0).Select(f=>new {token=f.MDToken.ToString(),name=f.Name.ToString(),type=f.DeclaringType.FullName,sig=FieldSigHex(f),modifier=(f.FieldSig.Type as ModifierSig)?.Modifier.MDToken.ToString(),attributes=f.CustomAttributes.Select(a=>new {type=a.AttributeType.FullName,ctor=a.Constructor.MDToken.ToString(),args=a.ConstructorArguments.Select(c=>c.Value?.ToString()).ToArray()}).ToArray()}).ToArray();
File.WriteAllText(Path.Combine(output,"fields.json"),JsonSerializer.Serialize(fs));
File.WriteAllText(Path.Combine(output,"memberrefs.json"),JsonSerializer.Serialize(m.GetMemberRefs().Select(x=>new {token=x.MDToken.ToString(),signature=x.FullName}).ToArray()));

var validations=new List<object>();
var bindingsPath=Path.Combine(output,"proxy-bindings.json");
var bindingNames=new Dictionary<uint,string>();
if(File.Exists(bindingsPath))foreach(var b in JsonDocument.Parse(File.ReadAllText(bindingsPath)).RootElement.EnumerateArray()){
 var ft=Convert.ToUInt32(b.GetProperty("fieldToken").GetString(),16);var tt=Convert.ToUInt32(b.GetProperty("targetToken").GetString(),16);
 var f=(FieldDef)m.ResolveToken(ft);var target=(dnlib.DotNet.IMethod)m.ResolveToken(tt);var inv=f.DeclaringType.FindMethod("Invoke");var callcode=b.GetProperty("encodedCallOpcode").GetString();
 var expected=target.MethodSig.Params.Select(x=>x.FullName).ToList();
 string ret=target.MethodSig.RetType.FullName;
 if(callcode=="0x73")ret=target.DeclaringType.FullName;
 else if(target.MethodSig.HasThis)expected.Insert(0,target.DeclaringType.FullName);
 var actual=inv.MethodSig.Params.Select(x=>x.FullName).ToArray();
 bool exact=expected.SequenceEqual(actual)&&ret==inv.MethodSig.RetType.FullName;
 validations.Add(new{fieldToken=b.GetProperty("fieldToken").GetString(),targetToken=b.GetProperty("targetToken").GetString(),targetSignature=target.FullName,expectedParameters=expected,delegateParameters=actual,expectedReturn=ret,delegateReturn=inv.MethodSig.RetType.FullName,exactSignatureMatch=exact,argumentCountMatch=expected.Count==actual.Length});
 bindingNames[ft]="Proxy_"+System.Text.RegularExpressions.Regex.Replace(target.DeclaringType.Name+"_"+target.Name,"[^A-Za-z0-9_]","_")+"_"+f.MDToken;
}
File.WriteAllText(Path.Combine(output,"proxy-signature-validation.json"),JsonSerializer.Serialize(validations,new JsonSerializerOptions{WriteIndented=true}));
var rename=new List<object>();bool Weird(string s)=>s.Any(c=>char.GetUnicodeCategory(c)==System.Globalization.UnicodeCategory.Format||c==','||c=='"'||c=='\\'||c<32);
foreach(var t in m.GetTypes()) {if(Weird(t.Name)){rename.Add(new{kind="type",token=t.MDToken.ToString(),original=t.FullName,replacement="RecoveredType_"+t.MDToken});t.Name="RecoveredType_"+t.MDToken;}if(Weird(t.Namespace))t.Namespace="Recovered";
foreach(var x in t.Methods)if(Weird(x.Name)){rename.Add(new{kind="method",token=x.MDToken.ToString(),original=x.Name.ToString(),replacement="RecoveredMethod_"+x.MDToken});x.Name="RecoveredMethod_"+x.MDToken;}
foreach(var x in t.Properties)if(Weird(x.Name))x.Name="RecoveredProperty_"+x.MDToken;
foreach(var x in t.Fields)if(Weird(x.Name)){rename.Add(new{kind="field",token=x.MDToken.ToString(),original=x.Name.ToString(),replacement="RecoveredField_"+x.MDToken});x.Name="RecoveredField_"+x.MDToken;}}
File.WriteAllText(Path.Combine(output,"renames.json"),JsonSerializer.Serialize(rename,new JsonSerializerOptions{WriteIndented=true}));
foreach(var t in m.GetTypes())foreach(var f in t.Fields)if(bindingNames.TryGetValue(f.MDToken.Raw,out var bn))f.Name=bn;
foreach(var method in methods){var il=method.Body?.Instructions;if(il==null||il.Count<3||il[0].OpCode!=OpCodes.Ldsfld||!(il[0].Operand is FieldDef f)||!bindingNames.TryGetValue(f.MDToken.Raw,out var bn)||il[il.Count-1].OpCode!=OpCodes.Ret||il[il.Count-2].OpCode!=OpCodes.Call||!il.Skip(1).Take(il.Count-3).All(i=>i.OpCode.Name.StartsWith("ldarg")))continue;method.Name=bn+"_wrapper_"+method.MDToken;}
m.GlobalType.Name="RecoveredGlobal"; var derived=Path.Combine(output,"AIInfluence.calls-readable.dll");m.Write(derived,new ModuleWriterOptions(m){Logger=DummyLogger.NoThrowInstance});
var types=visited.Select(x=>{var t=x.DeclaringType;while(t.DeclaringType!=null)t=t.DeclaringType;return t.FullName;}).Append("RecoveredGlobal").Distinct().ToArray();
if(args.Length>2)return; var dec=new CSharpDecompiler(derived,new DecompilerSettings{AsyncAwait=false,YieldReturn=false,ThrowOnAssemblyResolveErrors=false});
Directory.CreateDirectory(Path.Combine(output,"readable"));int ok=0,fail=0;
foreach(var type in types){try{File.WriteAllText(Path.Combine(output,"readable",type.Replace('/','_')+".cs"),dec.DecompileTypeAsString(new FullTypeName(type)));ok++;}catch(Exception ex){Console.WriteLine("FAIL "+type+" "+ex.Message);fail++;}}
File.WriteAllText(Path.Combine(output,"coverage.json"),JsonSerializer.Serialize(new{seedMethods=seeds.Length,reachableMethods=visited.Count,staticEdges=edges.Count,types=types.Length,decompiled=ok,failures=fail,renamedIdentifiers=rename.Count,limitations=new[]{"Static resolved calls only; dynamic reflection/delegate targets are not automatically established.","Reachability includes calls irrespective of branch feasibility.","Renamed analysis copy is not product source or executable replacement."}},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"OK {ok} FAIL {fail} EDGES {edges.Count}");


