// odx_inject.exe <process name | pid> <path to client_extractor.dll>
// Loads the extractor DLL into a running game via LoadLibraryW in a remote thread. Prints the game's pid.
// Use only on private/offline setups where anti-cheat is absent or officially disabled.
#include <windows.h>
#include <tlhelp32.h>

#include <cstdio>
#include <cwchar>
#include <cwctype>
#include <filesystem>
#include <string>

namespace {

DWORD find_pid(const std::wstring& name_or_pid) {
    if (!name_or_pid.empty() && std::iswdigit(name_or_pid[0])) return static_cast<DWORD>(_wtoi(name_or_pid.c_str()));
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    PROCESSENTRY32W pe{sizeof(pe)};
    DWORD pid = 0;
    for (BOOL ok = Process32FirstW(snap, &pe); ok && !pid; ok = Process32NextW(snap, &pe))
        if (_wcsicmp(pe.szExeFile, name_or_pid.c_str()) == 0) pid = pe.th32ProcessID;
    CloseHandle(snap);
    return pid;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc < 3) {
        std::fwprintf(stderr, L"usage: odx_inject <process.exe | pid> <client_extractor.dll>\n");
        return 2;
    }
    const DWORD pid = find_pid(argv[1]);
    if (!pid) {
        std::fwprintf(stderr, L"process not found: %ls\n", argv[1]);
        return 1;
    }
    const std::wstring dll = std::filesystem::absolute(argv[2]).wstring();
    if (!std::filesystem::exists(dll)) {
        std::fwprintf(stderr, L"dll not found: %ls\n", dll.c_str());
        return 1;
    }

    HANDLE proc = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION |
                                  PROCESS_VM_WRITE | PROCESS_VM_READ,
                              FALSE, pid);
    if (!proc) {
        std::fwprintf(stderr, L"OpenProcess failed: %lu (run as the same user / elevated?)\n", GetLastError());
        return 1;
    }
    const SIZE_T bytes = (dll.size() + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(proc, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote || !WriteProcessMemory(proc, remote, dll.c_str(), bytes, nullptr)) {
        std::fwprintf(stderr, L"writing dll path failed: %lu\n", GetLastError());
        return 1;
    }
    auto load_library = reinterpret_cast<LPTHREAD_START_ROUTINE>(
        GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));
    HANDLE thread = CreateRemoteThread(proc, nullptr, 0, load_library, remote, 0, nullptr);
    if (!thread) {
        std::fwprintf(stderr, L"CreateRemoteThread failed: %lu\n", GetLastError());
        return 1;
    }
    WaitForSingleObject(thread, INFINITE);
    DWORD module = 0;
    GetExitCodeThread(thread, &module);
    VirtualFreeEx(proc, remote, 0, MEM_RELEASE);
    CloseHandle(thread);
    CloseHandle(proc);
    if (!module) {
        std::fwprintf(stderr, L"LoadLibraryW returned NULL in the target (architecture mismatch or missing deps?)\n");
        return 1;
    }
    std::wprintf(L"injected into pid %lu\n%lu\n", pid, pid);
    return 0;
}
