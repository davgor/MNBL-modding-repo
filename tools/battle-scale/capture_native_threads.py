"""Bounded diagnostic of the one owned probe process; never write target memory.

Capture each thread's context and at most64KiB of stack while briefly suspending
that thread only. Resume in finally BEFORE StackWalk64/symbol work. Walk against
the captured stack with live read-only module metadata; JIT frames may truncate.
"""
import argparse,ctypes as C,json,os,struct,time
from ctypes import wintypes as W
from pathlib import Path

PID=11120
EXPECTED=r'D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client\Bannerlord.exe'
OUT=Path(__file__).resolve().parents[2]/'build/battle-scale/native-run-20261017-070802-dd8b5783'
K=C.WinDLL('kernel32',use_last_error=True)
D=C.WinDLL(r'C:\Windows\System32\dbghelp.dll',use_last_error=True)
U64=C.c_ulonglong
class THREADENTRY(C.Structure):
    _fields_=[('size',W.DWORD),('usage',W.DWORD),('tid',W.DWORD),('pid',W.DWORD),('priority',W.LONG),('delta',W.LONG),('flags',W.DWORD)]
class MODULEENTRY(C.Structure):
    _fields_=[('size',W.DWORD),('id',W.DWORD),('pid',W.DWORD),('globalUse',W.DWORD),('processUse',W.DWORD),('base',C.c_void_p),('bytes',W.DWORD),('module',W.HMODULE),('name',W.WCHAR*256),('path',W.WCHAR*260)]
class ADDRESS(C.Structure):
    _fields_=[('Offset',U64),('Segment',W.WORD),('Mode',C.c_int)]
class KDHELP(C.Structure):
    _fields_=[('Thread',U64),('ThCallbackStack',W.DWORD),('ThCallbackBStore',W.DWORD),('NextCallback',W.DWORD),('FramePointer',W.DWORD),
              ('KiCallUserMode',U64),('KeUserCallbackDispatcher',U64),('SystemRangeStart',U64),('KiUserExceptionDispatcher',U64),('StackBase',U64),('StackLimit',U64),
              ('BuildVersion',W.DWORD),('RetpolineStubFunctionTableSize',W.DWORD),('RetpolineStubFunctionTable',U64),('RetpolineStubOffset',W.DWORD),('RetpolineStubSize',W.DWORD),('Reserved0',U64*2)]
class STACKFRAME(C.Structure):
    _fields_=[('AddrPC',ADDRESS),('AddrReturn',ADDRESS),('AddrFrame',ADDRESS),('AddrStack',ADDRESS),('AddrBStore',ADDRESS),('FuncTableEntry',C.c_void_p),('Params',U64*4),('Far',W.BOOL),('Virtual',W.BOOL),('Reserved',U64*3),('KdHelp',KDHELP)]

def api(lib,name,result,args):
    f=getattr(lib,name);f.restype=result;f.argtypes=args;return f
