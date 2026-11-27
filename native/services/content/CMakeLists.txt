# kiln.content. The gamedata worker's sections (gamedata*.cpp: templates, arsenals, sequences, buffs, perks, skills)
# are picked up when present; content.cpp / packs.cpp / store.cpp call them under __has_include("gamedata.h").
file(GLOB KILN_CONTENT_GAMEDATA CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/gamedata*.cpp")
# The registry stage (registry/): the stage runner, the model and the boot back-end (S5, S10, S11), the live back-end
# (registry_live.cpp, S9) and the files they share (S2-S8).
set(KILN_CONTENT_REGISTRY registry_live.cpp registry_entries.cpp registry/visibility.cpp registry/entry_compat.cpp registry/ir.cpp registry/compose.cpp registry/saves.cpp registry/claims.cpp registry/regmap.cpp registry/formulas.cpp
    registry/report.cpp registry/model.cpp registry/model_bytes.cpp registry/stage.cpp registry/archive_journal.cpp registry/stage_service.cpp registry/live_table.cpp registry/boot_bridge.cpp registry/proof.cpp registry/limits.cpp)
kiln_service(content content.cpp game.cpp nearmem.cpp refl.cpp registries.cpp store.cpp packs.cpp newcontent.cpp refs.cpp barber.cpp gear.cpp menulayout.cpp ${KILN_CONTENT_REGISTRY} ${KILN_CONTENT_GAMEDATA})
if(KILN_CONTENT_GAMEDATA)
    target_compile_definitions(kiln-content PRIVATE KILN_CONTENT_GAMEDATA=1)
endif()

# near-win-test.exe: nearmem.cpp (the near-memory policy) on a real address space under Wine, no game, and the walk's
# timing: native/services/content/tests/run.sh. Output: bin/content-tests/. (tests/near_test.cpp is the host test.)
add_executable(near-win-test tests/near_win_test.cpp nearmem.cpp)
set_target_properties(near-win-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/content-tests")

# Pending store Windows handles, filesystem replacement and optional symlink checks; no game.
add_executable(pending-win-test tests/pending_win_test.cpp)
set_target_properties(pending-win-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/content-tests")
