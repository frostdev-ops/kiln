// /profiles/:id/mods: the profile's Kiln mods as the load order the resolver gives (client or server side): numerals,
// flame toggles (light a mod or put it out and the order closes up), name and id, version, and why a mod stays out.
// On a client, side "server" mods (and the mods that need one) come next, unnumbered: they start with the player's own
// world (ResolvePreview.withWorld), unless the game's kiln.json turns that off. Rows glide to their new slots, keyed by
// mod id. The aside holds the side switch, the resolver's sentence and Add mod.
// Below: "In the game folder" (GameFolder.tsx), the mods of every kind a player put in the game folder by hand.
import { useQuery } from "@tanstack/react-query";
import { MoreHorizontal } from "lucide-react";
import { useEffect, useState } from "react";
import * as api from "../../api/commands";
import type { ProfileMod, RunSide } from "../../api/commands";
import { qk, useGame, useLibrary } from "../../api/hooks";
import { Banner, Button, FlameToggle, Menu, Segmented, Skeleton, errorText, type MenuItem } from "../../ui";
import { GameFolderSection } from "./GameFolder";
import { AddModsModal, libName } from "./ModList";
import { ProfileAside, useActivation, useOpenSettings } from "./ProfileLayout";
import { useProfileEdit, useProfileId } from "./useProfileEdit";

const ROW = 48;
const GAP = 30;

