import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import RootApp from "../App";

// The UI language decides which apology clip plays.
const lang = vi.hoisted(() => ({ current: "en" }));
vi.mock("react-i18next", () => ({
    useTranslation: () => ({
        t: (key: string) => key,
        i18n: {
            get language() {
                return lang.current;
            },
            changeLanguage: () => Promise.resolve()
        }
    }),
    initReactI18next: { type: "3rdParty", init: () => undefined }
}));

const rt = vi.hoisted(() => ({
    params: null as any,
    api: {
        startSession: vi.fn(),
        addUserAudio: vi.fn(),
        inputAudioBufferClear: vi.fn(),
        cancelResponse: vi.fn(),
        sendVerboseLogging: vi.fn(),
        sendLogToFile: vi.fn(),
        sendVoiceChoice: vi.fn(),
        endSession: vi.fn(),
        reconnect: vi.fn(async () => {}),
        isConnected: true
    }
}));
const rec = vi.hoisted(() => ({ start: vi.fn(async () => true), stop: vi.fn(async () => {}), mute: vi.fn(), unmute: vi.fn() }));
const player = vi.hoisted(() => ({ reset: vi.fn(async () => {}), play: vi.fn(), stop: vi.fn(), waitForDrain: vi.fn(async () => true) }));

vi.mock("@/hooks/useRealtime", () => ({
    default: (params: any) => {
        rt.params = params;
        return rt.api;
    }
}));
vi.mock("darkreader", () => ({ enable: vi.fn(), disable: vi.fn(), auto: vi.fn(), setFetchMethod: vi.fn() }));
vi.mock("@/hooks/useAudioRecorder", () => ({ default: () => rec }));
vi.mock("@/hooks/useAudioPlayer", () => ({ default: () => player }));

class FakeAudio {
    static instances: FakeAudio[] = [];
    static playResult: () => Promise<void> = () => Promise.resolve();
    src: string;
    listeners: Record<string, Array<() => void>> = {};
    play = vi.fn(() => FakeAudio.playResult());
    pause = vi.fn();
    constructor(src: string) {
        this.src = src;
        FakeAudio.instances.push(this);
    }
    addEventListener(type: string, fn: () => void) {
        (this.listeners[type] ??= []).push(fn);
    }
    removeEventListener(type: string, fn: () => void) {
        this.listeners[type] = (this.listeners[type] ?? []).filter(f => f !== fn);
    }
    fire(type: string) {
        [...(this.listeners[type] ?? [])].forEach(fn => fn());
    }
}

const RATE_LIMITED = { type: "extension.rate_limited" as const, attempt: 1 };
const FINAL = { type: "extension.rate_limited" as const, attempt: 2, final: true };
const answer = (transcript: string) => ({ type: "response.done", response: { output: [{ content: [{ transcript }] }] } });

const tapMic = async () => {
    await act(async () => {
        fireEvent.click(screen.getByLabelText(/app\.(start|stop)Recording/));
    });
};

async function startConversation() {
    render(<RootApp />);
    await tapMic();
    // The guest's turn: the model's response starts (mic muted), then is rate-limited.
    act(() => rt.params.onReceivedResponseCreated({ type: "response.created" }));
    rec.mute.mockClear();
    rec.unmute.mockClear();
}

const flush = () => act(async () => {
    await Promise.resolve();
    await Promise.resolve();
});

beforeEach(() => {
    vi.clearAllMocks();
    Element.prototype.scrollIntoView = vi.fn();
    rec.start.mockImplementation(async () => true);
    lang.current = "en";
    FakeAudio.instances = [];
    FakeAudio.playResult = () => Promise.resolve();
    vi.stubGlobal("Audio", FakeAudio);
});

afterEach(() => {
    vi.unstubAllGlobals();
});

