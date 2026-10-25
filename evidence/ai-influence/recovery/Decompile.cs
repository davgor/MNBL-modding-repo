using System;
using System.IO;
using System.Linq;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
var settings=new DecompilerSettings { AsyncAwait=false, YieldReturn=false, ThrowOnAssemblyResolveErrors=false };
var dec=new CSharpDecompiler(args[0],settings);
Directory.CreateDirectory(args[1]);
foreach(var t in dec.TypeSystem.MainModule.TopLevelTypeDefinitions.Where(t=>t.FullName.StartsWith("AIInfluence"))) {
 try { var s=dec.DecompileTypeAsString(new FullTypeName(t.FullName)); File.WriteAllText(Path.Combine(args[1],t.FullName+".cs"),s); Console.WriteLine("OK "+t.FullName); }
 catch(Exception e){Console.WriteLine("FAIL "+t.FullName+" "+e.Message);}
}
