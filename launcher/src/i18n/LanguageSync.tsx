// Applies the launcher's language: the saved setting (LauncherSettings.language), mirrored in localStorage so the next
// start's first screen (and the data folder dialog, before settings can be read) is already in it; and the translation
// files in the data folder's locales/ (app_locales), read at the start and again from Settings.
import { useQuery } from "@tanstack/react-query";
import { parse, type ParseError } from "jsonc-parser";
import { useEffect } from "react";
import * as api from "../api/commands";
import { useSettings } from "../api/hooks";
import { setLanguage, setUserLocales, type LanguagePref } from "./index";

const STORE = "kiln.language";

/** The preference the last start saved (before settings load). */
export function storedLanguage(): LanguagePref {
  try {
    return localStorage.getItem(STORE) || "system";
  } catch {
    return "system";
  }
}

/** Shows `pref` now and remembers it for the next start's first screen. */
export function applyLanguage(pref: string | null | undefined) {
  const p = pref || "system";
  setLanguage(p);
  try {
    localStorage.setItem(STORE, p);
  } catch {
    // private mode: only this start
  }
}

export const userLocalesKey = ["app_locales"] as const;

export interface UserLocales {
  dir: string;
  tags: string[];
  errors: { file: string; message: string }[];
}

/** Reads the data folder's translation files and puts them in use (a bad file is named, not used). */
export async function loadUserLocales(): Promise<UserLocales> {
  const got = await api.appLocales();
  const errors = [...got.errors];
  const files: { tag: string; file: unknown }[] = [];
  for (const f of got.files) {
    const bad: ParseError[] = [];
    const value = parse(f.text, bad, { allowTrailingComma: true });
    if (bad.length || value === null || typeof value !== "object" || Array.isArray(value)) {
      errors.push({ file: `${f.tag}.json`, message: bad.length ? `not JSON (at character ${bad[0].offset})` : "not one { ... } object" });
      continue;
    }
    files.push({ tag: f.tag, file: value });
  }
  setUserLocales(files);
  return { dir: got.dir, tags: files.map((f) => f.tag), errors };
}

/** The data folder's translation files (Settings' reload refetches it). */
export const useUserLocales = () => useQuery({ queryKey: userLocalesKey, queryFn: loadUserLocales, staleTime: Infinity, retry: false });

/** Mounted once under the QueryClientProvider (main.tsx). */
export function LanguageSync() {
  const s = useSettings();
  const loaded = !!s.data;
  const pref = s.data?.language ?? null;
  useEffect(() => {
    if (loaded) applyLanguage(pref);
  }, [loaded, pref]);
  useUserLocales();
  return null;
}
