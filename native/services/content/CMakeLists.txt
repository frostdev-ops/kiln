# kiln.content. The gamedata worker's sections (gamedata*.cpp: templates, arsenals, sequences, buffs, perks, skills)
# are picked up when present; content.cpp / packs.cpp / store.cpp call them under __has_include("gamedata.h").
file(GLOB KILN_CONTENT_GAMEDATA CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/gamedata*.cpp")
kiln_service(content content.cpp game.cpp refl.cpp registries.cpp store.cpp packs.cpp newcontent.cpp refs.cpp barber.cpp ${KILN_CONTENT_GAMEDATA})
if(KILN_CONTENT_GAMEDATA)
    target_compile_definitions(kiln-content PRIVATE KILN_CONTENT_GAMEDATA=1)
endif()
