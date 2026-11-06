// /profiles/:id/configs: the profile's config folder (<data>/profiles/<pid>/config).
import { ConfigEditor } from "../../components/ConfigEditor";
import { useProfileId } from "./useProfileEdit";

export default function ProfileConfigsPage() {
  const id = useProfileId();
  return <div className="k-scroll" style={{ paddingBottom: 20 }}><ConfigEditor target={{ profile: id }} /></div>;
}
