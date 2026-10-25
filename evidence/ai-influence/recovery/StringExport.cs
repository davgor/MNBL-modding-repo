using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
var input=Path.GetFullPath(args[0]);var output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
var hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))).ToLowerInvariant();
if(hash!="472658f15a0ee00b6b05904830fda257593653f74fdf3c8fec1345267a73862d")throw new Exception("This decoder is pinned to the investigated artifact.");
var module=ModuleDefMD.Load(input);
var init=(MethodDef)module.ResolveToken(0x06000017);
var field=(FieldDef)init.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldtoken).Operand;
File.WriteAllBytes(Path.Combine(output,"encrypted-constants.bin"),field.InitialValue);
var records=new List<object>();var failures=new List<object>();
foreach(var m in module.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.HasBody)){
 var il=m.Body.Instructions;var pred=il.ToDictionary(i=>i,i=>new List<Instruction>());
 for(int k=0;k<il.Count;k++){
  var i=il[k];
  if(i.Operand is Instruction dest)pred[dest].Add(i);
  else if(i.Operand is IList<Instruction> dests)foreach(var d in dests)pred[d].Add(i);
  if(k+1<il.Count && i.OpCode.FlowControl!=FlowControl.Branch && i.OpCode.FlowControl!=FlowControl.Return && i.OpCode.FlowControl!=FlowControl.Throw)pred[il[k+1]].Add(i);
 }
 foreach(var i in il){
  if(i.OpCode!=OpCodes.Call || i.Operand is not MethodSpec ms || ms.GenericInstMethodSig.GenericArguments.Count!=1 || ms.GenericInstMethodSig.GenericArguments[0].FullName!="System.String")continue;
  var decoder=ms.Method.ResolveMethodDef();
  if(decoder==null||decoder.DeclaringType!=module.GlobalType ||decoder.MDToken.Raw<0x06000018||decoder.MDToken.Raw>0x0600001c)continue;
  var seen=new HashSet<Instruction>();var literals=new HashSet<int>();var q=new Queue<Instruction>(pred[i]);var bad=new List<string>();
  while(q.Count>0){var p=q.Dequeue();if(!seen.Add(p))continue;
   if(p.IsLdcI4())literals.Add(p.GetLdcI4Value());
   else if(p.OpCode==OpCodes.Nop||p.OpCode==OpCodes.Br||p.OpCode==OpCodes.Br_S){foreach(var z in pred[p])q.Enqueue(z);}
   else bad.Add(p.Offset+":"+p.OpCode.Name);
  }
  if(literals.Count!=1||bad.Count!=0){failures.Add(new{methodToken=m.MDToken.ToString(),offset=i.Offset,literals,bad});continue;}
  records.Add(new{methodToken=m.MDToken.ToString(),type=m.DeclaringType.FullName,method=m.Name.ToString(),ilOffset=i.Offset,decoderToken=decoder.MDToken.ToString(),argument=literals.Single(),predecessorsInspected=seen.Count});
 }
}
var obj=new{originalSha256=hash,initializerToken=init.MDToken.ToString(),initializerFieldToken=field.MDToken.ToString(),fieldRva=(uint)field.RVA,fieldFileOffset=(uint)module.Metadata.PEImage.ToFileOffset(field.RVA),fieldBytes=field.InitialValue.Length,fieldSha256=Convert.ToHexString(SHA256.HashData(field.InitialValue)).ToLowerInvariant(),calls=records,failures};
File.WriteAllText(Path.Combine(output,"static-inputs.json"),JsonSerializer.Serialize(obj,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(new{calls=records.Count,failures=failures.Count,fieldBytes=field.InitialValue.Length}));
