// Placeholder page: names the route until the page exists.
import { Hammer } from "lucide-react";
import { useParams } from "react-router-dom";
import { EmptyState, Page, PageHeader } from "../ui";
import { useT } from "../i18n";

export function Placeholder({ title, route }: { title: string; route: string }) {
  const t = useT();
  const params = useParams();
  return (
    <Page>
      <PageHeader title={title} />
      <EmptyState icon={<Hammer />} title={t("shell.placeholder.notBuilt")} hint={<><code>{route}</code>{params.id ? <> · <code>{params.id}</code></> : null}</>} />
    </Page>
  );
}
