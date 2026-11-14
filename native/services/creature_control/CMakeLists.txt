# kiln.creature_control: the creature control broker, kiln-creature-control.dll (19d §4.3; 19d-pets adds its native
# sources to this same target).
kiln_service(creature-control control.cpp lease.cpp program.cpp registry.cpp types_guard.cpp)
# tools/deploy-server.sh builds each service as kiln-<folder name>: the folder is creature_control, the DLL keeps the
# name of the contract.
add_custom_target(kiln-creature_control)
add_dependencies(kiln-creature_control kiln-creature-control)