OpenProcess=api(K,'OpenProcess',W.HANDLE,[W.DWORD,W.BOOL,W.DWORD])
OpenThread=api(K,'OpenThread',W.HANDLE,[W.DWORD,W.BOOL,W.DWORD])
Close=api(K,'CloseHandle',W.BOOL,[W.HANDLE])
Snapshot=api(K,'CreateToolhelp32Snapshot',W.HANDLE,[W.DWORD,W.DWORD])
ThreadFirst=api(K,'Thread32First',W.BOOL,[W.HANDLE,C.POINTER(THREADENTRY)])
ThreadNext=api(K,'Thread32Next',W.BOOL,[W.HANDLE,C.POINTER(THREADENTRY)])
ModuleFirst=api(K,'Module32FirstW',W.BOOL,[W.HANDLE,C.POINTER(MODULEENTRY)])
ModuleNext=api(K,'Module32NextW',W.BOOL,[W.HANDLE,C.POINTER(MODULEENTRY)])
QueryPath=api(K,'QueryFullProcessImageNameW',W.BOOL,[W.HANDLE,W.DWORD,W.LPWSTR,C.POINTER(W.DWORD)])
ProcessTimes=api(K,'GetProcessTimes',W.BOOL,[W.HANDLE,C.POINTER(U64),C.POINTER(U64),C.POINTER(U64),C.POINTER(U64)])
ThreadTimes=api(K,'GetThreadTimes',W.BOOL,[W.HANDLE,C.POINTER(U64),C.POINTER(U64),C.POINTER(U64),C.POINTER(U64)])
ThreadOwner=api(K,'GetProcessIdOfThread',W.DWORD,[W.HANDLE])
ExitCode=api(K,'GetExitCodeProcess',W.BOOL,[W.HANDLE,C.POINTER(W.DWORD)])
Suspend=api(K,'SuspendThread',W.DWORD,[W.HANDLE])
Resume=api(K,'ResumeThread',W.DWORD,[W.HANDLE])
GetContext=api(K,'GetThreadContext',W.BOOL,[W.HANDLE,C.c_void_p])
Read=api(K,'ReadProcessMemory',W.BOOL,[W.HANDLE,C.c_void_p,C.c_void_p,C.c_size_t,C.POINTER(C.c_size_t)])
SymInit=api(D,'SymInitializeW',W.BOOL,[W.HANDLE,W.LPCWSTR,W.BOOL])
SymCleanup=api(D,'SymCleanup',W.BOOL,[W.HANDLE])
SymOptions=api(D,'SymSetOptions',W.DWORD,[W.DWORD])
READ_CALLBACK=C.WINFUNCTYPE(W.BOOL,W.HANDLE,U64,C.c_void_p,W.DWORD,C.POINTER(W.DWORD))
Walk=api(D,'StackWalk64',W.BOOL,[W.DWORD,W.HANDLE,W.HANDLE,C.POINTER(STACKFRAME),C.c_void_p,READ_CALLBACK,C.c_void_p,C.c_void_p,C.c_void_p])

def error(label):return OSError(C.get_last_error(),label)
def identity(h):
    path=C.create_unicode_buffer(32768);length=W.DWORD(len(path));status=W.DWORD()
    if not QueryPath(h,0,path,C.byref(length)):raise error('Query process path')
    ts=[U64() for _ in range(4)]
    if not ProcessTimes(h,*[C.byref(v) for v in ts]):raise error('Get process creation time')
    if not ExitCode(h,C.byref(status)) or status.value!=259:raise RuntimeError('Owned process is no longer active')
    if os.path.normcase(path.value)!=os.path.normcase(EXPECTED):raise RuntimeError('PID path differs from owned probe')
    return dict(pid=PID,path=path.value,creationFileTime=ts[0].value)
def inventory():
    threads=[];modules=[]
    for flag,entry,first,next_,dest in ((4,THREADENTRY(),ThreadFirst,ThreadNext,threads),(0x18,MODULEENTRY(),ModuleFirst,ModuleNext,modules)):
        handle=Snapshot(flag,PID)
        if handle==C.c_void_p(-1).value:raise error('Toolhelp snapshot')
        try:
            entry.size=C.sizeof(entry)
            ok=first(handle,C.byref(entry))
            while ok:
                if flag==4:
                    if entry.pid==PID:dest.append(entry.tid)
                else:dest.append(dict(base=entry.base,size=entry.bytes,name=entry.name,path=entry.path))
                ok=next_(handle,C.byref(entry))
        finally:Close(handle)
    return threads,modules
def aligned_context():
    storage=C.create_string_buffer(1232+16);pointer=(C.addressof(storage)+15)&~15
    C.c_uint32.from_address(pointer+48).value=0x10000b
    return storage,pointer
