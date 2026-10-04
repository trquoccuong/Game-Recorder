// Memory access used by Lua profiles: reads, module lookup, AOB signature scan, RIP-relative resolution.
#pragma once

#include <cstddef>
#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace odx {

struct ModuleSpan {
    uint64_t base = 0;
    size_t size = 0;
};

class MemoryReader {
public:
    virtual ~MemoryReader() = default;
    // Returns false (never crashes) when the range is not readable.
    virtual bool read(uint64_t addr, void* out, size_t n) = 0;
    virtual std::optional<ModuleSpan> module(std::string_view name) = 0;
};

// Parsed AOB pattern, e.g. "48 8B 05 ?? ?? ?? ??". nullopt bytes are wildcards.
using Pattern = std::vector<std::optional<uint8_t>>;
std::optional<Pattern> parse_pattern(std::string_view text);

// First match of `pattern` inside [span.base, span.base + span.size), read in chunks.
std::optional<uint64_t> scan(MemoryReader& mem, const ModuleSpan& span, const Pattern& pattern);

// Resolves `instr + instr_len + disp32`, where disp32 sits at `instr + disp_offset` (x64 RIP-relative operand).
std::optional<uint64_t> resolve_rip(MemoryReader& mem, uint64_t instr, int disp_offset, int instr_len);

// In-memory fake process for tests and tools: named byte regions at fixed base addresses.
class BufferMemory : public MemoryReader {
public:
    void add_module(const std::string& name, uint64_t base, std::vector<uint8_t> bytes);
    std::vector<uint8_t>& bytes(const std::string& name);
    bool read(uint64_t addr, void* out, size_t n) override;
    std::optional<ModuleSpan> module(std::string_view name) override;

private:
    struct Region {
        uint64_t base;
        std::vector<uint8_t> bytes;
    };
    std::map<std::string, Region, std::less<>> regions_;
};

}  // namespace odx
