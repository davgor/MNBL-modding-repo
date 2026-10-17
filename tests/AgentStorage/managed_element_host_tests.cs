using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using MNBL.AgentGrowthProbe;

static class ManagedElementHostTests
{
    static int checks;
    static void Check(bool value, string message) { ++checks; if (!value) throw new Exception(message); }
    static void Reject(Action action, string message)
    {
        bool rejected = false; try { action(); } catch { rejected = true; }
        Check(rejected, message);
    }
    static ProbeResult Valid()
    {
        return new ProbeResult { abi_size=712, abi_version=1, status=1, stage=8, capacity=90, pointer_checks=600, header_checks=450,
            allocation_attempts=21, allocations=20, releases=20, constructor_attempts=488,
            constructed=new ulong[] {120,97,90,90,90}, destroyed=new ulong[] {120,97,90,90,90},
            allocation_failure_checked=1, constructor_failure_checked=1, exhaustion_checked=1, reuse_checked=1, error=new byte[512] };
    }
    static int Main(string[] args)
    {
        try
        {
            Check(args.Length==2, "DLL and hash arguments");
            Check(Marshal.SizeOf(typeof(ProbeResult))==712 && Marshal.OffsetOf(typeof(ProbeResult), "error").ToInt32()==200, "ABI");
            ProbeInterop.ValidateSuccess(0, Valid()); ++checks;
            foreach (var field in typeof(ProbeResult).GetFields().Where(f=>f.FieldType==typeof(ulong)))
            {
                object value=Valid(); field.SetValue(value, (ulong)field.GetValue(value)+1);
                Reject(()=>ProbeInterop.ValidateSuccess(0,(ProbeResult)value), "scalar mismatch: "+field.Name);
            }
            for (int i=0;i<5;++i)
            {
                var r=Valid();++r.constructed[i];Reject(()=>ProbeInterop.ValidateSuccess(0,r), "construction mismatch");
                r=Valid();++r.destroyed[i];Reject(()=>ProbeInterop.ValidateSuccess(0,r), "cleanup mismatch");
            }
            Reject(()=>ProbeInterop.ValidateSuccess(3,Valid()), "nonzero return");
            var err=Valid();err.error[0]=65;Reject(()=>ProbeInterop.ValidateSuccess(0,err), "native error");
            Reject(()=> {int code;ProbeInterop.Call(args[0],new string('0',64),out code);}, "DLL hash mismatch");
            for (int repeat=0;repeat<2;++repeat)
            {
                int code;var r=ProbeInterop.Call(args[0],args[1],out code);
                Check(code==3 && r.abi_size==712 && r.abi_version==1 && r.status==2 && r.stage==0 && r.ErrorText=="Native module is not loaded", "no-engine rejection");
                Check(r.capacity==0 && r.pointer_checks==0 && r.header_checks==0 && r.allocation_attempts==0 && r.allocations==0 && r.releases==0 && r.constructor_attempts==0 &&
                    r.constructed.All(n=>n==0) && r.destroyed.All(n=>n==0) && r.allocation_failure_checked==0 && r.constructor_failure_checked==0 && r.exhaustion_checked==0 && r.reuse_checked==0,
                    "no-engine counters");
            }
            string host=Assembly.GetExecutingAssembly().Location;
            string directory=Path.Combine(Path.GetDirectoryName(host),"request-tests",Guid.NewGuid().ToString("D")); Directory.CreateDirectory(directory);
            string requestPath=Path.Combine(directory,"request-"+Path.GetFileName(directory)+".json");
            string ownerPath=Path.Combine(directory,"owner.json");
            var request=new ProbeRequest {schemaVersion=1,runId=Path.GetFileName(directory),createdUtc=DateTime.UtcNow.ToString("O"),gameExecutable=host,
                nativeDll=Path.Combine(Path.GetDirectoryName(host),"MNBL.NativeElementProbe.dll"),nativeSha256=args[1],hostSha256=ProbeInterop.Hash(host),
                ownerPath=ownerPath,claimPath=Path.Combine(directory,"claimed.json"),resultPath=Path.Combine(directory,"result.json")};
            File.WriteAllText(requestPath,ProbeRequest.Json.Serialize(request));
            File.WriteAllText(ownerPath,ProbeRequest.Json.Serialize(new ProbeRequest.Owner {runId=request.runId,pid=123,startTicks=456}));
            Func<ProbeRequest> read=()=>ProbeRequest.ReadAndValidate(requestPath,host,123,456,host,DateTime.UtcNow);
            Check(read()!=null,"valid process arming");
            Check(ProbeRequest.ReadAndValidate(null,host,123,456,host,DateTime.UtcNow)==null,"ordinary launch inert");
            Reject(()=>ProbeRequest.ReadAndValidate(requestPath,host,124,456,host,DateTime.UtcNow),"foreign PID");
            Reject(()=>ProbeRequest.ReadAndValidate(requestPath,host,123,457,host,DateTime.UtcNow),"PID reuse");
            Reject(()=>ProbeRequest.ReadAndValidate(requestPath,host,123,456,host,DateTime.UtcNow.AddMinutes(11)),"stale request");
            string old=request.resultPath;request.resultPath=Path.Combine(directory,"..","escape.json");
            File.WriteAllText(requestPath,ProbeRequest.Json.Serialize(request));Reject(()=>read(),"output path escape");
            request.resultPath=old;request.hostSha256=new string('0',64);File.WriteAllText(requestPath,ProbeRequest.Json.Serialize(request));Reject(()=>read(),"wrong host");
            request.hostSha256=ProbeInterop.Hash(host);File.WriteAllText(requestPath,ProbeRequest.Json.Serialize(request));
            request=read();request.Claim(123,456);Reject(()=>request.Claim(123,456),"single claim");
            request.Report(new {runId=request.runId,status="first"});request.Report(new {runId=request.runId,status="second"});
            Check(File.ReadAllText(request.resultPath).Contains("second"),"atomic report replacement");
            Console.WriteLine("PASS managed private probe host: "+checks+" checks; ABI712/error200; two actual no-engine interop rejections; strict counters; foreign PID/start time/stale/hash/path/duplicate-claim rejection.");return 0;
        }
        catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
