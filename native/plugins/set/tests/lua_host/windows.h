#pragma once
#include <cstdint>
#include <cstring>
#include <cstdio>
#include <cstdlib>
#include <chrono>
#include <thread>
#include <filesystem>
#include <vector>
#include <string>
#include <cstdarg>
#include <strings.h>
#define __try try
#define __except(...) catch (...)
#define EXCEPTION_ACCESS_VIOLATION 1
#define EXCEPTION_EXECUTE_HANDLER 1
#define EXCEPTION_CONTINUE_SEARCH 0
#define GetExceptionCode() 0
#define PAGE_READWRITE 4
#define PAGE_EXECUTE_READWRITE 64
#define _stricmp strcasecmp
#define sscanf_s sscanf
using DWORD = unsigned long;
using ULONGLONG = unsigned long long;
using HWND = void*;
using HMODULE = void*;
using HANDLE = void*;
constexpr int VK_SPACE=0x20,VK_TAB=9,VK_RETURN=0x0D,VK_BACK=8,VK_CAPITAL=0x14,VK_LEFT=0x25,VK_UP=0x26,VK_RIGHT=0x27,VK_DOWN=0x28,VK_SNAPSHOT=0x2C,VK_SCROLL=0x91,VK_NUMLOCK=0x90,
 VK_NUMPAD0=0x60,VK_NUMPAD1=0x61,VK_NUMPAD2=0x62,VK_NUMPAD3=0x63,VK_NUMPAD4=0x64,VK_NUMPAD5=0x65,VK_NUMPAD6=0x66,VK_NUMPAD7=0x67,VK_NUMPAD8=0x68,VK_NUMPAD9=0x69,
 VK_MULTIPLY=0x6A,VK_ADD=0x6B,VK_SUBTRACT=0x6D,VK_DECIMAL=0x6E,VK_DIVIDE=0x6F,VK_OEM_1=0xBA,VK_OEM_PLUS=0xBB,VK_OEM_COMMA=0xBC,VK_OEM_MINUS=0xBD,VK_OEM_PERIOD=0xBE,
 VK_OEM_2=0xBF,VK_OEM_4=0xDB,VK_OEM_5=0xDC,VK_OEM_6=0xDD,VK_OEM_7=0xDE,VK_OEM_102=0xE2;
struct LARGE_INTEGER { long long QuadPart; };
inline int QueryPerformanceCounter(LARGE_INTEGER* p){p->QuadPart=std::chrono::steady_clock::now().time_since_epoch().count();return 1;}
inline int QueryPerformanceFrequency(LARGE_INTEGER* p){p->QuadPart=1000000000;return 1;}
constexpr int VK_OEM_3=192,VK_INSERT=45,VK_HOME=36,VK_END=35,VK_DELETE=46,VK_PRIOR=33,VK_NEXT=34,VK_PAUSE=19,VK_F1=112,VK_F24=135,VK_CONTROL=17,VK_SHIFT=16,VK_MENU=18;
constexpr DWORD FILE_ATTRIBUTE_DIRECTORY=16, FILE_ATTRIBUTE_REPARSE_POINT=1024, INVALID_FILE_ATTRIBUTES=~0ul;
#define INVALID_HANDLE_VALUE reinterpret_cast<void*>(-1)
constexpr DWORD MEM_COMMIT=4096,PAGE_GUARD=256,PAGE_NOACCESS=1;
struct MEMORY_BASIC_INFORMATION {void* BaseAddress;size_t RegionSize;DWORD State,Protect;};
struct HostRegion {uintptr_t base;size_t size;DWORD protection;bool fail=false;};
inline std::vector<HostRegion> hostRegions;
inline size_t VirtualQuery(const void* p,MEMORY_BASIC_INFORMATION* out,size_t){
 uintptr_t at=reinterpret_cast<uintptr_t>(p);
 for(auto& r:hostRegions)if(at>=r.base&&at-r.base<r.size){*out={reinterpret_cast<void*>(r.base),r.size,MEM_COMMIT,r.protection};return sizeof(*out);}
 *out={const_cast<void*>(p),4096,MEM_COMMIT,PAGE_READWRITE};return sizeof(*out);
}
inline bool VirtualProtect(void* p,size_t n,DWORD protection,DWORD* old){
 uintptr_t at=reinterpret_cast<uintptr_t>(p);
 for(auto& r:hostRegions)if(at>=r.base&&at-r.base<r.size){if(r.fail)return false;if(n>r.size-(at-r.base))return false;*old=r.protection;r.protection=protection;return true;}
 *old=PAGE_READWRITE;return true;
}
inline HMODULE GetModuleHandleW(const wchar_t*){return nullptr;}
inline HWND GetForegroundWindow(){return nullptr;}
inline DWORD GetCurrentProcessId(){return 0;}
inline void GetWindowThreadProcessId(HWND,DWORD* p){*p=0;}
inline HWND GetConsoleWindow(){return nullptr;}
inline int GetAsyncKeyState(int){return 0;}
inline ULONGLONG GetTickCount64(){return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();}
inline void Sleep(unsigned n){std::this_thread::sleep_for(std::chrono::milliseconds(n));}
inline DWORD GetPrivateProfileStringW(const wchar_t*,const wchar_t*,const wchar_t*,wchar_t* b,DWORD n,const wchar_t*){if(n)*b=0;return 0;}
inline bool WritePrivateProfileStringW(const wchar_t*,const wchar_t*,const wchar_t*,const wchar_t*){return true;}
