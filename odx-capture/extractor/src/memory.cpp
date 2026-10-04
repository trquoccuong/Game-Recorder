#include "odx/memory.hpp"

#include <algorithm>
#include <cctype>
#include <cstring>
#include <sstream>

namespace odx {

std::optional<Pattern> parse_pattern(std::string_view text) {
    Pattern p;
    std::istringstream in{std::string(text)};
    std::string tok;
    while (in >> tok) {
        if (tok == "?" || tok == "??") {
            p.push_back(std::nullopt);
            continue;
        }
        if (tok.size() != 2 || !std::isxdigit(static_cast<unsigned char>(tok[0])) ||
            !std::isxdigit(static_cast<unsigned char>(tok[1])))
            return std::nullopt;
        p.push_back(static_cast<uint8_t>(std::stoul(tok, nullptr, 16)));
    }
    if (p.empty()) return std::nullopt;
    return p;
}

std::optional<uint64_t> scan(MemoryReader& mem, const ModuleSpan& span, const Pattern& pattern) {
    constexpr size_t kChunk = 1 << 20;
    const size_t overlap = pattern.size() - 1;
    std::vector<uint8_t> buf;
    for (size_t offset = 0; offset < span.size; offset += kChunk) {
        size_t n = std::min(kChunk + overlap, span.size - offset);
        buf.resize(n);
        if (!mem.read(span.base + offset, buf.data(), n)) continue;  // unreadable page range: skip
        for (size_t i = 0; i + pattern.size() <= n; ++i) {
            bool hit = true;
            for (size_t j = 0; j < pattern.size() && hit; ++j) hit = !pattern[j] || *pattern[j] == buf[i + j];
            if (hit) return span.base + offset + i;
        }
    }
    return std::nullopt;
}

std::optional<uint64_t> resolve_rip(MemoryReader& mem, uint64_t instr, int disp_offset, int instr_len) {
    int32_t disp = 0;
    if (!mem.read(instr + disp_offset, &disp, sizeof(disp))) return std::nullopt;
    return instr + instr_len + static_cast<int64_t>(disp);
}

void BufferMemory::add_module(const std::string& name, uint64_t base, std::vector<uint8_t> bytes) {
    regions_[name] = Region{base, std::move(bytes)};
}

std::vector<uint8_t>& BufferMemory::bytes(const std::string& name) { return regions_.at(name).bytes; }

bool BufferMemory::read(uint64_t addr, void* out, size_t n) {
    for (const auto& [_, r] : regions_) {
        if (addr >= r.base && addr + n <= r.base + r.bytes.size()) {
            std::memcpy(out, r.bytes.data() + (addr - r.base), n);
            return true;
        }
    }
    return false;
}

std::optional<ModuleSpan> BufferMemory::module(std::string_view name) {
    auto it = regions_.find(name);
    if (it == regions_.end()) return std::nullopt;
    return ModuleSpan{it->second.base, it->second.bytes.size()};
}

}  // namespace odx
