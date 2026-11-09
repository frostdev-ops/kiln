// SPDX-License-Identifier: MIT
#include "../snapshot_stage.h"
#include <cstdio>

int main() {
    int failures = 0;
    const auto check = [&](bool ok, const char* what) { std::printf("%s %s\n", ok ? "PASS" : "FAIL", what); failures += !ok; };
    ksaves::SnapshotStage stage;
    KilnSaveEvent out{}, request{};
    check(!stage.take(0, true, &out), "no final stage without an accepted request");
    request.seq = 10; request.revision = 42; request.reason = KILN_SAVE_REASON_REQUEST;
    stage.accept(request);
    check(!stage.take(9, true, &out), "a stale save identity cannot consume a newer accepted request");
    check(stage.take(10, true, &out) && out.revision == 42 && out.reason == KILN_SAVE_REASON_REQUEST && out.save_seq == 10,
          "a Pre request restages at its following Post with the captured target revision and reason");
    check(!stage.take(10, true, &out), "reentrant serialization cannot take the same request twice");
    check(!stage.take(10, false, &out), "background-write Posts never restage later mutations");
    request.seq = 20; request.revision = 43; request.reason = KILN_SAVE_REASON_AUTOSAVE;
    stage.accept(request);
    check(stage.take(20, true, &out) && out.revision == 43 && out.reason == KILN_SAVE_REASON_AUTOSAVE,
          "an autosave requested after Post restages after the next simulation");
    request.seq = 30; stage.accept(request);
    check(!stage.take(30, false, &out) && !stage.take(30, true, &out),
          "a late-Post request already snapshotted before the next Post can never overwrite its saved generation");
    request.seq = 40; stage.accept(request);
    request.seq = 50; request.revision = 44; stage.accept(request);
    check(!stage.take(40, true, &out) && stage.take(50, true, &out) && out.revision == 44,
          "a replacement request owns its own target, not the prior save's revision");
    stage.clear();
    check(!stage.take(50, true, &out), "world close discards the old session's pending stage");
    return failures ? 1 : 0;
}
