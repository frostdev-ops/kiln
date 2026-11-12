// SPDX-License-Identifier: MIT
// Host-only shim: the store test never hashes a blob. The provider does not open, so content_hash_of() gives a zero digest.
#pragma once
#include "windows.h"

using BCRYPT_ALG_HANDLE = void*;
using NTSTATUS = long;
#define BCRYPT_SHA256_ALGORITHM L"SHA256"
inline NTSTATUS BCryptOpenAlgorithmProvider(BCRYPT_ALG_HANDLE*, const wchar_t*, const wchar_t*, ULONG) { return -1; }
inline NTSTATUS BCryptHash(BCRYPT_ALG_HANDLE, PUCHAR, ULONG, PUCHAR, ULONG, PUCHAR, ULONG) { return -1; }
inline NTSTATUS BCryptCloseAlgorithmProvider(BCRYPT_ALG_HANDLE, ULONG) { return 0; }
