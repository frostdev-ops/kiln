// Real query-cache and hook regressions: bun test scripts/nexus-capability.test.tsx.
import { expect, test } from "bun:test";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderToString } from "react-dom/server";
import { MemoryRouter } from "react-router-dom";
import { qk, useNexusEnabled } from "../src/api/hooks";
import { NexusRoute } from "../src/components/NexusGate";

function Capability() { return <span>{useNexusEnabled() ? "enabled" : "disabled"}</span>; }
const capability = (client: QueryClient) => renderToString(<QueryClientProvider client={client}><Capability /></QueryClientProvider>);

test("Nexus stays disabled until app_info succeeds with an explicit enabled capability", () => {
  const client = new QueryClient();
  expect(capability(client)).toBe("<span>disabled</span>");
  for (const info of [{}, { nexusEnabled: false }, { nexusEnabled: true }]) {
    client.setQueryData(qk.appInfo, info);
    expect(capability(client)).toBe(`<span>${info.nexusEnabled === true ? "enabled" : "disabled"}</span>`);
  }
  client.clear();
});

test("failed app_info refresh cannot reuse a cached enabled capability or mount the catalog", async () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(qk.appInfo, { nexusEnabled: true });
  expect(capability(client)).toBe("<span>enabled</span>");
  await expect(client.fetchQuery({ queryKey: qk.appInfo, queryFn: () => Promise.reject(new Error("backend unavailable")), staleTime: 0 })).rejects.toThrow("backend unavailable");
  expect(client.getQueryState(qk.appInfo)?.status).toBe("error");
  expect(client.getQueryData(qk.appInfo)).toEqual({ nexusEnabled: true });
  expect(capability(client)).toBe("<span>disabled</span>");
  const html = renderToString(<QueryClientProvider client={client}><MemoryRouter>
    <NexusRoute home="/"><span>catalog mounted</span></NexusRoute>
  </MemoryRouter></QueryClientProvider>);
  expect(html).not.toContain("catalog mounted");
  client.clear();
});
