// Placeholder page: names the route until the page exists.
import { Hammer } from "lucide-react";
import { useParams } from "react-router-dom";
import { EmptyState, Page, PageHeader } from "../ui";

export function Placeholder({ title, route }: { title: string; route: string }) {
  const params = useParams();
  return (
    <Page>
      <PageHeader title={title} />
      <EmptyState icon={<Hammer />} title="Not built yet" hint={<><code>{route}</code>{params.id ? <> · <code>{params.id}</code></> : null}</>} />
    </Page>
  );
}
