// SPDX-License-Identifier: MIT
// Hosts .NET from a private runtime (<game>/kiln/dotnet) and starts Kiln.Host (<game>/kiln/host/Kiln.Host.dll).
// The host gets the same KilnApi table as native plugins.
#pragma once
#include "common.h"

namespace kiln::clr {

bool boot(const KilnApi* api);
bool running();

}  // namespace kiln::clr
