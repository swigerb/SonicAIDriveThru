import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import useRealTime, { RESUME_STORAGE_KEY, WS_CLOSE_IDLE_TIMEOUT, WS_CLOSE_SUPERSEDED } from "../useRealtime";

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
const sent = () => ws.send.mock.calls.map(([msg]) => msg);
const sentTypes = () => sent().map(msg => msg.type);
const serverSays = (message: object) => last().options.onMessage({ data: JSON.stringify(message) } as MessageEvent);
const storedId = () => sessionStorage.getItem(RESUME_STORAGE_KEY);

let tokenCounter = 0;

beforeEach(() => {
    ws.calls = [];
    ws.readyState = 1;
    ws.send.mockReset();
    sessionStorage.clear();
    tokenCounter = 0;
    vi.stubGlobal(
        "fetch",
        vi.fn(async () => ({ ok: true, json: async () => ({ token: `tok${++tokenCounter}` }) }))
    );
});

async function renderConnected(extra: Record<string, any> = {}) {
    const onConnectionLost = extra.onConnectionLost ?? vi.fn();
    const hook = renderHook(() => useRealTime({ enableInputAudioTranscription: true, ...extra, onConnectionLost }));
    await waitFor(() => expect(last().url).toBe("/realtime?token=tok1"));
    return { ...hook, onConnectionLost };
}

const open = () => act(() => last().options.onOpen(new Event("open")));

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
        expect(onConnectionLost).toHaveBeenCalledWith({ code: 4000, reason: "idle_timeout", idle: true, kind: "idle", resuming: false });
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
        expect(onConnectionLost).toHaveBeenCalledWith({ code: 1006, reason: "", idle: false, kind: "transport", resuming: false });
    });

    it("stops connecting once retries are exhausted so a tap can restart it", async () => {
        const onReconnectGaveUp = vi.fn();
        await renderConnected({ onReconnectGaveUp });
        act(() => last().options.onReconnectStop(10));
        expect(last().connect).toBe(false);
        expect(onReconnectGaveUp).toHaveBeenCalledTimes(1);
    });

    it("never queues realtime audio or cancels for a future socket", async () => {
        const { result } = await renderConnected();
        result.current.addUserAudio("AAAA");
        result.current.inputAudioBufferClear();
        result.current.cancelResponse();
        result.current.startSession();
        expect(ws.send).not.toHaveBeenCalled();

        open();
        // session.update is the one message that must survive into the next socket.
        expect(sentTypes()).toEqual(["session.update"]);
    });

    it("keeps the 4001 / expired token-refresh path", async () => {
        await renderConnected();
        expect(last().options.shouldReconnect(closeEvent(4001, "token expired"))).toBe(true);
        act(() => last().options.onClose(closeEvent(4001, "token expired")));
        await waitFor(() => expect(last().url).toBe("/realtime?token=tok2"));
        expect(last().connect).toBe(true);
    });
});

