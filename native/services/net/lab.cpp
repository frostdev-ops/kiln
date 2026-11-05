// SPDX-License-Identifier: MIT
#include "lab.h"

#include <winsock2.h>
#include <ws2tcpip.h>

#include <cstdio>

namespace knet::lab {
namespace {
SOCKET g_sock = INVALID_SOCKET;
bool g_wsa = false;

uint64_t key_of(const sockaddr_in& a) {
    return (static_cast<uint64_t>(a.sin_addr.s_addr) << 16) | static_cast<uint64_t>(ntohs(a.sin_port));
}
sockaddr_in addr_of(uint64_t key) {
    sockaddr_in a{};
    a.sin_family = AF_INET;
    a.sin_port = htons(static_cast<u_short>(key & 0xFFFF));
    a.sin_addr.s_addr = static_cast<ULONG>(key >> 16);
    return a;
}
}  // namespace

bool open(uint16_t port, std::string* error) {
    if (g_sock != INVALID_SOCKET) return true;
    WSADATA wsa;
    if (!g_wsa) {
        if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) {
            if (error) *error = "WSAStartup failed";
            return false;
        }
        g_wsa = true;
    }
    SOCKET s = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s == INVALID_SOCKET) {
        if (error) *error = "socket() failed: " + std::to_string(WSAGetLastError());
        return false;
    }
    sockaddr_in a{};
    a.sin_family = AF_INET;
    a.sin_port = htons(port);
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (::bind(s, reinterpret_cast<sockaddr*>(&a), sizeof(a)) != 0) {
        if (error) *error = "bind 127.0.0.1:" + std::to_string(port) + " failed: " + std::to_string(WSAGetLastError());
        closesocket(s);
        return false;
    }
    u_long nb = 1;
    ioctlsocket(s, FIONBIO, &nb);
    int big = 1 << 20;
    setsockopt(s, SOL_SOCKET, SO_RCVBUF, reinterpret_cast<const char*>(&big), sizeof(big));
    g_sock = s;
    return true;
}

bool is_open() { return g_sock != INVALID_SOCKET; }

void close() {
    if (g_sock != INVALID_SOCKET) closesocket(g_sock);
    g_sock = INVALID_SOCKET;
}

int recv(uint8_t* buffer, int capacity, uint64_t* from) {
    if (g_sock == INVALID_SOCKET) return -1;
    sockaddr_in a{};
    int alen = sizeof(a);
    const int n = recvfrom(g_sock, reinterpret_cast<char*>(buffer), capacity, 0, reinterpret_cast<sockaddr*>(&a), &alen);
    if (n == SOCKET_ERROR) {
        const int e = WSAGetLastError();
        // WSAECONNRESET: an earlier send hit a closed port (ICMP); not fatal for UDP.
        return (e == WSAEWOULDBLOCK || e == WSAECONNRESET || e == WSAEMSGSIZE) ? 0 : -1;
    }
    if (from) *from = key_of(a);
    return n;
}

bool send(uint64_t to, const uint8_t* data, uint32_t len) {
    if (g_sock == INVALID_SOCKET) return false;
    const sockaddr_in a = addr_of(to);
    return sendto(g_sock, reinterpret_cast<const char*>(data), static_cast<int>(len), 0,
                  reinterpret_cast<const sockaddr*>(&a), sizeof(a)) == static_cast<int>(len);
}

std::string describe(uint64_t key) {
    const sockaddr_in a = addr_of(key);
    const uint8_t* b = reinterpret_cast<const uint8_t*>(&a.sin_addr.s_addr);
    char buf[48];
    std::snprintf(buf, sizeof(buf), "%u.%u.%u.%u:%u", b[0], b[1], b[2], b[3], ntohs(a.sin_port));
    return buf;
}

}  // namespace knet::lab
