"""Independent empty-stock snapshot gate review; no process-memory acquisition."""
from pathlib import Path
import hashlib
import json
import subprocess
from patch_native_copy import safe_review_output

ROOT = Path(__file__).resolve().parents[2]
BUILD = ROOT/'build/agent-storage'
OUT = ROOT/'evidence/engine-extension/native-empty-snapshot-independent-review.json'
INPUTS = ['src/MNBL.AgentStorage/EmptyNativePoolSnapshot.hpp',
          'src/MNBL.AgentStorage/NativePoolSnapshot.hpp','src/MNBL.AgentStorage/StableAgentStorage.hpp',
          'tests/AgentStorage/empty_native_snapshot_tests.cpp','tools/battle-scale/Test-EmptyNativeSnapshot.cmd',
          'tools/battle-scale/review_empty_native_snapshot.py',
          'build/agent-storage/empty_native_snapshot_tests.exe','build/agent-storage/empty_native_snapshot_asan.exe']
CPP = r'''
#include "../../src/MNBL.AgentStorage/EmptyNativePoolSnapshot.hpp"
#include <iostream>
#include <limits>
using namespace mnbl::storage;
int main(){
 constexpr std::array<std::size_t,5> widths{0xc60,0xc60,0x230,0x2c8,0x2c8},offsets{16,16,28,0,0};
 std::array<std::vector<std::byte>,5> records,links;
 std::array<NativePoolSnapshot,5> views;
 std::size_t checks=0;
 auto check=[&](bool value){++checks;if(!value)throw std::runtime_error("Independent empty snapshot mismatch");};
 for(std::size_t p=0;p<5;++p){
   records[p].assign(2040*widths[p]+2,std::byte{0x96});links[p].assign(4082,std::byte{0xff});
   // Deliberately unaligned spans: memcpy, not typed loads, is required.
   views[p]={std::span<const std::byte>(records[p]).subspan(1,2040*widths[p]),std::span<const std::byte>(links[p]).subspan(1,4080),0,0};
   for(std::size_t i=0;i<2040;++i){const std::int32_t x=-1;std::memcpy(records[p].data()+1+i*widths[p]+offsets[p],&x,4);}
 }
 check(static_cast<bool>(check_empty_stock_native_pools(views)));
 for(std::size_t p=0;p<5;++p){
   auto rejected=[&](EmptySnapshotFailure f,std::uint64_t i=no_slot){auto r=check_empty_stock_native_pools(views);check(!r&&r.failure==f&&r.pool==p&&r.index==i);};
   for(std::size_t i=0;i<2040;++i){
     auto* h=records[p].data()+1+i*widths[p]+offsets[p];
     const std::int32_t occupied=static_cast<std::int32_t>(i),empty=-1;
     std::memcpy(h,&occupied,4);rejected(EmptySnapshotFailure::Header,i);std::memcpy(h,&empty,4);
     auto* l=links[p].data()+1+i*2;const std::int16_t live=2041,free=-1;
     std::memcpy(l,&live,2);rejected(EmptySnapshotFailure::Link,i);std::memcpy(l,&free,2);
   }
   auto original=views[p];
   for(const auto n:{std::size_t{0},std::size_t{2040}*widths[p]+1}){
     views[p].records=std::span<const std::byte>(records[p]).subspan(1,n);rejected(EmptySnapshotFailure::Dimensions);views[p]=original;
   }
   for(const auto n:{std::size_t{0},std::size_t{4081}}){views[p].signed_links=std::span<const std::byte>(links[p]).first(n);rejected(EmptySnapshotFailure::Dimensions);views[p]=original;}
   for(auto v:{std::numeric_limits<std::int32_t>::min(),std::numeric_limits<std::int32_t>::max()}){
     views[p].live_count=v;rejected(EmptySnapshotFailure::Counters);views[p]=original;
     views[p].highwater=v;rejected(EmptySnapshotFailure::Counters);views[p]=original;
   }
   check(static_cast<bool>(check_empty_stock_native_pools(views)));
   check(records[p].front()==std::byte{0x96}&&records[p].back()==std::byte{0x96});
 }
 // The gate intentionally ignores other object fields: no object/resource or
 // publication guarantee follows from successfully checking metadata alone.
 records[0][1+0x888]=std::byte{0x7f};
 check(static_cast<bool>(check_empty_stock_native_pools(views)));
 std::cout<<"PASS independent empty snapshot "<<checks<<" checks;20400 all-index mutations;unaligned spans;strict dimensions;metadata-only scope\n";
}
'''