describe("rate-limit recovery in the app", () => {
    it.each([
        ["en", "/audio/apology-en.wav"],
        ["es", "/audio/apology-es.wav"],
        ["fr-CA", "/audio/apology-fr.wav"],
        ["ja", "/audio/apology-ja.wav"],
        ["de", "/audio/apology-en.wav"]
    ])("attempt 1 plays the apology clip for UI language %s", async (language, url) => {
        lang.current = language;
        await startConversation();
        act(() => rt.params.onReceivedRateLimited(RATE_LIMITED));
        expect(FakeAudio.instances.map(a => a.src)).toEqual([url]);
        expect(FakeAudio.instances[0].play).toHaveBeenCalledTimes(1);
        expect(screen.getByText("status.rateLimited")).toBeInTheDocument();
    });

    it("keeps the mic muted while the clip plays and unmutes it after", async () => {
        await startConversation();
        act(() => rt.params.onReceivedRateLimited(RATE_LIMITED));
        expect(rec.mute).toHaveBeenCalled();
        await flush();
        expect(rec.unmute).not.toHaveBeenCalled();

        await act(async () => FakeAudio.instances[0].fire("ended"));
        await flush();
        expect(rec.unmute).toHaveBeenCalledTimes(1);
    });

    it("leaves the mic muted when the retry's answer starts during the clip", async () => {
        await startConversation();
        act(() => rt.params.onReceivedRateLimited(RATE_LIMITED));
        act(() => rt.params.onReceivedResponseCreated({ type: "response.created" }));
        await act(async () => FakeAudio.instances[0].fire("ended"));
        await flush();
        expect(rec.unmute).not.toHaveBeenCalled();

        act(() => rt.params.onReceivedResponseDone(answer("Sure, one large tots.")));
        expect(rec.unmute).toHaveBeenCalledTimes(1);
        expect(screen.queryByText("status.rateLimited")).not.toBeInTheDocument();
        expect(screen.getByText("status.conversationInProgress")).toBeInTheDocument();
    });

    it("a clip that can't play still gives the mic back", async () => {
        FakeAudio.playResult = () => Promise.reject(new DOMException("blocked", "NotAllowedError"));
        const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
        await startConversation();
        act(() => rt.params.onReceivedRateLimited(RATE_LIMITED));
        await flush();
        await flush();
        expect(rec.unmute).toHaveBeenCalledTimes(1);
        warn.mockRestore();
    });

    it("final: asks the guest to say it again and gives the mic back, with no clip", async () => {
        await startConversation();
        act(() => rt.params.onReceivedRateLimited(FINAL));
        expect(FakeAudio.instances).toEqual([]);
        expect(rec.unmute).toHaveBeenCalledTimes(1);
        expect(screen.getByText("status.rateLimitedFinal")).toBeInTheDocument();

        // The guest speaks again: the notice goes.
        act(() => rt.params.onReceivedInputAudioBufferSpeechStarted({ type: "input_audio_buffer.speech_started" }));
        expect(screen.queryByText("status.rateLimitedFinal")).not.toBeInTheDocument();
    });

    it("final while the clip is still playing leaves the unmute to the clip", async () => {
        await startConversation();
        act(() => rt.params.onReceivedRateLimited(RATE_LIMITED));
        act(() => rt.params.onReceivedRateLimited(FINAL));
        expect(rec.unmute).not.toHaveBeenCalled();
        expect(screen.getByText("status.rateLimitedFinal")).toBeInTheDocument();
        await act(async () => FakeAudio.instances[0].fire("ended"));
        await flush();
        expect(rec.unmute).toHaveBeenCalledTimes(1);
        expect(FakeAudio.instances).toHaveLength(1);
    });

    it("does nothing when no conversation is running", async () => {
        render(<RootApp />);
        act(() => rt.params.onReceivedRateLimited(RATE_LIMITED));
        act(() => rt.params.onReceivedRateLimited(FINAL));
        expect(FakeAudio.instances).toEqual([]);
        expect(rec.mute).not.toHaveBeenCalled();
        expect(rec.unmute).not.toHaveBeenCalled();
        expect(screen.queryByText("status.rateLimited")).not.toBeInTheDocument();
        expect(screen.queryByText("status.rateLimitedFinal")).not.toBeInTheDocument();
    });

    it("stopping the conversation clears the notice", async () => {
        await startConversation();
        act(() => rt.params.onReceivedRateLimited(FINAL));
        await tapMic();
        expect(screen.queryByText("status.rateLimitedFinal")).not.toBeInTheDocument();
        expect(screen.getByText("status.notRecordingMessage")).toBeInTheDocument();
    });
});
