// SPDX-License-Identifier: MIT
// Early plugins (kiln/plugins/kiln-*.dll exporting kiln_plugin_early): run after the native mods of phase "early",
// before sigdb and hooks, in safe idle too. See early_plugins.cpp.
#pragma once

namespace kiln::early_plugins {

void run();

}  // namespace kiln::early_plugins
