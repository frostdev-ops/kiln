#!/usr/bin/env bash
# Host tests of kiln-emf's EmfApi v10 wheel, v18 attributes and v22-v26 logic (src/v26_model.*, api_*.cpp against fake Kiln
# services): no game, no Wine.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${KILN_OUT:=$here/../../../build}"
out=$KILN_OUT/emf-host-test
mkdir -p "$out"
g++ -std=c++20 -Wall -Wextra -I"$here/../include" "$here/abi_test.cpp" -o "$out/abi_test"
"$out/abi_test"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -I"$here/../include" -I"$here/../src" \
    "$here/v26_model_test.cpp" "$here/../src/v26_model.cpp" -o "$out/v26_model_test"
"$out/v26_model_test"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined \
    -I"$here/host" -I"$here/../include" -I"$here/../src" -I"$here/../../../core/abi" -I"$here/../../../services/players" \
    "$here/glue_test.cpp" "$here/../src/api_windows.cpp" "$here/../src/api_wheel.cpp" "$here/../src/api_attributes.cpp" "$here/../src/api_level.cpp" "$here/../src/api_components.cpp" "$here/../src/api_events.cpp" "$here/../src/v26_model.cpp" \
    -o "$out/glue_test"
"$out/glue_test" "$out"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -pthread -DKEMF_NET_CLIENT_TEST \
    -I"$here/host" -I"$here/../include" -I"$here/../src" -I"$here/../../../core/abi" -I"$here/../../../services/players" \
    "$here/net_test.cpp" "$here/../src/api_net.cpp" "$here/../src/api_net_client.cpp" \
    "$here/../src/net/net_service.cpp" "$here/../src/net/net_link.cpp" "$here/../src/net/net_line.cpp" "$here/../src/net/net_peers.cpp" \
    -o "$out/net_test"
"$out/net_test"
g++ -std=c++20 -O1 -g -Wall -Wextra -fsanitize=address,undefined -pthread -DKEMF_PORTRAIT_TEST \
    -I"$here/host" -I"$here/../include" -I"$here/../src" -I"$here/../../../core/abi" -I"$here/../../../services/players" -I"$here/../../../third_party" \
    "$here/portraits_test.cpp" "$here/../src/api_portraits.cpp" "$here/../src/v26_model.cpp" -o "$out/portraits_test"
"$out/portraits_test" "$out"
