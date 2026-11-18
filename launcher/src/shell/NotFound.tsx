// 404.
import { Compass } from "lucide-react";
import { Link } from "react-router-dom";
import { Button, EmptyState, Page } from "../ui";
import { useT } from "../i18n";

export function NotFound() {
  const t = useT();
  return (
    <Page>
      <EmptyState icon={<Compass />} title={t("shell.notFound.title")} hint={t("shell.notFound.hint")} action={<Link to="/"><Button>{t("shell.notFound.back")}</Button></Link>} />
    </Page>
  );
}
