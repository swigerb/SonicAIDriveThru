import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { APOLOGY_LANGUAGES, apologyClipUrl, apologyLanguage, playApologyClip } from "../apology";

describe("apology clip lookup", () => {
    it.each([
        ["en", "en"],
        ["en-US", "en"],
        ["es", "es"],
        ["es-MX", "es"],
        ["fr_FR", "fr"],
        ["JA", "ja"],
        ["ja-JP", "ja"],
        ["de", "en"],
        ["", "en"],
        [undefined, "en"],
        [null, "en"]
    ])("%s -> %s", (language, expected) => {
        expect(apologyLanguage(language)).toBe(expected);
        expect(apologyClipUrl(language)).toBe(`/audio/apology-${expected}.wav`);
    });

    it("covers exactly the four UI languages", () => {
        expect([...APOLOGY_LANGUAGES]).toEqual(["en", "es", "fr", "ja"]);
    });
});

describe("playApologyClip", () => {
    class SilentAudio {
        static last: SilentAudio;
        pause = vi.fn();
        play = vi.fn(() => Promise.resolve());
        constructor(public src: string) {
            SilentAudio.last = this;
        }
        addEventListener() {}
        removeEventListener() {}
    }

    beforeEach(() => {
        vi.useFakeTimers();
        vi.stubGlobal("Audio", SilentAudio);
    });

    afterEach(() => {
        vi.useRealTimers();
        vi.unstubAllGlobals();
    });

    it("gives up after the timeout if the clip never reports ending", async () => {
        let done = false;
        void playApologyClip("/audio/apology-en.wav", 3000).then(() => {
            done = true;
        });
        await vi.advanceTimersByTimeAsync(2999);
        expect(done).toBe(false);
        await vi.advanceTimersByTimeAsync(1);
        expect(done).toBe(true);
        expect(SilentAudio.last.pause).toHaveBeenCalled();
    });
});
