import { afterEach, describe, expect, it, vi } from "vitest";

import { Recorder } from "../recorder";

type Ctx = { state: AudioContextState; resume: ReturnType<typeof vi.fn> };
let ctx: Ctx;

class FakeNode {
    connect = vi.fn();
    disconnect = vi.fn();
    port = { onmessage: null as unknown };
    gain = { value: 1 };
    fftSize = 512;
    getByteTimeDomainData = vi.fn();
}

function installAudio(initial: AudioContextState, resumes: boolean) {
    vi.stubGlobal(
        "AudioContext",
        vi.fn(function (this: any) {
            ctx = this;
            this.state = initial;
            this.resume = vi.fn(() => (resumes ? ((this.state = "running"), Promise.resolve()) : new Promise(() => {})));
            this.audioWorklet = { addModule: vi.fn(async () => {}) };
            this.createMediaStreamSource = () => new FakeNode();
            this.createGain = () => new FakeNode();
            this.createAnalyser = () => new FakeNode();
        })
    );
    vi.stubGlobal("AudioWorkletNode", FakeNode);
}

const fakeStream = () => {
    const track = { stop: vi.fn() };
    return { stream: { getTracks: () => [track] } as unknown as MediaStream, track };
};

afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

describe("Recorder.start", () => {
    it("resolves true once capture runs", async () => {
        installAudio("running", true);
        const { stream, track } = fakeStream();
        const rec = new Recorder(() => {});
        await expect(rec.start(stream)).resolves.toBe(true);
        expect(track.stop).not.toHaveBeenCalled();
        await rec.stop();
    });

    it("resumes a suspended context when the browser allows it", async () => {
        installAudio("suspended", true);
        const rec = new Recorder(() => {});
        await expect(rec.start(fakeStream().stream)).resolves.toBe(true);
        expect(ctx.resume).toHaveBeenCalled();
        await rec.stop();
    });

    it("gives up (false, mic released) when resume() never settles without a gesture", async () => {
        vi.useFakeTimers();
        installAudio("suspended", false);
        const { stream, track } = fakeStream();
        const rec = new Recorder(() => {});
        const started = rec.start(stream);
        await vi.advanceTimersByTimeAsync(1500);
        await expect(started).resolves.toBe(false);
        expect(ctx.state).toBe("suspended");
        expect(track.stop).toHaveBeenCalled(); // mic released so the browser indicator goes off
    });
});
