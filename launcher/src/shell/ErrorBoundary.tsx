// Route error boundary: a page that throws (or a lazy page that fails to load) shows this
// instead of blanking the whole launcher. The shell keys it by path, so navigating away recovers.
import { TriangleAlert } from "lucide-react";
import { Component, type ReactNode } from "react";
import { Button, EmptyState, Page } from "../ui";
import { t } from "../i18n";

interface State { error: Error | null }

export class RouteErrorBoundary extends Component<{ children: ReactNode }, State> {
  state: State = { error: null };
  static getDerivedStateFromError(error: Error): State {
    return { error };
  }
  componentDidCatch(error: Error) {
    console.warn("page failed:", error);
  }
  render() {
    if (!this.state.error) return this.props.children;
    return (
      <Page>
        <EmptyState icon={<TriangleAlert />} title={t("shell.errorBoundary.title")} hint={<span className="k-mono" style={{ fontSize: 12 }}>{this.state.error.message}</span>}
          action={<Button onClick={() => this.setState({ error: null })}>{t("shell.errorBoundary.tryAgain")}</Button>} />
      </Page>
    );
  }
}
