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
import { useT } from "../../i18n";

export default function Kiln() {
  const t = useT();
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
  if (!game) say = t("deck.shared.noGame");
  else if (!rt) say = t("deck.kiln.readingFolder");
  else if (rt.installed) say = t("deck.kiln.inGame", { version: kilnLabel(current ?? "") });
  else if (k.shroudforge) say = t("deck.kiln.shroudforge");
  else say = t("deck.shared.kilnNotInGame");

  return (
    <Screen name="kiln" title={t("deck.app.tabs.kiln")} say={say}>
      {game && rt && (
        <div className="deck-kiln">
          {busy && (
            <p className="deck-line" data-testid="deck-kiln-task"><Word tone="ember">{change?.what}</Word> <span className="deck-dim">{task?.message ?? t("deck.shared.starting")}</span>
              {task?.progress != null && <span className="deck-bar" aria-hidden><i style={{ width: `${Math.round(task.progress * 100)}%` }} /></span>}</p>
          )}
          {!busy && change?.state === "failed" && <p className="deck-line"><Word tone="err">{t("deck.shared.whatFailed", { what: change.what })}</Word> <span className="deck-dim">{change.error?.message}</span></p>}
          {!busy && change?.state === "done" && <p className="deck-line"><Word tone="ok">{t("deck.kiln.whatDone", { what: change.what })}</Word></p>}
          {running && <p className="deck-line"><Word tone="warn">{t("deck.shared.closeGameFirst")}</Word></p>}
          {updating && <p className="deck-line"><Word tone="warn">{t("deck.shared.steamUpdating")}</Word></p>}
          <dl className="deck-facts" tabIndex={0} data-deck-a="-" data-deck-id="facts" aria-label={t("deck.kiln.factsAria")}>
            <div><dt>{t("deck.kiln.version")}</dt><dd>{current ? kilnLabel(current) : t("deck.kiln.none")}</dd></div>
            <div><dt>{rt.receipt?.updated ? t("deck.kiln.updated") : t("deck.kiln.installed")}</dt><dd>{rt.receipt ? ago(rt.receipt.updated ?? rt.receipt.installed) : "-"}</dd></div>
            <div><dt>{t("deck.kiln.files")}</dt><dd>{rt.modified.length ? t("deck.kiln.filesChanged", { files: rt.receipt ? rt.receipt.files.length : "-", changed: rt.modified.length }) : rt.receipt ? rt.receipt.files.length : "-"}</dd></div>
            <div><dt>{t("deck.kiln.runsOn")}</dt><dd>{game.platform === "proton" ? "Proton" : game.platform === "wine" ? "Wine" : "Windows"}</dd></div>
          </dl>
          <div className="deck-row-buttons">
            {!rt.installed && !k.shroudforge && (
              <DeckButton id="install" primary autoFocus disabled={blocked || !k.bundle} onClick={() => setOpen("install")}>{t("deck.shared.installKiln")}</DeckButton>
            )}
            {rt.installed && upgrade && (
              <DeckButton id="update" primary autoFocus disabled={blocked} onClick={() => setOpen("update")}>{t("deck.shared.updateTo", { version: kilnLabel(upgrade) })}</DeckButton>
            )}
            {rt.installed && (
              <HoldButton id="uninstall" disabled={blocked} onConfirm={() => void runRuntimeChange(qc, game, k.bundles, t("deck.kiln.uninstall"), null, t("deck.kiln.removed"), () => api.runtimeUninstall(game.id))}>
                {t("deck.kiln.holdUninstall")}
              </HoldButton>
            )}
            {k.shroudforge && <DeckButton id="desktop" onClick={() => switchUi(nav, "desktop")}>{t("deck.shared.desktopUi")}</DeckButton>}
          </div>
          {rt.installed && !upgrade && <p className="deck-note">{t("deck.kiln.upToDate")}</p>}
        </div>
      )}
      {!game && <DeckButton id="go-home" onClick={() => nav("/deck")}>{t("deck.shared.home")}</DeckButton>}
      {open && game && <InstallDialog game={game} update={open === "update"} onClose={() => setOpen(null)} />}
    </Screen>
  );
}
