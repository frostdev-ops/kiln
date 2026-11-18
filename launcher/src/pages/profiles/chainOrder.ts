// The chain card's texts for compat rules that order two DLLs (dev.29 action `before`: the subject loads first, the
// target late). Backend: kiln_manage::compat (RuleResult.early/warning, ChainViewEntry.earlyBy/lateBy). Pure, so the
// unit tests (unit/chain-order.test.ts) hold the sentences a player reads.
import type { ChainViewEntry, CompatRuleResult } from "../../api/commands";
import { t } from "../../i18n";

const file = (p: string) => p.split(/[\\/]/).pop() ?? p;
const ruleId = (by: string | null | undefined) => (by?.startsWith("rule:") ? by.slice(5) : null);

/** What an applied `before` rule does, in a player's words: its message, else a sentence from the file names (the same
 *  as RuleResult::order_sentence, which the launcher logs at activation). */
export function orderSentence(r: CompatRuleResult): string {
  const said = r.message.trim();
  if (said) return said;
  const names = (v: string[] | undefined) => (v ?? []).map(file).join(", ");
  return t("profiles.chain.orderSentence", { targets: names(r.targets), early: names(r.early), id: r.id });
}

export interface EntryOrder {
  rule: CompatRuleResult;
  /** true: the rule keeps this entry loading first; false: it loads this entry late. */
  first: boolean;
  /** The rule's sentence, shown under the entry. */
  text: string;
  /** The late switch's hint: who decides it now. */
  hint: string;
}

/** The `before` rule that decides this entry's late switch, or null when none does (then the switch is the player's,
 *  the profile's or another rule's, as before). */
export function entryOrder(e: ChainViewEntry, rules: CompatRuleResult[]): EntryOrder | null {
  const early = ruleId(e.earlyBy);
  const id = early ?? ruleId(e.lateBy);
  const rule = id ? rules.find((r) => r.id === id && r.action === "before" && r.applied) : undefined;
  if (!rule) return null;
  const first = early === rule.id;
  return { rule, first, text: orderSentence(rule), hint: first ? t("profiles.chain.loadsFirstRule", { id: rule.id }) : t("profiles.chain.loadsLateRule", { id: rule.id }) };
}

/** The `before` rules that fired but could not order their DLLs (nothing was moved), as warnings. */
export function orderWarnings(rules: CompatRuleResult[]): { id: string; text: string }[] {
  return rules.filter((r) => r.fired && r.action === "before" && r.warning).map((r) => ({ id: r.id, text: r.warning! }));
}

/** A `before` rule's two sides in the rules list: "dinput8.dll before xinput9_1_0.dll". */
export function orderSides(r: CompatRuleResult): string | null {
  if (r.action !== "before" || !r.early?.length || !r.targets.length) return null;
  return t("profiles.chain.sides", { early: r.early.map(file).join(", "), targets: r.targets.map(file).join(", ") });
}
