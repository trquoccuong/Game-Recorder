#include "vtable.h"

#include <windows.h>

#include <cstdint>
#include <cstring>

namespace
{
// x64 MSVC RTTI: _RTTICompleteObjectLocator, with image-relative (RVA) pointers.
struct CompleteObjectLocator
{
    uint32_t signature;  // 1 on x64
    uint32_t offset;     // where this vtable's subobject sits in the complete object
    uint32_t cdOffset;
    int32_t typeDescriptor;
    int32_t classDescriptor;
    int32_t self;
};

struct Section
{
    uint8_t *begin, *end;
};

bool FindSection(uint8_t *base, const char *name, Section &out)
{
    auto *nt = reinterpret_cast<IMAGE_NT_HEADERS *>(base + reinterpret_cast<IMAGE_DOS_HEADER *>(base)->e_lfanew);
    IMAGE_SECTION_HEADER *s = IMAGE_FIRST_SECTION(nt);
    for (int i = 0; i < nt->FileHeader.NumberOfSections; i++, s++)
        if (strncmp(reinterpret_cast<const char *>(s->Name), name, IMAGE_SIZEOF_SHORT_NAME) == 0)
        {
            out = {base + s->VirtualAddress, base + s->VirtualAddress + s->Misc.VirtualSize};
            return true;
        }
    return false;
}
}  // namespace

void **FindVtable(const char *module, const char *rttiName)
{
    auto *base = reinterpret_cast<uint8_t *>(GetModuleHandleA(module));
    Section data, rdata;
    if (!base || !FindSection(base, ".data", data) || !FindSection(base, ".rdata", rdata)) return nullptr;

    // TypeDescriptor: {vftable pointer, spare pointer, name}; the name is the class's decorated name.
    size_t nameLength = strlen(rttiName) + 1;
    uint8_t *typeDescriptor = nullptr;
    for (uint8_t *p = data.begin + 16; p + nameLength <= data.end; p++)
        if (memcmp(p, rttiName, nameLength) == 0)
        {
            typeDescriptor = p - 16;
            break;
        }
    if (!typeDescriptor) return nullptr;

    auto rva = static_cast<int32_t>(typeDescriptor - base);
    for (uint8_t *p = rdata.begin; p + sizeof(CompleteObjectLocator) <= rdata.end; p += 4)
    {
        auto *col = reinterpret_cast<CompleteObjectLocator *>(p);
        if (col->signature != 1 || col->offset != 0 || col->typeDescriptor != rva || col->self != p - base) continue;
        // The vtable is preceded by a pointer to its locator.
        for (uint8_t *q = rdata.begin; q + 2 * sizeof(void *) <= rdata.end; q += sizeof(void *))
            if (*reinterpret_cast<uint8_t **>(q) == p) return reinterpret_cast<void **>(q + sizeof(void *));
    }
    return nullptr;
}

void *PatchVtable(void **vtable, int index, void *replacement)
{
    DWORD protection;
    VirtualProtect(&vtable[index], sizeof(void *), PAGE_READWRITE, &protection);
    void *previous = InterlockedExchangePointer(&vtable[index], replacement);
    VirtualProtect(&vtable[index], sizeof(void *), protection, &protection);
    return previous;
}

long long UtcNowUs()
{
    FILETIME ft;
    GetSystemTimePreciseAsFileTime(&ft);
    long long ticks = (static_cast<long long>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
    return (ticks - 116444736000000000LL) / 10;  // 100 ns ticks since 1601 -> µs since 1970
}
