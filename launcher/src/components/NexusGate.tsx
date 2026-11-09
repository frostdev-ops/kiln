import type { PropsWithChildren } from "react";
import { Navigate } from "react-router-dom";
import { useAppInfo } from "../api/hooks";

/** A hidden catalog route must not mount its queries, including before capability discovery finishes. */
export function NexusRoute({ children, home }: PropsWithChildren<{ home: string }>) {
  const info = useAppInfo();
  if (!info.isFetched) return null;
  return info.isSuccess && info.data?.nexusEnabled === true ? children : <Navigate to={home} replace />;
}
