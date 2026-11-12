# kiln.playeranim: mods' states in the player's animation graph, merged at boot (native/core/abi/services/kiln_playeranim.h).
# typedb, blob, states and merge are portable (tests/run.sh runs them on the host against the Python reference).
kiln_service(playeranim playeranim.cpp typedb.cpp blob.cpp states.cpp merge.cpp)
