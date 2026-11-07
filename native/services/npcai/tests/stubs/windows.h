// SPDX-License-Identifier: MIT
#pragma once
#include <atomic>
#include <cstdint>
inline std::atomic<uint64_t> test_now{1000};
inline uint64_t GetTickCount64() { return test_now.load(); }