def capture_memory(expected):
    """Read only bounded object regions identified in the reviewed register snapshot."""
    source=OUT/'native-threads-134366944844276357-24972.json'
    prior=json.loads(source.read_text())
    if prior['identity']['creationFileTime']!=expected:raise RuntimeError('Snapshot identity differs')
    busy=[t for t in prior['threads'] if t.get('rip',{}).get('module')=='TaleWorlds.Native.dll' and t['rip']['rva'] in ('0x88744','0x88747','0x88749')]
    regions={}
    for t in busy:
        for reg,size in (('rbp',0x140),('rcx',0x300)):
            addr=int(t['registers'][reg],16)
            regions.setdefault((addr,size),[]).append({'tid':t['tid'],'register':reg})
    if busy:
        addr=int(busy[0]['registers']['rdi'],16);regions[(addr,0x100)]=[{'tid':busy[0]['tid'],'register':'rdi'}]
    if len(regions)>31 or sum(s for _,s in regions)>17000:raise RuntimeError('Unexpected region budget')
    process=OpenProcess(0x410,False,PID)
    if not process:raise error('Open owned process')
    try:
        observed=identity(process)
        if observed['creationFileTime']!=expected:raise RuntimeError('Owned process identity changed')
        report={'identity':observed,'capturedUtc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),
                'sourceThreadSnapshot':str(source),'atomic':False,'threadsSuspended':False,'regions':[]}
        for (addr,size),origins in regions.items():
            buf=C.create_string_buffer(size);got=C.c_size_t()
            success=bool(Read(process,addr,buf,size,C.byref(got)));data=bytes(buf.raw[:got.value])
            report['regions'].append({'address':hex(addr),'requestedBytes':size,'readBytes':got.value,'success':success,
                'origins':origins,'hex':data.hex(),
                'qwords':{hex(i):hex(struct.unpack_from('<Q',data,i)[0]) for i in range(0,len(data)-7,8)},
                'dwords':{hex(i):hex(struct.unpack_from('<I',data,i)[0]) for i in range(0,len(data)-3,4)}})
        dest=OUT/('native-object-memory-'+str(os.getpid())+'.json');dest.write_text(json.dumps(report,indent=2)+'\n')
        print(json.dumps({'output':str(dest),'regions':len(regions),'bytesRead':sum(r['readBytes'] for r in report['regions']),'allRead':all(r['success'] for r in report['regions'])}))
    finally:Close(process)
