// Deck Kiln: the runtime in the game folder: Install, Update, Uninstall (hold A), and the running step. Rollback,
// the proxy chain and "switch from ShroudForge" (typed) stay in the desktop UI.
import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useNavigate } from "react-router-dom";
import * as api from "../../api/commands";
import { useGame, useGameStatus, useRuntimeChanges } from "../../api/hooks";
import { useTasks } from "../../ui";
import { useKilnInstall } from "../../shell/firstRun";
import { isOlder, runRuntimeChange } from "../../shell/runtimeChange";
import { InstallDialog } from "../dialogs";
import { switchUi } from "../mode";
import { DeckButton, HoldButton, Screen, Word } from "../ui";
import { ago, kilnLabel } from "../status";

export default function Kiln() {
  const qc = useQueryClient();
  const nav = useNavigate();
  const { game } = useGame();
  const status = useGameStatus(game?.id);
  const k = useKilnInstall(game);
  const change = useRuntimeChanges((s) => (game ? s.last[game.id] : undefined));
  const task = useTasks((s) => (change?.taskId ? s.tasks[change.taskId] : undefined));
  const [open, setOpen] = useState<null | "install" | "update">(null);
  const rt = status.data?.runtime;
  const running = !!status.data?.running;
  const updating = !!status.data?.steamUpdating;
  const busy = change?.state === "running";
  const current = rt?.receipt?.runtime.version ?? null;
  // a newer bundle than the installed one (the launcher's own, or one imported on the desktop)
  const newer = current ? k.bundles.find((b) => b.sides.includes("client") && isOlder(current, b.version, k.bundles)) : null;
  const upgrade = rt?.upgradeAvailable ?? newer?.version ?? null;
  const blocked = running || updating || busy;

  let say: string;
  if (!game) say = "No game yet.";
  else if (!rt) say = "Reading the game folder.";
  else if (rt.installed) say = `Kiln ${kilnLabel(current ?? "")} is in the game.`;
  else if (k.shroudforge) say = "ShroudForge is in this game. Kiln loads every engine's mods: switch in the desktop UI.";
  else say = "Kiln is not in the game yet.";

  return (
    <Screen name="kiln" title="Kiln" say={say}>
      {game && rt && (
        <div className="deck-kiln">
          {busy && (
            <p className="deck-line" data-testid="deck-kiln-task"><Word tone="ember">{change?.what}</Word> <span className="deck-dim">{task?.message ?? "Starting"}</span>
              {task?.progress != null && <span className="deck-bar" aria-hidden><i style={{ width: `${Math.round(task.progress * 100)}%` }} /></span>}</p>
          )}
          {!busy && change?.state === "failed" && <p className="deck-line"><Word tone="err">{change.what} failed</Word> <span className="deck-dim">{change.error?.message}</span></p>}
          {!busy && change?.state === "done" && <p className="deck-line"><Word tone="ok">{change.what} done</Word></p>}
          {running && <p className="deck-line"><Word tone="warn">Close the game first</Word></p>}
          {updating && <p className="deck-line"><Word tone="warn">Steam is updating the game</Word></p>}
          <dl className="deck-facts" tabIndex={0} data-deck-a="-" data-deck-id="facts" aria-label="Kiln in the game">
            <div><dt>Version</dt><dd>{current ? kilnLabel(current) : "None"}</dd></div>
            <div><dt>{rt.receipt?.updated ? "Updated" : "Installed"}</dt><dd>{rt.receipt ? ago(rt.receipt.updated ?? rt.receipt.installed) : "-"}</dd></div>
            <div><dt>Files</dt><dd>{rt.receipt ? rt.receipt.files.length : "-"}{rt.modified.length ? `, ${rt.modified.length} changed` : ""}</dd></div>
            <div><dt>Runs on</dt><dd>{game.platform === "proton" ? "Proton" : game.platform === "wine" ? "Wine" : "Windows"}</dd></div>
          </dl>
          <div className="deck-row-buttons">
            {!rt.installed && !k.shroudforge && (
              <DeckButton id="install" primary autoFocus disabled={blocked || !k.bundle} onClick={() => setOpen("install")}>Install Kiln</DeckButton>
            )}
            {rt.installed && upgrade && (
              <DeckButton id="update" primary autoFocus disabled={blocked} onClick={() => setOpen("update")}>Update to {kilnLabel(upgrade)}</DeckButton>
            )}
            {rt.installed && (
              <HoldButton id="uninstall" disabled={blocked} onConfirm={() => void runRuntimeChange(qc, game, k.bundles, "Uninstall", null, "Kiln removed", () => api.runtimeUninstall(game.id))}>
                Hold to uninstall
              </HoldButton>
            )}
            {k.shroudforge && <DeckButton id="desktop" onClick={() => switchUi(nav, "desktop")}>Desktop UI</DeckButton>}
          </div>
          {rt.installed && !upgrade && <p className="deck-note">Up to date.</p>}
        </div>
      )}
      {!game && <DeckButton id="go-home" onClick={() => nav("/deck")}>Home</DeckButton>}
      {open && game && <InstallDialog game={game} update={open === "update"} onClose={() => setOpen(null)} />}
    </Screen>
  );
}
