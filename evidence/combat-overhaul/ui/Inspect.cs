using System;
using System.IO;
using System.Linq;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
var dec=new CSharpDecompiler(args[0],new DecompilerSettings { ThrowOnAssemblyResolveErrors=false });
foreach(var t in dec.TypeSystem.MainModule.TopLevelTypeDefinitions.Where(t=>args.Skip(2).Any(q=>t.FullName.Contains(q,StringComparison.OrdinalIgnoreCase)))) {
 if(args[1]=="list") { Console.WriteLine(t.FullName); continue; }
 Directory.CreateDirectory(args[1]);
 try{File.WriteAllText(Path.Combine(args[1],t.FullName+".cs"),dec.DecompileTypeAsString(new FullTypeName(t.FullName)));Console.WriteLine(t.FullName);}catch(Exception e){Console.WriteLine(e.Message);}
}
