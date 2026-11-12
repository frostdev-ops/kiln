// A path in the mono style, shortened in the middle when it does not fit ("/home/player/.loc…/kiln"): the last name
// always shows, the full path is the title (and the text, for copy and screen readers).
import { splitPath } from "./format";

export function PathText({ path, className, testId }: { path: string; className?: string; testId?: string }) {
  const [head, tail] = splitPath(path);
  return (
    <span className={"k-path k-mono" + (className ? ` ${className}` : "")} title={path} data-testid={testId}>
      {head && <span className="k-path-head">{head}</span>}
      <span className="k-path-tail">{tail}</span>
    </span>
  );
}
