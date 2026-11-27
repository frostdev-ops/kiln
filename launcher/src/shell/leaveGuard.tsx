// Leaving a page with unsaved work asks first (netjunk, 2026-11-26: a server's settings were left behind without a word).
// A page marks its work with useLeaveGuard while it is not saved; every way out through the router (the header, a server's
// tabs and switcher, the pilot light, Back) then stops at one dialog: Save (and go), Discard (and go) or Stay. Closing
// the window has its own question (shell/closeGuard.ts); the launcher's restart reads shell/unsaved.ts.
import { useCallback, useEffect, useId, useRef, useState } from "react";
import { useBlocker, type BlockerFunction } from "react-router-dom";
import { create } from "zustand";
import { useT } from "../i18n";
import { Button, Modal } from "../ui";

export interface LeaveGuard {
  /** What is not saved, in a few words ("the server's settings", a file's name). */
  what: string;
  /** Saves it; false when it did not save (the page says why). Absent: it cannot be saved now (Discard or Stay). */
  save?: () => Promise<boolean>;
  /** Throws the changes away. */
  discard: () => void;
}

const useGuards = create<{ by: Record<string, LeaveGuard> }>(() => ({ by: {} }));

/** This component's unsaved work while `dirty`: leaving the page asks first. It goes when the component does. */
export function useLeaveGuard(dirty: boolean, guard: LeaveGuard) {
  const key = useId();
  const latest = useRef(guard);
  latest.current = guard;
  const canSave = !!guard.save;
  const what = guard.what;
  useEffect(() => {
    if (!dirty) return;
    const g: LeaveGuard = {
      what,
      save: canSave ? () => latest.current.save?.() ?? Promise.resolve(false) : undefined,
      discard: () => latest.current.discard(),
    };
    useGuards.setState((s) => ({ by: { ...s.by, [key]: g } }));
    return () => useGuards.setState((s) => {
      const by = { ...s.by };
      delete by[key];
      return { by };
    });
  }, [key, dirty, canSave, what]);
}

/** Leaving for another page (not a change of the same page's hash or query) while some work is unsaved. */
export const leaving: BlockerFunction = ({ currentLocation, nextLocation }) =>
  Object.keys(useGuards.getState().by).length > 0 && currentLocation.pathname !== nextLocation.pathname;

/** The dialog every way out stops at (mounted once, in the shell). */
export function LeaveGuardHost() {
  const t = useT();
  const blocker = useBlocker(useCallback<BlockerFunction>((a) => leaving(a), []));
  const guards = useGuards((s) => s.by);
  const [saving, setSaving] = useState(false);
  const blocked = blocker.state === "blocked";
  const list = Object.values(guards);
  // one answer per blocked way out: the dialog's buttons and the effect below must not both go (a second proceed would
  // navigate twice)
  const answered = useRef(false);
  useEffect(() => { if (blocked) answered.current = false; }, [blocked, blocker.location]);
  const answer = (go: boolean) => {
    if (answered.current || blocker.state !== "blocked") return;
    answered.current = true;
    if (go) blocker.proceed(); else blocker.reset();
  };
  // the work was saved or thrown away meanwhile: nothing to ask any more
  useEffect(() => {
    if (blocked && list.length === 0) answer(true);
  });
  const stay = () => { if (!saving) answer(false); };
  const discard = () => {
    for (const g of list) g.discard();
    answer(true);
  };
  const canSave = list.length > 0 && list.every((g) => g.save);
  const save = async () => {
    setSaving(true);
    try {
      for (const g of list) if (!(await g.save!())) return answer(false);
      answer(true);
    } finally {
      setSaving(false);
    }
  };
  const what = [...new Set(list.map((g) => g.what))].join(", ");
  return (
    <Modal open={blocked && list.length > 0} onClose={stay} locked={saving} title={t("shell.leaveGuard.title")} width={480}
      footer={<>
        <Button variant="ghost" data-autofocus onClick={stay} disabled={saving}>{t("shell.leaveGuard.stay")}</Button>
        <Button variant="danger" onClick={discard} disabled={saving}>{t("shell.leaveGuard.discard")}</Button>
        {canSave && <Button variant="primary" onClick={() => void save()} loading={saving}>{t("shell.leaveGuard.save")}</Button>}
      </>}>
      <p style={{ margin: 0 }}>{t("shell.leaveGuard.text", { what })}</p>
      {!canSave && <p className="k-muted" style={{ margin: "8px 0 0" }}>{t("shell.leaveGuard.cannotSave")}</p>}
    </Modal>
  );
}
