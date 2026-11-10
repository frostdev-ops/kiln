# kiln.content. The gamedata worker's sections (gamedata*.cpp: templates, arsenals, sequences, buffs, perks, skills)
# are picked up when present; content.cpp / packs.cpp / store.cpp call them under __has_include("gamedata.h").
file(GLOB KILN_CONTENT_GAMEDATA CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/gamedata*.cpp")
kiln_service(content content.cpp game.cpp nearmem.cpp refl.cpp registries.cpp store.cpp packs.cpp newcontent.cpp refs.cpp barber.cpp ${KILN_CONTENT_GAMEDATA})
if(KILN_CONTENT_GAMEDATA)
    target_compile_definitions(kiln-content PRIVATE KILN_CONTENT_GAMEDATA=1)
endif()

# near-win-test.exe: nearmem.cpp (the near-memory policy) on a real address space under Wine, no game, and the walk's
# timing: native/services/content/tests/run.sh. Output: bin/content-tests/. (tests/near_test.cpp is the host test.)
add_executable(near-win-test tests/near_win_test.cpp nearmem.cpp)
set_target_properties(near-win-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/content-tests")
