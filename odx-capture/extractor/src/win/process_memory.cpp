#include "process_memory.hpp"

#include <windows.h>
#include <psapi.h>

#include <string>

namespace odx::win {

bool ProcessMemory::read(uint64_t addr, void* out, size_t n) {
    // ReadProcessMemory on our own process fails cleanly on unmapped/guarded pages instead of faulting.
    SIZE_T got = 0;
    return ReadProcessMemory(GetCurrentProcess(), reinterpret_cast<LPCVOID>(addr), out, n, &got) && got == n;
}

std::optional<ModuleSpan> ProcessMemory::module(std::string_view name) {
    HMODULE h = name.empty() ? GetModuleHandleW(nullptr) : GetModuleHandleA(std::string(name).c_str());
    if (!h) return std::nullopt;
    MODULEINFO mi{};
    if (!GetModuleInformation(GetCurrentProcess(), h, &mi, sizeof(mi))) return std::nullopt;
    return ModuleSpan{reinterpret_cast<uint64_t>(mi.lpBaseOfDll), mi.SizeOfImage};
}

}  // namespace odx::win
