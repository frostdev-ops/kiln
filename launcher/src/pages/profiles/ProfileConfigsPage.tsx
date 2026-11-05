// /profiles/:id/configs: the profile's config folder (<data>/profiles/<pid>/config).
import { ConfigEditor } from "../../components/ConfigEditor";
import { useProfileId } from "./useProfileEdit";

export default function ProfileConfigsPage() {
  const id = useProfileId();
  return <ConfigEditor target={{ profile: id }} />;
}
