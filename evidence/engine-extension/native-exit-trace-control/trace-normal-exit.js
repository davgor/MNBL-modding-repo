// Observation only: no argument, return-value, register or memory replacement.
'use strict';
function frame(address) {
  const module = Process.findModuleByAddress(address);
  return {address: address.toString(), module: module ? module.name : null,
          rva: module ? address.sub(module.base).toString() : null};
}
function record(label, context, extra) {
  let stack = [];
  try { stack = Thread.backtrace(context, Backtracer.ACCURATE).slice(0, 24).map(frame); }
  catch (error) { stack = [{error: String(error)}]; }
  send({event: label, tid: Process.getCurrentThreadId(), stack, ...extra});
}
for (const [moduleName, name, codeIndex] of [
  ['kernel32.dll', 'ExitProcess', 0],
  ['kernelbase.dll', 'TerminateProcess', 1],
  ['ntdll.dll', 'RtlExitUserProcess', 0],
  ['ntdll.dll', 'NtTerminateProcess', 1]
]) {
  const address = Process.getModuleByName(moduleName).getExportByName(name);
  Interceptor.attach(address, {onEnter(args) {
    record(name, this.context, {code: args[codeIndex].toInt32(), handle: codeIndex ? args[0].toString() : null});
  }});
  send({event: 'hook-installed', name, location: frame(address)});
}
const native = Process.getModuleByName('TaleWorlds.Native.dll');
Interceptor.attach(native.base.add(0x472b0), {
  onEnter() { record('native-cleanup-enter', this.context, {returnCode: native.base.add(0xd92cd4).readS32()}); },
  onLeave(result) { record('native-cleanup-return', this.context, {code: result.toInt32(), returnCode: native.base.add(0xd92cd4).readS32()}); }
});
Interceptor.attach(native.base.add(0x47740), {
  onLeave(result) { record('native-main-return', this.context, {code: result.toInt32()}); }
});
send({event: 'ready', pid: Process.id, native: native.base.toString()});
