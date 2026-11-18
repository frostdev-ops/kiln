import { afterEach, expect, test } from "bun:test";
import { fill, flatten, formatNumber, lookupOrder, placeholders, pluralBase, rawIn } from "../src/i18n/format";
import { currentLanguage, currentLocale, hasKey, languageName, languages, registerLocale, resolveLanguage, setLanguage, setUserLocales, t } from "../src/i18n";
import en from "../src/i18n/en.json";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { rich } from "../src/i18n";

afterEach(() => {
  setUserLocales([]);
  setLanguage("en");
});

test("nested objects make dotted keys; other values are named", () => {
  const bad: string[] = [];
  expect(flatten({ settings: { title: "Settings", deep: { x: "y" } }, n: 3, list: ["a"] }, "", {}, bad))
    .toEqual({ "settings.title": "Settings", "settings.deep.x": "y" });
  expect(bad).toEqual(["n", "list"]);
});

test("placeholders: positional, named, formats, braces", () => {
  expect(fill("{0} of {1}", [3, 7])).toBe("3 of 7");
  expect(fill("Hello {name}", [{ name: "XHL" }])).toBe("Hello XHL");
  expect(fill("{{literal}} {0}", ["x"])).toBe("{literal} x");
  expect(fill("}}{{", [])).toBe("}{");
  expect(fill("{missing} and {0}", [])).toBe("{missing} and {0}");
  expect(fill("{name}", [{ name: null }])).toBe("");
  expect(fill("{count:N0} files", [{ count: 1234567 }], "en")).toBe("1,234,567 files");
  expect(fill("{count} files", [{ count: 1234567 }], "en")).toBe("1234567 files");
  expect(fill("{0:N0}", [1234567], "de")).toBe("1.234.567");
  expect(fill("{0:Q9}", [12])).toBe("12"); // a bad format: the value without it
  expect(fill("{0:N0}", ["text"])).toBe("text");
  expect(fill("open {", [])).toBe("open {");
  expect(placeholders("{0} of {total:N0} {{x}}")).toEqual(["0", "total"]);
});

test(".NET number formats in a locale", () => {
  expect(formatNumber(1234.5, "N", "en")).toBe("1,234.50");
  expect(formatNumber(1234.5, "N1", "en")).toBe("1,234.5");
  expect(formatNumber(1234.5, "F0", "en")).toBe("1235");
  expect(formatNumber(7, "D3", "en")).toBe("007");
  expect(formatNumber(0.256, "P0", "en")).toBe("26%");
  expect(formatNumber(1234.5, "N0", "zh-CN")).toBe("1,235");
  expect(formatNumber(1, "X", "en")).toBeNull();
});

test("lookup order: exact tag, relatives, main language, English", () => {
  expect(lookupOrder("zh-CN")).toEqual(["zh-cn", "zh", "en"]);
  expect(lookupOrder("zh-Hans-CN")).toEqual(["zh-hans-cn", "zh-cn", "zh", "en"]);
  expect(lookupOrder("zh-HK")).toEqual(["zh-hk", "zh-tw", "zh", "en"]);
  expect(lookupOrder("es-MX")).toEqual(["es-mx", "es-419", "es", "en"]);
  expect(lookupOrder("en")).toEqual(["en"]);
  expect(lookupOrder("")).toEqual(["en"]);
});

test("plurals: .one/.other in the language's own rules; Chinese has only .other", () => {
  const enT = { "x.one": "{count} file", "x.other": "{count} files" };
  expect(rawIn(enT, "x", 1, "en")).toBe("{count} file");
  expect(rawIn(enT, "x", 0, "en")).toBe("{count} files");
  expect(rawIn({ "x.other": "{count} 个文件" }, "x", 1, "zh-CN")).toBe("{count} 个文件");
  expect(pluralBase("a.b.one")).toBe("a.b");
  expect(pluralBase("a.b")).toBe("a.b");
});

test("t: the active language, then its main language, then English; a missing key is the key", () => {
  registerLocale("zh-CN", { languageSettings: { title: "语言" }, test: { only: "仅中文" } });
  registerLocale("zh", { common: { cancel: "取消(zh)" } });
  setLanguage("zh-CN");
  expect(currentLanguage()).toBe("zh-cn");
  expect(currentLocale()).toBe("zh-CN");
  expect(t("languageSettings.title")).toBe("语言");
  expect(t("common.cancel")).toBe("取消(zh)");
  expect(t("common.save")).toBe(en.common.save);
  expect(t("no.such.key")).toBe("no.such.key");
  setLanguage("en");
  expect(t("languageSettings.title")).toBe("Language");
  expect(t("languageSettings.filesRead", { count: 1 })).toBe("1 translation file read");
  expect(t("languageSettings.filesRead", { count: 2 })).toBe("2 translation files read");
  expect(t("languageSettings.filesBad", "zh-CN.json", "not JSON")).toBe("zh-CN.json: not JSON");
  expect(hasKey("languageSettings.filesRead")).toBe(true);
  expect(hasKey("nope")).toBe(false);
});

test("system: the first system language with a file, else English", () => {
  registerLocale("zh-CN", { language: { name: "简体中文" } });
  expect(resolveLanguage("system", ["de-DE", "zh-CN"])).toBe("zh-cn");
  expect(resolveLanguage("system", ["zh-Hans-CN"])).toBe("zh-hans-cn");
  expect(resolveLanguage("system", ["de-DE", "fr"])).toBe("en");
  expect(resolveLanguage("pt-BR", [])).toBe("pt-br");
  expect(languages().map((l) => l.tag)[0]).toBe("en");
  expect(languages().find((l) => l.tag === "zh-CN")?.name).toBe("简体中文");
  expect(languageName("en")).toBe("English");
  expect(languageName("zh-TW")).toBe("繁體中文");
});

test("a translator's file in the data folder wins over the built-in one, and goes when it is gone", () => {
  registerLocale("zh-CN", { languageSettings: { title: "语言" } });
  setLanguage("zh-CN");
  setUserLocales([{ tag: "zh-CN", file: { languageSettings: { title: "语言（测试）" } } }, { tag: "en", file: { common: { save: "nope" } } }]);
  expect(t("languageSettings.title")).toBe("语言（测试）");
  expect(t("common.save")).toBe("Save"); // English stays en.json's
  setUserLocales([]);
  expect(t("languageSettings.title")).toBe("语言");
});

test("rich: elements in a text, in the translation's order", () => {
  registerLocale("zh-CN", { languageSettings: { filesBad: "{1}（{0}）" } });
  setLanguage("zh-CN");
  const node = rich("languageSettings.filesBad", createElement("code", null, "zh-CN.json"), "not JSON");
  expect(renderToStaticMarkup(createElement("p", null, node))).toBe("<p>not JSON（<code>zh-CN.json</code>）</p>");
  expect(rich("languageSettings.filesBad", "a", "b")).toBe("{1}（{0}）".replace("{1}", "b").replace("{0}", "a"));
});
