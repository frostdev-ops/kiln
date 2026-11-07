# SPDX-License-Identifier: MIT
# kiln-ember.dll: the Kiln side of Ember (zachthedev/enshrouded-ember, MIT), the Rust server SDK. Ember has no loader
# release and no mod ABI yet (its published crates 0.1.0 hold module docs only), so this plugin finds Ember mods
# (ember/mods/<mod>/, profile mods.ember), steps aside for Ember's own loader, and reports each mod in kiln.log and
# kiln/state/ember.json. docs/kiln/engines/ember.md. Deployed to <game>/kiln/plugins/.
kiln_plugin(ember src/plugin.cpp)

add_executable(ember-contract-test contract_test.cpp)
target_include_directories(ember-contract-test PRIVATE "${CMAKE_SOURCE_DIR}/core/abi" "${CMAKE_SOURCE_DIR}/third_party")
set_target_properties(ember-contract-test PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin/ember-tests")
add_dependencies(kiln-ember ember-contract-test)
