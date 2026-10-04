#pragma once

#include "odx/memory.hpp"

namespace odx::win {

class ProcessMemory : public MemoryReader {
public:
    bool read(uint64_t addr, void* out, size_t n) override;
    // Module by file name (e.g. "Game-Win64-Shipping.exe"); empty name = main executable.
    std::optional<ModuleSpan> module(std::string_view name) override;
};

}  // namespace odx::win
