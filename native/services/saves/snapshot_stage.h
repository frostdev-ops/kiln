// SPDX-License-Identifier: MIT
#pragma once
#include "../../core/abi/services/kiln_saves.h"

namespace ksaves {
// The service mutex protects this state. One accepted request gets one final, safe Post stage. Retain the target
// event itself: a background write or a later request must never supply this request's revision or reason.
class SnapshotStage {
    KilnSaveEvent request_{};
    bool taken_ = false;
public:
    void accept(const KilnSaveEvent& request) { request_ = request; taken_ = false; }
    void clear() { request_ = {}; taken_ = false; }
    bool take(uint64_t current, bool still_requested, KilnSaveEvent* out) {
        if (!request_.seq || taken_ || request_.seq != current) return false;
        taken_ = true; // before managed serialization: reentry cannot stage this request twice
        if (!still_requested) return false; // already snapshotted (e.g. a request from a later Post callback)
        *out = request_;
        out->kind = KILN_SAVE_EVENT_SNAPSHOT;
        out->save_seq = request_.seq;
        return true;
    }
};
}
