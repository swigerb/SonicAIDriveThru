import { describe, expect, it } from "vitest";

// Guard against wording left over from the templates this app grew from
// (VoiceRAG's "Talk to your data" / "Azure AI Search + Azure OpenAI", Contoso
// sample names, the previous brand). Every locale must carry Sonic wording and
// the same keys as English.

type Tree = { [key: string]: string | Tree };

const localeModules = import.meta.glob<Tree>("../*/translation.json", { eager: true, import: "default" });
const locales: Record<string, Tree> = Object.fromEntries(
    Object.entries(localeModules).map(([path, tree]) => [path.split("/").slice(-2)[0], tree])
);

// User-visible source: components, the app shell and the page title.
const sources: Record<string, string> = {
    ...import.meta.glob<string>(["../../**/*.{ts,tsx}", "!../../**/__tests__/**", "!../../test/**"], {
        eager: true,
        query: "?raw",
        import: "default"
    }),
    ...import.meta.glob<string>("../../../index.html", { eager: true, query: "?raw", import: "default" })
};

const TEMPLATE_LEFTOVERS: RegExp[] = [
    /contoso/i,
    /mercer/i,
    /voice\s*rag/i,
    /talk to your data/i,
    /habla con tus datos/i,
    /parlez [àa] vos donn[ée]es/i,
    /データと話す/,
    /azure ai search \+ azure openai/i,
    /\bdunkin/i,
    /coffee[-\s]?chat/i
];

function flatten(tree: Tree, prefix = ""): Record<string, string> {
    return Object.entries(tree).reduce<Record<string, string>>((acc, [key, value]) => {
        const path = prefix ? `${prefix}.${key}` : key;
        return typeof value === "string" ? { ...acc, [path]: value } : { ...acc, ...flatten(value, path) };
    }, {});
}

function leftoversIn(values: Record<string, string>): string[] {
    return Object.entries(values).flatMap(([key, value]) =>
        TEMPLATE_LEFTOVERS.filter(pattern => pattern.test(value)).map(pattern => `${key}: ${JSON.stringify(value)} matches ${pattern}`)
    );
}

describe("locale files", () => {
    const languages = Object.keys(locales).sort();

    it("covers every UI language", () => {
        expect(languages).toEqual(["en", "es", "fr", "ja"]);
    });

    it.each(languages)("%s has no template leftovers", lang => {
        expect(leftoversIn(flatten(locales[lang]))).toEqual([]);
    });

    it.each(languages)("%s has exactly the English keys", lang => {
        expect(Object.keys(flatten(locales[lang])).sort()).toEqual(Object.keys(flatten(locales.en)).sort());
    });

    it.each(languages)("%s has no empty strings", lang => {
        expect(Object.entries(flatten(locales[lang])).filter(([, value]) => !value.trim())).toEqual([]);
    });

    it.each(languages)("%s names Sonic in the app title", lang => {
        expect(flatten(locales[lang])["app.title"]).toMatch(/Sonic/);
    });

    it.each(languages.filter(lang => lang !== "en"))("%s credits the same Azure services as English", lang => {
        const footer = flatten(locales[lang])["app.footer"];
        for (const service of ["Azure AI", "Azure OpenAI", "Azure Speech"]) {
            expect(footer).toContain(service);
        }
    });
});

describe("user-visible source", () => {
    it("scans the app shell and components", () => {
        const files = Object.keys(sources);
        expect(files.some(file => file.endsWith("/App.tsx"))).toBe(true);
        expect(files.some(file => file.endsWith("/index.html"))).toBe(true);
        expect(files.some(file => file.includes("/components/"))).toBe(true);
        expect(files.some(file => file.includes("__tests__"))).toBe(false);
    });

    it("has no template leftovers", () => {
        const hits = Object.entries(sources).flatMap(([file, text]) =>
            text
                .split("\n")
                .map((line, index) => ({ line, index }))
                .filter(({ line }) => TEMPLATE_LEFTOVERS.some(pattern => pattern.test(line)))
                .map(({ line, index }) => `${file}:${index + 1}: ${line.trim()}`)
        );
        expect(hits).toEqual([]);
    });
});

describe("leftoversIn", () => {
    it("flags the old template wording in any language", () => {
        expect(
            leftoversIn({
                a: "Welcome to Contoso Coffee",
                b: "Habla con tus datos",
                c: "Créée avec Azure AI Search + Azure OpenAI",
                d: "データと話す",
                e: "Sonic Voice Ordering"
            }).map(hit => hit.split(":")[0])
        ).toEqual(["a", "b", "c", "d"]);
    });
});
