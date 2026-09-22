import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import useRealTime, { WS_CLOSE_IDLE_TIMEOUT } from "../useRealtime";

const ws = vi.hoisted(() => ({
    calls: [] as Array<{ url: string | null; options: any; connect: boolean }>,
    readyState: 1,
    send: vi.fn()
}));

vi.mock("react-use-websocket", () => ({
    default: (url: string | null, options: any, connect: boolean) => {
        ws.calls.push({ url, options, connect });
        return { sendJsonMessage: ws.send, readyState: ws.readyState };
    },
    ReadyState: { UNINSTANTIATED: -1, CONNECTING: 0, OPEN: 1, CLOSING: 2, CLOSED: 3 }
}));

const last = () => ws.calls[ws.calls.length - 1];
const closeEvent = (code: number, reason = "") => ({ code, reason, wasClean: true }) as CloseEvent;

let tokenCounter = 0;

beforeEach(() => {
    ws.calls = [];
    ws.readyState = 1;
    ws.send.mockReset();
    tokenCounter = 0;
    vi.stubGlobal(
        "fetch",
        vi.fn(async () => ({ ok: true, json: async () => ({ token: `tok${++tokenCounter}` }) }))
    );
});

async function renderConnected(onConnectionLost = vi.fn()) {
    const hook = renderHook(() => useRealTime({ enableInputAudioTranscription: true, onConnectionLost }));
    await waitFor(() => expect(last().url).toBe("/realtime?token=tok1"));
    return { ...hook, onConnectionLost };
}

describe("useRealTime connection lifecycle", () => {
    it("does not open a socket until the session token fetch settles", async () => {
        await renderConnected();
        expect(ws.calls[0].url).toBeNull();
        expect(ws.calls.filter(c => c.url !== null).every(c => c.url === "/realtime?token=tok1")).toBe(true);
    });

    it("stays disconnected after the server's idle close instead of auto-reconnecting", async () => {
        const { onConnectionLost } = await renderConnected();
        expect(last().options.shouldReconnect(closeEvent(WS_CLOSE_IDLE_TIMEOUT, "idle_timeout"))).toBe(false);

        act(() => last().options.onClose(closeEvent(WS_CLOSE_IDLE_TIMEOUT, "idle_timeout")));

        expect(last().connect).toBe(false);
        expect(onConnectionLost).toHaveBeenCalledWith({ code: 4000, reason: "idle_timeout", idle: true });
    });

    it("reconnects with a fresh token only when the guest asks to", async () => {
        const { result } = await renderConnected();
        act(() => last().options.onClose(closeEvent(WS_CLOSE_IDLE_TIMEOUT, "idle_timeout")));
        ws.readyState = 3;

        await act(async () => {
            await result.current.reconnect();
        });

        expect(last().connect).toBe(true);
        expect(last().url).toBe("/realtime?token=tok2");
    });

    it("treats other closes as connection loss and keeps background reconnect", async () => {
        const { onConnectionLost } = await renderConnected();
        expect(last().options.shouldReconnect(closeEvent(1006))).toBe(true);
        expect(last().options.shouldReconnect(closeEvent(1002))).toBe(true);

        act(() => last().options.onClose(closeEvent(1006)));

        expect(last().connect).toBe(true);
        expect(onConnectionLost).toHaveBeenCalledWith({ code: 1006, reason: "", idle: false });
    });

    it("stops connecting once retries are exhausted so a tap can restart it", async () => {
        await renderConnected();
        act(() => last().options.onReconnectStop(10));
        expect(last().connect).toBe(false);
    });

    it("never queues realtime audio or cancels for a future socket", async () => {
        const { result } = await renderConnected();
        result.current.addUserAudio("AAAA");
        result.current.inputAudioBufferClear();
        result.current.cancelResponse();
        result.current.startSession();

        const byType = (t: string) => ws.send.mock.calls.find(([msg]) => msg.type === t)!;
        expect(byType("input_audio_buffer.append")[1]).toBe(false);
        expect(byType("input_audio_buffer.clear")[1]).toBe(false);
        expect(byType("response.cancel")[1]).toBe(false);
        // session.update is the one message that must survive into the next socket.
        expect(byType("session.update")[1]).not.toBe(false);
    });
});
