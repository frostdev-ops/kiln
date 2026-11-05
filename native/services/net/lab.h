// SPDX-License-Identifier: MIT
// kiln.net lab transport: Kiln frames as UDP datagrams on 127.0.0.1 (tests with tools/nettest; never public).
#pragma once
#include <cstdint>
#include <string>

namespace knet::lab {

bool open(uint16_t port, std::string* error);  // binds 127.0.0.1:port, non-blocking
bool is_open();
void close();

// Receives one datagram; returns its length (0 = none waiting, -1 = error) and the sender's key (ip:port as u64).
int recv(uint8_t* buffer, int capacity, uint64_t* from);
bool send(uint64_t to, const uint8_t* data, uint32_t len);
std::string describe(uint64_t key);

}  // namespace knet::lab
