"""Trace the pinned quit callback and exit return using an offline native backup."""
from pathlib import Path
import hashlib
import json
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT / 'build/battle-scale/python-libs'), str(Path(__file__).parent)]
import capstone
import pefile
from patch_native_copy import safe_review_output


def build():
    # No memory map of the installed Native DLL: the runner owns replacement.
    source = ROOT / 'build/battle-scale/native-run-20261017-065749-08e99ed9/backup-0'
    executable = Path('D:/SteamLibrary/steamapps/common/Mount & Blade II Bannerlord/bin/Win64_Shipping_Client/Bannerlord.Native.exe')
    managed = ROOT / 'evidence/engine-extension/native-quit-managed-contract.json'
    native_bytes = source.read_bytes()
    if hashlib.sha256(native_bytes).hexdigest() != '2a5e0e0b15513ebb7052d747a621b82c50ee834553eba283b33bbbc33766e12a':
        raise ValueError('Offline native backup identity mismatch')
    pe = pefile.PE(data=native_bytes)
    exe_bytes = executable.read_bytes()
    exe = pefile.PE(data=exe_bytes)
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)

    def rows(image, start, end):
        result = list(decoder.disasm(image.get_data(start, end-start), image.OPTIONAL_HEADER.ImageBase+start))
        if sum(i.size for i in result) != end-start:
            raise ValueError('Incomplete selected instruction span')
        return [dict(address=hex(i.address), bytes=i.bytes.hex(), mnemonic=i.mnemonic, operands=i.op_str) for i in result]

    native_spans = {
        'quitBinding': (0x39823b, 0x39824f),
        'delayedExitBinding': (0x397b6f, 0x397b83),
        'returnCodeBinding': (0x39800b, 0x39801f),
        'registrationDispatcher': (0x3986e0, 0x3986ee),
        'quitCallback': (0x528020, 0x52802f),
        'returnCodeGetter': (0x529b50, 0x529b57),
        'delayedExitSetterPrefix': (0x5298a0, 0x5298ad),
        'loopEntryCheck': (0x4714f, 0x47163),
        'loopBackedgeAndReturn': (0x47275, 0x472a1),
        'cleanupReturn': (0x47700, 0x4771e),
        'nativeMain': (0x47790, 0x477d1),
        'exportThunk': (0x45780, 0x45785),
    }
    raw = {name: rows(pe, *span) for name, span in native_spans.items()}
    raw.update({
        'exeWinMain': rows(exe, 0x1000, 0x100d),
        'crtCallAndPreservedReturn': rows(exe, 0x1229, 0x1250),
        'crtExitBranches': rows(exe, 0x1289, 0x1299),
        'crtExitThunk': rows(exe, 0x1c7c, 0x1c88),
    })
    if [x['mnemonic'] for x in raw['quitCallback']] != ['mov', 'mov', 'ret']:
        raise ValueError('Quit callback changed')
    if raw['quitCallback'][1]['bytes'] != 'c6805502000000':
        raise ValueError('Quit callback no longer clears run flag')
    imports = {hex(i.address): i.name.decode() for dll in exe.DIRECTORY_ENTRY_IMPORT for i in dll.imports if i.name}
    if imports['0x140002088'] != 'WotsMainNativeSDLL' or imports['0x140002108'] != 'exit':
        raise ValueError('Executable return/exit import changed')
    section = pe.get_section_by_rva(0xd92cd4)
    if not section.SizeOfRawData <= 0xd92cd4-section.VirtualAddress < section.Misc_VirtualSize:
        raise ValueError('Return code storage is not zero-filled image tail')
    sources = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in (source, executable, managed, Path(__file__))}
    return {
        'schemaVersion': 1,
        'status': 'quit-clears-loop-flag-return-code-is-separate-minus-one-not-proven-normal',
        'sources': sources, 'gameExecuted': False, 'installedNativeMapped': False,
        'managedBindings': json.loads(managed.read_text(encoding='utf-8-sig')),
        'nativeBindings': {'QuitGame': {'index': 1483, 'target': '0x180528020'},
                           'GetReturnCode': {'index': 1455, 'target': '0x180529b50'},
                           'DoDelayedexit': {'index': 1396, 'target': '0x1805298a0'}},
        'rawProofs': raw,
        'findings': [
            'Utilities.QuitGame dispatches to528020: load application fromD96CF0, clear application+255, return. It does not call ExitProcess, exit, TerminateProcess, or set return code.',
            'Native main47790 calls initialization46ae0, loop470a0, cleanup472b0; loop exits after application+255 becomes zero.',
            'Cleanup47700 loads DWORD D92CD4 into EBX, preserves it over the final release call, and returns it in EAX at4770f. Native main preserves that result through epilogue.',
            'Executable WinMain1000 tail-dispatches imported WotsMainNativeSDLL. CRT1229 saves its result in EBX; normal native branch1289 passes it to CRT exit at1c7c.',
            'Public Utilities.GetReturnCode dispatches to529b50 and reads the same D92CD4 global. DoDelayedexit(int) is a separate callback whose5298a7 store explicitly changes it.',
            'D92CD4 is initially zero-filled image storage. This does not prove it remains zero after initialization or callbacks.',
        ],
        'acceptanceImplication': 'Do not whitelist -1 as normal merely because QuitGame was invoked. Preserve strict successful probe receipt and normal-exit requirement; record Utilities.GetReturnCode before quit without clearing or resetting it.',
        'unknowns': ['The runtime source of the observed -1 is not established by this static trace.',
                     'Other error/exit-code writers and runtime failures can change the return code; these spans do not certify a particular run.'],
    }


if __name__ == '__main__':
    result = build()
    output = ROOT / 'evidence/engine-extension/native-quit-exit-contract.json'
    safe_review_output(output, list(result['sources']))
    output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(result['status'])
