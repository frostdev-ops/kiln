// Where the editor's views run. The desktop page (EditorApp.jsx): mouse and keyboard, the launcher's Modal, a typed
// number, a <select>, hover. The Deck screen (../deck/DeckEditor.jsx) provides another env: a controller only, so a
// dialog is a Deck dialog (a layer: B closes it), a number is set on the number pad, a choice is a list, what hover
// shows follows focus, and a view's X/Y/trigger actions go to the screen's hint bar. The views and their rules stay one
// set of files: a fix lands in both.
import { createContext, useContext, useEffect, useRef } from 'react';

export const EditorEnv = createContext({
  deck: false,
  /** (props: {title, onClose, children, wide}) => the dialog frame; desktop: the launcher's Modal */
  Dialog: null,
  /** ({title, value, min, max, presets: [{label, value}], onDone}) => void: the Deck's number pad */
  askNumber: null,
  /** ({title, value, options: [{value, label, sub}], onDone}) => void: the Deck's list of choices */
  choose: null,
  /** ({actions, back} | null) => void: the open view's own actions on the Deck's hint bar (X/Y/triggers) and what B does
   *  first ({label, run}; null: the screen's own B) */
  setActions: null,
});

export const useEditorEnv = () => useContext(EditorEnv);

/** A view's actions on the Deck's hint bar while it is shown: [{button, label, run, disabled}], and `back` ({label,
 *  run}) for B before the screen's own. The run functions read the latest render. Nothing on the desktop. */
export function useDeckActions(actions, back = null) {
  const env = useEditorEnv();
  const latest = useRef({ actions, back });
  latest.current = { actions, back };
  const sig = JSON.stringify([(actions ?? []).map((a) => [a.button, a.label, !!a.disabled]), back?.label ?? null]);
  useEffect(() => {
    if (!env.setActions) return undefined;
    env.setActions({
      actions: (actions ?? []).map((a, i) => ({ ...a, run: () => latest.current.actions?.[i]?.run() })),
      back: back ? { label: back.label, run: () => latest.current.back?.run() } : null,
    });
    return undefined;
  }, [sig, env]);
  useEffect(() => () => env.setActions?.(null), [env]);
}
