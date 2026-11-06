// /profiles/:id/emf: EMF mods run by kiln-emf (EmfApi v21 on Kiln services).
import { ModList } from "./ModList";
import { ProfileFacts } from "./ProfileLayout";
import { useProfileEdit, useProfileId } from "./useProfileEdit";

export default function ProfileEmfPage() {
  const id = useProfileId();
  const { profile, toggle, remove, update } = useProfileEdit(id);
  if (!profile) return null;
  return (
    <>
      <ModList kind="emf" mods={profile.mods.emf} fill onToggle={(m, on) => toggle("emf", m, on)} onRemove={(m) => remove("emf", m)}
        onAdd={(ms) => update((p) => { p.mods.emf.push(...ms); })}
        onVersion={(m, version, hash) => update((p) => { p.mods.emf = p.mods.emf.map((x) => (x.id === m ? { ...x, version, hash } : x)); })}
        emptyHint="No EMF mods in this profile." />
      <ProfileFacts hint="kiln-emf loads EMF mods. With EMF's own loader installed, kiln-emf steps aside. Their settings are under Configs." />
    </>
  );
}