describe("useRealTime order resume", () => {
    it("sends extension.resume as the literal first frame, ahead of anything queued", async () => {
        sessionStorage.setItem(RESUME_STORAGE_KEY, "RID-1");
        const { result } = await renderConnected();
        result.current.startSession();
        result.current.sendVoiceChoice("cedar");
        expect(ws.send).not.toHaveBeenCalled();

        open();

        expect(sent()[0]).toEqual({ type: "extension.resume", resume_id: "RID-1" });
        expect(sentTypes()).toEqual(["extension.resume", "session.update", "extension.set_voice"]);
    });

    it("never hands a frame to react-use-websocket's own queue", async () => {
        sessionStorage.setItem(RESUME_STORAGE_KEY, "RID-1");
        const { result } = await renderConnected();
        result.current.startSession();
        result.current.sendVerboseLogging(true);
        open();
        result.current.sendLogToFile(true);
        result.current.addUserAudio("AAAA");
        act(() => serverSays({ type: "response.created" }));

        expect(ws.send.mock.calls.length).toBe(6);
        expect(ws.send.mock.calls.every(([, keep]) => keep === false)).toBe(true);
    });

    it("sends no resume frame when the tab holds no id", async () => {
        const { result } = await renderConnected();
        result.current.startSession();
        open();
        expect(sentTypes()).toEqual(["session.update"]);
    });

    it("stores resumeId from session_metadata and presents it on the next open", async () => {
        const onReceivedSessionMetadata = vi.fn();
        await renderConnected({ onReceivedSessionMetadata });
        open();
        act(() => serverSays({ type: "extension.session_metadata", sessionToken: "S", roundTripIndex: 0, roundTripToken: "T", resumeId: "RID-A" }));
        expect(storedId()).toBe("RID-A");
        expect(onReceivedSessionMetadata).toHaveBeenCalledTimes(1);

        act(() => last().options.onClose(closeEvent(1011)));
        ws.send.mockReset();
        open();
        expect(sent()[0]).toEqual({ type: "extension.resume", resume_id: "RID-A" });
    });

    it("session_resumed stores the rotated id and hands the order to the app", async () => {
        sessionStorage.setItem(RESUME_STORAGE_KEY, "RID-OLD");
        const onReceivedSessionResumed = vi.fn();
        await renderConnected({ onReceivedSessionResumed });
        open();
        const resumed = {
            type: "extension.session_resumed",
            order_summary: { items: [{ item: "Tots", size: "Large", quantity: 1, price: 2.99, display: "Large Tots" }], total: 2.99, tax: 0.24, finalTotal: 3.23 },
            session_token: "S",
            round_trip_index: 2,
            round_trip_token: "T",
            resume_id: "RID-NEW"
        };
        act(() => serverSays(resumed));
        expect(storedId()).toBe("RID-NEW");
        expect(onReceivedSessionResumed).toHaveBeenCalledWith(resumed);
    });

    it("resume_rejected clears the stored id; the following metadata stores the new one", async () => {
        sessionStorage.setItem(RESUME_STORAGE_KEY, "RID-OLD");
        const onReceivedResumeRejected = vi.fn();
        await renderConnected({ onReceivedResumeRejected });
        open();
        act(() => serverSays({ type: "extension.resume_rejected", reason: "expired" }));
        expect(storedId()).toBeNull();
        expect(onReceivedResumeRejected).toHaveBeenCalledWith({ type: "extension.resume_rejected", reason: "expired" });

        act(() => serverSays({ type: "extension.session_metadata", sessionToken: "S2", roundTripIndex: 0, roundTripToken: "T", resumeId: "RID-FRESH" }));
        expect(storedId()).toBe("RID-FRESH");
    });

    it.each([1001, 1002, 1006, 1011])("transport close %i reconnects and keeps the id to resume", async code => {
        sessionStorage.setItem(RESUME_STORAGE_KEY, "RID-1");
        const { onConnectionLost } = await renderConnected();
        open();
        expect(last().options.shouldReconnect(closeEvent(code))).toBe(true);
        act(() => last().options.onClose(closeEvent(code)));
        expect(last().connect).toBe(true);
        expect(storedId()).toBe("RID-1");
        expect(onConnectionLost).toHaveBeenCalledWith(expect.objectContaining({ kind: "transport", resuming: true }));
    });

    it.each([
        ["idle 4000", WS_CLOSE_IDLE_TIMEOUT, "idle_timeout", "idle", null],
        ["superseded 4002", WS_CLOSE_SUPERSEDED, "superseded", "superseded", "RID-1"]
    ])("%s: no reconnect; id afterwards = %s", async (_label, code, reason, kind, idAfter) => {
        sessionStorage.setItem(RESUME_STORAGE_KEY, "RID-1");
        const { onConnectionLost } = await renderConnected();
        open();
        expect(last().options.shouldReconnect(closeEvent(code as number, reason as string))).toBe(false);
        act(() => last().options.onClose(closeEvent(code as number, reason as string)));
        expect(last().connect).toBe(false);
        expect(storedId()).toBe(idAfter);
        expect(onConnectionLost).toHaveBeenCalledWith(expect.objectContaining({ kind, resuming: false }));
    });

    it("a plain 1000 close (not session_ended) still reconnects", async () => {
        await renderConnected();
        expect(last().options.shouldReconnect(closeEvent(1000, ""))).toBe(true);
    });

    it("frames queued for a session that then ends (idle) never reach the next one", async () => {
        const { result } = await renderConnected();
        open();
        act(() => last().options.onClose(closeEvent(1006)));
        result.current.sendVoiceChoice("cedar"); // queued while reconnecting
        act(() => last().options.onClose(closeEvent(WS_CLOSE_IDLE_TIMEOUT, "idle_timeout")));
        ws.readyState = 3;
        await act(async () => {
            await result.current.reconnect();
        });
        ws.send.mockReset();
        open();
        expect(ws.send).not.toHaveBeenCalled();
    });

    it("session_ended 1000: no background reconnect, id cleared, a fresh socket opens with a new token", async () => {
        sessionStorage.setItem(RESUME_STORAGE_KEY, "RID-1");
        const { onConnectionLost } = await renderConnected();
        open();
        expect(last().options.shouldReconnect(closeEvent(1000, "session_ended"))).toBe(false);
        act(() => last().options.onClose(closeEvent(1000, "session_ended")));
        expect(storedId()).toBeNull();
        expect(onConnectionLost).toHaveBeenCalledWith(expect.objectContaining({ kind: "ended", resuming: false }));
        await waitFor(() => expect(last().url).toBe("/realtime?token=tok2"));
        expect(last().connect).toBe(true);
    });

    it("a tap right after endSession is held for the fresh session, never sent to the ending one", async () => {
        sessionStorage.setItem(RESUME_STORAGE_KEY, "RID-1");
        const { result } = await renderConnected();
        open();
        ws.send.mockReset();
        result.current.endSession();
        result.current.startSession();
        result.current.addUserAudio("AAAA");
        expect(sentTypes()).toEqual(["extension.end_session"]);

        act(() => last().options.onClose(closeEvent(1000, "session_ended")));
        await waitFor(() => expect(last().url).toBe("/realtime?token=tok2"));
        ws.send.mockReset();
        open();
        expect(sentTypes()).toEqual(["session.update"]);
    });

    it("endSession on a socket that is not open sends nothing but still forgets the id", async () => {
        sessionStorage.setItem(RESUME_STORAGE_KEY, "RID-1");
        const { result } = await renderConnected();
        result.current.endSession();
        expect(ws.send).not.toHaveBeenCalled();
        expect(storedId()).toBeNull();
    });

    it("endSession sends extension.end_session and forgets the id", async () => {
        sessionStorage.setItem(RESUME_STORAGE_KEY, "RID-1");
        const { result } = await renderConnected();
        open();
        ws.send.mockReset();
        result.current.endSession();
        expect(sent()).toEqual([{ type: "extension.end_session" }]);
        expect(storedId()).toBeNull();

        act(() => last().options.onClose(closeEvent(1000, "session_ended")));
        ws.send.mockReset();
        open();
        expect(sentTypes()).not.toContain("extension.resume");
    });
});
