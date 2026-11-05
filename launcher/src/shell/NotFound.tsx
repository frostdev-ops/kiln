// 404.
import { Compass } from "lucide-react";
import { Link } from "react-router-dom";
import { Button, EmptyState, Page } from "../ui";

export function NotFound() {
  return (
    <Page>
      <EmptyState icon={<Compass />} title="Nothing here" hint="This page does not exist." action={<Link to="/"><Button>Back to Play</Button></Link>} />
    </Page>
  );
}