def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()


def main():
    pins={p:sha(ROOT/p) for p in INPUTS}
    cpp=BUILD/'empty_native_snapshot_independent.cpp';cmd=BUILD/'Test-EmptySnapshotIndependent.cmd'
    cpp.write_text(CPP)
    commands=['@echo off','setlocal',
              'call "C:\\Program Files (x86)\\Microsoft Visual Studio\\2022\\BuildTools\\VC\\Auxiliary\\Build\\vcvars64.bat" >nul',
              'if errorlevel 1 exit /b %errorlevel%']
    for name in INPUTS[-2:]:
        commands += [name.replace('/','\\'),'if errorlevel 1 exit /b %errorlevel%']
    for suffix,flags in [('optimized','/O2'),('asan','/O1 /fsanitize=address /Zi')]:
        stem='build\\agent-storage\\empty_native_snapshot_independent_'+suffix
        commands += ['cl /nologo /std:c++20 /EHsc /W4 /WX /MD '+flags+' build\\agent-storage\\empty_native_snapshot_independent.cpp /Fo:'+stem+'.obj /Fe:'+stem+'.exe /Fd:'+stem+'.pdb',
                     'if errorlevel 1 exit /b %errorlevel%',stem+'.exe','if errorlevel 1 exit /b %errorlevel%']
    cmd.write_text('\n'.join(commands)+'\n')
    result=subprocess.run(['cmd','/c',str(cmd)],cwd=ROOT,capture_output=True,text=True,timeout=90)
    assert result.returncode==0,(result.stdout,result.stderr)
    assert result.stdout.count('137 checks;')==2,result.stdout
    assert result.stdout.count('PASS independent empty snapshot 20452 checks;')==2,result.stdout
    assert pins=={p:sha(ROOT/p) for p in INPUTS}
    generated=[cpp,cmd]+[BUILD/('empty_native_snapshot_independent_'+x+'.exe') for x in ('optimized','asan')]
    report={'schemaVersion':1,'status':'accepted-necessary-empty-stock-metadata-gate','reviewedSources':pins,
            'generatedArtifacts':{str(p.relative_to(ROOT)).replace('\\','/'):sha(p) for p in generated},
            'findings':[],'originalExecutablesReplayed':INPUTS[-2:],'originalChecksPerBuild':137,
            'independentExecution':{'returnCode':result.returncode,'stdout':result.stdout,'stderr':result.stderr,'checksPerBuild':20452},
            'observations':['Exact 2040-record dimensions and 4080-byte links are required in each of five pools.',
                            'Every one of 10200 headers and 10200 links was independently mutated and rejected with correct pool/index diagnostics.',
                            'Negative/positive extreme counters, zero/oversized views and unaligned views behaved as specified.',
                            'Strict -1 comparison rejects all other negative header/link values; memcpy avoids alignment undefined behavior.',
                            'Other payload fields remain deliberately unvalidated. Passing metadata does not establish object inactivity, absence of resources or publication safety.'],
            'limitations':['Caller must supply readable immutable spans, completed constructor state, correct native byte layout, object lifetime and a mutation exclusion barrier through adoption.',
                           'This is stock2040 only; expanded pools and occupied imports are intentionally unsupported.',
                           'The old occupied validator remains unchanged and receives no approval from this review.',
                           'No live Mission snapshot, native thread synchronization, no-escape proof or engine hook was exercised.'],
            'gameLaunched':False,'enginePatchApproved':False}
    safe_review_output(OUT,[ROOT/p for p in INPUTS]+generated).write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({'status':report['status'],'sha256':sha(OUT),'checksPerBuild':20452}))


if __name__=='__main__':main()
