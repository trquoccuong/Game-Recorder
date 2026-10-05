#pragma once

// Kept apart from the SDK headers: vtable.cpp needs <windows.h>, which clashes with them.

// The primary vtable (the one at object offset 0) of an MSVC-compiled class in a loaded module, found through its RTTI;
// nullptr if the module or the class is missing.
void **FindVtable(const char *module, const char *rttiName);

// Replaces vtable[index] with replacement; returns the previous entry.
void *PatchVtable(void **vtable, int index, void *replacement);

// Current UTC time in microseconds since the Unix epoch.
long long UtcNowUs();
