# kiln.saves: native/services/saves -> bin/services/kiln-saves.dll (see docs/kiln/services/saves.md)
kiln_service(saves saves.cpp)
target_link_libraries(kiln-saves PRIVATE shell32 ole32 advapi32)