def capture(expected):
    if C.sizeof(C.c_void_p)!=8 or C.sizeof(ADDRESS)!=16 or C.sizeof(KDHELP)!=112 or C.sizeof(STACKFRAME)!=264:
        raise RuntimeError('Unsupported Windowsx64 structure layout')
    process=OpenProcess(0x410,False,PID)
    if not process:raise error('Open owned process read/query only')
    initialized=False
    report={'pid':PID,'capturedUtc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),'threads':[],
            'method':'Per-thread context+cached stack; each successful suspension resumed before StackWalk64. No target-memory writes or whole-process suspension.',
            'limitations':['Sequential thread snapshots are not simultaneous.','Unwinding uses captured stack plus live module metadata; CLR/JIT frames can truncate.']}
    try:
        report['identity']=identity(process)
        if report['identity']['creationFileTime']!=expected:raise RuntimeError('Process creation identity differs')
        tids,modules=inventory();report['modules']=modules
        def location(pc):
            m=next((m for m in modules if m['base']<=pc<m['base']+m['size']),None)
            return dict(address=hex(pc),module=m['name'] if m else None,rva=hex(pc-m['base']) if m else None)
        # Symbol initialization and file loading occur with all target threads running.
        SymOptions(0x4|0x200|0x80000) # deferred, fail-critical-errors, no-prompts
        initialized=bool(SymInit(process,str(OUT),True))
        report['dbghelpInitialized']=initialized
        for tid in tids[:192]:
            identity_now=identity(process)
            if identity_now['creationFileTime']!=expected:raise RuntimeError('Process identity changed before thread capture')
            thread=OpenThread(0x4a,False,tid)
            row={'tid':tid,'suspendSucceeded':False,'resumed':False};report['threads'].append(row)
            if not thread:row['error']=str(error('OpenThread'));continue
            suspended=False
            try:
                if ThreadOwner(thread)!=PID:raise RuntimeError('Thread no longer belongs to owned process')
                times=[U64() for _ in range(4)]
                if ThreadTimes(thread,*[C.byref(v) for v in times]):row.update(creationFileTime=times[0].value,kernel100ns=times[2].value,user100ns=times[3].value)
                storage,ctx=aligned_context();chunks=[];started=time.perf_counter()
                try:
                    previous=Suspend(thread)
                    if previous==0xffffffff:raise error('SuspendThread')
                    suspended=True;row['suspendSucceeded']=True;row['previousSuspendCount']=previous
                    if not GetContext(thread,ctx):raise error('GetThreadContext')
                    raw=C.string_at(ctx,1232);rsp=struct.unpack_from('<Q',raw,152)[0];rbp=struct.unpack_from('<Q',raw,160)[0];rip=struct.unpack_from('<Q',raw,248)[0]
                    row['rip']=location(rip);row['rsp']=hex(rsp);row['rbp']=hex(rbp)
                    row['registers']={name:hex(struct.unpack_from('<Q',raw,120+8*n)[0]) for n,name in enumerate(('rax','rcx','rdx','rbx','rsp','rbp','rsi','rdi','r8','r9','r10','r11','r12','r13','r14','r15','rip'))}
                    # Small page reads stop at stack guard/uncommitted boundary.
                    address=rsp
                    while address<rsp+65536 and time.perf_counter()-started<0.05:
                        count=min(4096-(address&4095),rsp+65536-address);buf=C.create_string_buffer(count);got=C.c_size_t()
                        if not Read(process,address,buf,count,C.byref(got)) or not got.value:break
                        chunks.append((address,bytes(buf.raw[:got.value])));address+=got.value
                finally:
                    if suspended:
                        result=Resume(thread);row['resumeReturnCount']=result;row['resumed']=result!=0xffffffff
                        row['suspendMilliseconds']=(time.perf_counter()-started)*1000
                        if result==0xffffffff:raise error('CRITICAL ResumeThread failed')
                        suspended=False
                row['cachedStackBytes']=sum(len(b) for _,b in chunks)
                row['frames']=[location(rip)]
                @READ_CALLBACK
                def reader(handle,address,dest,count,got):
                    for start,data in chunks:
                        if start<=address and address+count<=start+len(data):
                            C.memmove(dest,data[address-start:address-start+count],count);got[0]=count;return True
                    # Refuse uncached reads in the captured stack interval.
                    if rsp<=address<rsp+65536:got[0]=0;return False
                    actual=C.c_size_t();ok=Read(process,address,dest,count,C.byref(actual));got[0]=actual.value;return ok
                frame=STACKFRAME();frame.AddrPC.Offset=rip;frame.AddrPC.Mode=3;frame.AddrStack.Offset=rsp;frame.AddrStack.Mode=3;frame.AddrFrame.Offset=rbp;frame.AddrFrame.Mode=3
                if initialized:
                    seen=set()
                    for _ in range(40):
                        if not Walk(0x8664,process,thread,C.byref(frame),ctx,reader,C.cast(D.SymFunctionTableAccess64,C.c_void_p),C.cast(D.SymGetModuleBase64,C.c_void_p),None):break
                        pc=frame.AddrPC.Offset;key=(pc,frame.AddrStack.Offset)
                        if not pc or key in seen:break
                        seen.add(key)
                        if pc!=rip:row['frames'].append(dict(location(pc),stack=hex(frame.AddrStack.Offset)))
            except Exception as exc:
                row['error']=str(exc)
            finally:
                if suspended:
                    result=Resume(thread);row['resumeRetryReturnCount']=result;row['resumed']=result!=0xffffffff
                Close(thread)
            # Persist only after thread has been resumed and handle released.
            OUT.mkdir(parents=True,exist_ok=True)
            destination=OUT/('native-threads-'+str(report['identity']['creationFileTime'])+'-'+str(os.getpid())+'.json')
            destination.write_text(json.dumps(report,indent=2)+'\n')
        report['allSuccessfulSuspensionsResumed']=all(not x['suspendSucceeded'] or x['resumed'] for x in report['threads'])
        report['threadCount']=len(report['threads']);report['listedThreadCount']=len(tids)
        destination.write_text(json.dumps(report,indent=2)+'\n')
        print(json.dumps({'output':str(destination),'threads':len(report['threads']),'allSuccessfulSuspensionsResumed':report['allSuccessfulSuspensionsResumed'],
                          'ripCounts':{name:sum(t.get('rip',{}).get('module')==name for t in report['threads']) for name in sorted({t.get('rip',{}).get('module') or 'unknown' for t in report['threads']})}}))
    finally:
        if initialized:SymCleanup(process)
        Close(process)

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--capture',action='store_true');parser.add_argument('--memory',action='store_true');parser.add_argument('--expected-creation',type=int)
    args=parser.parse_args()
    if args.memory:
        if args.expected_creation is None:parser.error('Memory capture requires expected creation identity')
        capture_memory(args.expected_creation)
    elif args.capture:
        if args.expected_creation is None:parser.error('Capture requires expected creation identity from prior inspection')
        capture(args.expected_creation)
    else:
        h=OpenProcess(0x410,False,PID)
        if not h:raise error('Open owned process')
        try:print(json.dumps(identity(h)))
        finally:Close(h)
