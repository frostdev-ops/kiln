// SPDX-License-Identifier: MIT
#pragma once
#include "kiln_api.h"
#include "services/kiln_tools.h"
#include "services/kiln_ui_ammo.h"
const KilnUiAmmoApi* kiln_tools_ammo_api();
KilnResult KILN_CALL kiln_tools_define_family(KilnOwner owner,const KilnToolFamilyDef* definition,KilnHandle* out);
KilnResult KILN_CALL kiln_tools_remove_family(KilnOwner owner,KilnHandle handle);
KilnResult KILN_CALL kiln_tools_material_snapshot(KilnEntity actor,uint32_t tool,KilnToolMaterialSnapshot* out);
KilnResult KILN_CALL kiln_tools_presentation_status(KilnToolPresentationStatus* out);
// Private native test seam. A provider returns an owned scalar observation, never a game view. It is sampled
// once per selector/brush invocation. Production publishes it from client Post and validates session/generation.
using KilnToolHeldItemProvider=uint32_t(KILN_CALL*)();
void kiln_tools_presentation_set_held_provider(KilnToolHeldItemProvider provider);
// kiln.ui.ammo's pick trace (ammo_trace.h), on or off before install: tools.cpp reads its switch once at the start.
void kiln_tools_presentation_set_ammo_trace(bool on);
void kiln_tools_presentation_install(const KilnApi* api,KilnOwner parent);
void kiln_tools_presentation_uninstall();
