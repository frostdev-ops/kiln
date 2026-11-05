// /profiles/:id/emf: EMF mods run by kiln-emf (EmfApi v21 on Kiln services).
import { Card, Banner } from "../../ui";
import { ModList } from "./ModList";
import { useProfileEdit, useProfileId } from "./useProfileEdit";

export default function ProfileEmfPage() {
  const id = useProfileId();
  const { profile, toggle, remove, update } = useProfileEdit(id);
  if (!profile) return null;
  return (
    <div className="k-stack" style={{ gap: 16 }}>
      <Card flush title="EMF mods" sub="Loaded by kiln-emf. Settings are under Configs.">
        <ModList kind="emf" mods={profile.mods.emf} onToggle={(m, on) => toggle("emf", m, on)} onRemove={(m) => remove("emf", m)}
          onAdd={(ms) => update((p) => { p.mods.emf.push(...ms); })}
          onVersion={(m, version, hash) => update((p) => { p.mods.emf = p.mods.emf.map((x) => (x.id === m ? { ...x, version, hash } : x)); })}
          emptyHint="Import an EMF mod folder on the Library page." />
      </Card>
      <Banner tone="info">With EMF's own loader installed, kiln-emf steps aside and EMF loads its mods itself.</Banner>
    </div>
  );
}