export default function ProfileModsPage() {
  const id = useProfileId();
  const { profile, toggle, remove, update } = useProfileEdit(id);
  const [side, setSide] = useState<RunSide>("client");
  const [adding, setAdding] = useState(false);
  const { game } = useGame();
  const gameId = game?.id ?? null;
  const preview = useQuery({ queryKey: qk.resolve(id, side, gameId), queryFn: () => api.profileResolve(id, side, gameId), enabled: !!profile });
  const lib = useLibrary("kiln");
  // the picker lists the Library as it is now: a mod imported a moment ago on the Library page must be in it
  const refetchLib = lib.refetch;
  useEffect(() => {
    if (adding) void refetchLib();
  }, [adding, refetchLib]);
  const act = useActivation();
  const openSettings = useOpenSettings();
  if (!profile) return null;
  const mods = profile.mods.kiln;
  const order = preview.data?.kiln.order ?? [];
  const rejected = new Map((preview.data?.kiln.rejected ?? []).map((r) => [r.id, r]));
  const loading = mods.filter((m) => m.enabled && (order.includes(m.id) || (!rejected.has(m.id) && !preview.data)))
    .sort((a, b) => order.indexOf(a.id) - order.indexOf(b.id));
  const withWorld = preview.data?.withWorld ?? [];
  const later = mods.filter((m) => m.enabled && withWorld.includes(m.id)).sort((a, b) => withWorld.indexOf(a.id) - withWorld.indexOf(b.id));
  const out = mods.filter((m) => !loading.includes(m) && !later.includes(m));
  const reason = (m: ProfileMod) => !m.enabled ? "Put out in this profile" : rejected.get(m.id)?.reason ?? (preview.data ? "Not in the load order" : "");
  const y = (m: ProfileMod) => {
    const li = loading.indexOf(m);
    if (li >= 0) return li * ROW;
    const wi = later.indexOf(m);
    return wi >= 0 ? (loading.length + wi) * ROW : (loading.length + later.length) * ROW + GAP + out.indexOf(m) * ROW;
  };
  const versions = (m: ProfileMod) => lib.data?.find((l) => l.id === m.id)?.versions ?? [];
  const menu = (m: ProfileMod): MenuItem[] => [
    ...versions(m).filter((v) => v.version !== m.version).map((v) => ({
      label: `Use ${v.version}`, onSelect: () => update((p) => { p.mods.kiln = p.mods.kiln.map((x) => (x.id === m.id ? { ...x, version: v.version, hash: v.hash } : x)); }),
    })),
    { label: "Remove from this profile", danger: true, onSelect: () => remove("kiln", m.id) },
  ];
  const counts = profile.mods.native.length + profile.mods.plugin.length + profile.mods.emf.length;
  const stays = out.length;
  const height = mods.length ? (loading.length + later.length + out.length) * ROW + (out.length ? GAP : 0) : 0;
  return (
    <>
      {preview.data?.warnings.map((w) => <Banner key={w} tone="warn" title="Resolver">{w}</Banner>)}
      <div className="k-scroll" style={{ paddingBottom: 12 }}>
        {!mods.length ? (
          <div className="k-empty" style={{ padding: "16px 0" }}>
            <div className="k-empty-title">No Kiln mods in this profile.</div>
            <div className="k-empty-action"><Button onClick={() => setAdding(true)}>Add from the library</Button></div>
          </div>
        ) : (
          <ol className="k-order" aria-label={`Load order on a ${side}`} style={{ height }}>
            {out.length > 0 && (
              <li className="k-order-out" aria-hidden style={{ transform: `translateY(${(loading.length + later.length) * ROW}px)` }}><span>Not loading</span><i /></li>
            )}
            {mods.map((m, ix) => {
              const li = loading.indexOf(m);
              const withIt = later.includes(m);
              const lit = li >= 0 || withIt;
              const why = lit ? null : reason(m);
              const name = libName(lib.data, "kiln", m.id);
              const missing = lib.data && !lib.data.some((l) => l.id === m.id);
              return (
                <li key={m.id} className={"k-order-row" + (lit ? " lit" : "")} style={{ transform: `translateY(${y(m)}px)`, animationDelay: `${(0.12 + ix * 0.035).toFixed(3)}s` }}>
                  <span className="k-order-num" aria-hidden>{li >= 0 ? li + 1 : ""}</span>
                  <FlameToggle on={m.enabled} label={name} onChange={(on) => toggle("kiln", m.id, on)} />
                  <span className="k-name-id"><span>{name}</span>{name !== m.id && <span className="k-mono">{m.id}</span>}</span>
                  <span className="k-mono k-order-ver" title={versions(m).length > 1 ? `${versions(m).length} versions in the library` : undefined}>{m.version}</span>
                  <span className={"k-order-note" + (why ? " why" : "")} title={withIt ? rejected.get(m.id)?.reason : why ?? undefined}>
                    {missing ? "Not in the library" : withIt ? "Starts with your world" : why || lib.data?.find((l) => l.id === m.id)?.versions.find((v) => v.version === m.version)?.side || ""}
                  </span>
                  <Menu label={`More for ${name}`} icon={<MoreHorizontal />} size="s" items={menu(m)} />
                  {li >= 0 && <span className="k-sr-only">loads {li + 1}.</span>}
                </li>
              );
            })}
          </ol>
        )}
        <div style={{ marginTop: 24 }}><GameFolderSection profileId={id} /></div>
      </div>
      <ProfileAside>
        <span className="k-label" style={{ fontSize: 16 }}>Load order for</span>
        <div style={{ marginTop: -10 }}>
          <Segmented variant="side" aria-label="Load order for" value={side} onChange={setSide} options={[{ id: "client", label: "Client" }, { id: "server", label: "Server" }]} />
        </div>
        {preview.isLoading ? <Skeleton lines={2} /> : preview.error ? <p className="k-error-text">{errorText(preview.error)}</p> : (
          <p style={{ fontSize: 19, lineHeight: 1.28, color: "var(--k-head)" }}>
            {loading.length} mod{loading.length === 1 ? "" : "s"} load on a {side}.
            {later.length ? ` ${later.length} ${later.length === 1 ? "starts" : "start"} with your world.` : ""}
            {stays ? ` ${stays} ${stays === 1 ? "stays" : "stay"} out.` : ""}
          </p>
        )}
        <p className="k-hint" style={{ fontSize: 16 }}>
          Light a mod or put it out and the order closes up. {counts ? `${profile.mods.native.length + profile.mods.plugin.length} native, ${profile.mods.emf.length} EMF in this profile.` : "No native or EMF mods in this profile."}
        </p>
        <div className="k-row" style={{ marginTop: "auto", gap: 8 }}>
          {act.button}
          <Button variant={act.needsAction ? "secondary" : "primary"} onClick={() => setAdding(true)}>Add mod</Button>
          <Button onClick={openSettings}>Settings</Button>
        </div>
      </ProfileAside>
      <AddModsModal open={adding} onClose={() => setAdding(false)} kind="kiln" have={mods.map((m) => m.id)} lib={lib.data ?? []}
        onAdd={(ms) => { update((p) => { p.mods.kiln.push(...ms); }); setAdding(false); }}
        loading={lib.isFetching && !lib.data?.length} error={lib.error ? errorText(lib.error) : undefined} />
    </>
  );
}
