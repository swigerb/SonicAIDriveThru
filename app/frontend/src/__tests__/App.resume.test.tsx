import { act, fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import RootApp from "../App";

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

const TOTS = { item: "Tots", size: "Large", quantity: 1, price: 2.99, display: "Large Tots" };
const LIMEADE = { item: "Cherry Limeade", size: "Medium", quantity: 1, price: 2.49, display: "Medium Cherry Limeade" };
const orderOf = (...items: (typeof TOTS)[]) => {
    const total = items.reduce((s, i) => s + i.price * i.quantity, 0);
    return { items, total, tax: total * 0.08, finalTotal: total * 1.08 };
};
const resumedMsg = (order = orderOf(TOTS, LIMEADE)) => ({
    type: "extension.session_resumed" as const,
    order_summary: order,
    session_token: "SESSION-1",
    round_trip_index: 3,
    round_trip_token: "RT-3",
    resume_id: "RID-NEW"
});
const transportDrop = { code: 1011, reason: "", idle: false, kind: "transport", resuming: true };

const tapMic = async () => {
    await act(async () => {
        fireEvent.click(screen.getByLabelText(/app\.(start|stop)Recording/));
    });
};

async function startConversationWithTots() {
    render(<RootApp />);
    await tapMic();
    act(() => rt.params.onReceivedExtensionMiddleTierToolResponse({ tool_name: "update_order", tool_result: JSON.stringify(orderOf(TOTS)), previous_item_id: "x" }));
    expect(screen.getByText("Large Tots")).toBeInTheDocument();
}

beforeEach(() => {
    vi.clearAllMocks();
    Element.prototype.scrollIntoView = vi.fn();
    rec.start.mockImplementation(async () => true);
    rt.api.isConnected = true;
});

describe("order resume in the app", () => {
    it("a mid-conversation drop pauses the mic and says it is reconnecting", async () => {
        await startConversationWithTots();
        await act(async () => rt.params.onConnectionLost(transportDrop));
        expect(rec.stop).toHaveBeenCalled();
        expect(screen.getByText("status.reconnecting")).toBeInTheDocument();
        expect(screen.getByText("Large Tots")).toBeInTheDocument();
    });

    it("session_resumed restores the ticket, restarts the mic at once and resets nothing", async () => {
        await startConversationWithTots();
        await act(async () => rt.params.onConnectionLost(transportDrop));
        rec.start.mockClear();
        rt.api.startSession.mockClear();

        await act(async () => rt.params.onReceivedSessionResumed(resumedMsg()));

        expect(screen.getByText("Large Tots")).toBeInTheDocument();
        expect(screen.getByText("Medium Cherry Limeade")).toBeInTheDocument();
        expect(rec.start).toHaveBeenCalledTimes(1);
        expect(rt.api.startSession).toHaveBeenCalledTimes(1);
        expect(rt.api.endSession).not.toHaveBeenCalled();
        expect(screen.getByText("status.resumed")).toBeInTheDocument();
        expect(screen.getByLabelText("app.stopRecording")).toBeInTheDocument();
    });

    it("falls back to tap-to-continue when the browser won't restart the mic", async () => {
        await startConversationWithTots();
        await act(async () => rt.params.onConnectionLost(transportDrop));
        rec.start.mockImplementation(async () => false);

        await act(async () => rt.params.onReceivedSessionResumed(resumedMsg()));

        expect(screen.getByText("status.resumedTapToContinue")).toBeInTheDocument();
        expect(screen.getByLabelText("app.startRecording")).toBeInTheDocument();
        expect(screen.getByText("Medium Cherry Limeade")).toBeInTheDocument();

        rec.start.mockImplementation(async () => true);
        rec.start.mockClear();
        await tapMic();
        // Resumed session: no greeting will come, so the mic starts without the 3.5 s wait.
        expect(rec.start).toHaveBeenCalledTimes(1);
        expect(screen.getByText("Medium Cherry Limeade")).toBeInTheDocument();
    });

    it("getUserMedia refusing after a resume also falls back to tap-to-continue", async () => {
        await startConversationWithTots();
        await act(async () => rt.params.onConnectionLost(transportDrop));
        rec.start.mockImplementation(async () => {
            throw new DOMException("denied", "NotAllowedError");
        });
        await act(async () => rt.params.onReceivedSessionResumed(resumedMsg()));
        expect(screen.getByText("status.resumedTapToContinue")).toBeInTheDocument();
    });

    it("a resume while the guest was not talking restores the ticket without touching the mic", async () => {
        await startConversationWithTots();
        await tapMic(); // guest stops the conversation
        rec.start.mockClear();
        await act(async () => rt.params.onConnectionLost(transportDrop));
        await act(async () => rt.params.onReceivedSessionResumed(resumedMsg()));
        expect(rec.start).not.toHaveBeenCalled();
        expect(screen.getByText("Medium Cherry Limeade")).toBeInTheDocument();
        expect(screen.getByText("status.resumedTapToContinue")).toBeInTheDocument();
    });

    it("resume_rejected clears the ticket and asks for a fresh start", async () => {
        await startConversationWithTots();
        await act(async () => rt.params.onConnectionLost(transportDrop));
        rec.start.mockClear();
        await act(async () => rt.params.onReceivedResumeRejected({ type: "extension.resume_rejected", reason: "expired" }));
        expect(screen.queryByText("Large Tots")).not.toBeInTheDocument();
        expect(screen.getByText("status.resumeRejected")).toBeInTheDocument();
        expect(rec.start).not.toHaveBeenCalled();
    });

    it("idle close: no resume, ticket reset on the next tap", async () => {
        await startConversationWithTots();
        rt.api.isConnected = false;
        await act(async () => rt.params.onConnectionLost({ code: 4000, reason: "idle_timeout", idle: true, kind: "idle", resuming: false }));
        expect(screen.getByText("status.sessionEndedIdle")).toBeInTheDocument();
        await tapMic();
        expect(rt.api.reconnect).toHaveBeenCalled();
        expect(screen.queryByText("Large Tots")).not.toBeInTheDocument();
    });

    it("superseded: tells the guest the order moved to another window", async () => {
        await startConversationWithTots();
        await act(async () => rt.params.onConnectionLost({ code: 4002, reason: "superseded", idle: false, kind: "superseded", resuming: false }));
        expect(screen.getByText("status.superseded")).toBeInTheDocument();
    });

    it("retries exhausted during a resume: connection lost", async () => {
        await startConversationWithTots();
        await act(async () => rt.params.onConnectionLost(transportDrop));
        await act(async () => rt.params.onReconnectGaveUp());
        expect(screen.getByText("status.connectionLost")).toBeInTheDocument();
    });

    it("start a new order: end_session and a clean ticket", async () => {
        await startConversationWithTots();
        await act(async () => {
            fireEvent.click(screen.getByText("app.newOrder"));
        });
        expect(rt.api.endSession).toHaveBeenCalledTimes(1);
        expect(screen.queryByText("Large Tots")).not.toBeInTheDocument();
        await act(async () => rt.params.onConnectionLost({ code: 1000, reason: "session_ended", idle: false, kind: "ended", resuming: false }));
        expect(screen.queryByText("status.connectionLost")).not.toBeInTheDocument();
    });

    it("a tap made before the old session finishes closing carries on into the fresh one", async () => {
        await startConversationWithTots();
        await tapMic(); // stop
        await act(async () => {
            fireEvent.click(screen.getByText("app.newOrder"));
        });
        await tapMic(); // start the next order at once
        rec.stop.mockClear();
        await act(async () => rt.params.onConnectionLost({ code: 1000, reason: "session_ended", idle: false, kind: "ended", resuming: false }));
        expect(rec.stop).not.toHaveBeenCalled();
        expect(screen.getByLabelText("app.stopRecording")).toBeInTheDocument();
        expect(screen.queryByText("status.connectionLost")).not.toBeInTheDocument();
    });

    it("a tap on a resumed session never waits for (or restarts the mic after) a greeting", async () => {
        await startConversationWithTots();
        await act(async () => rt.params.onConnectionLost(transportDrop));
        rec.start.mockImplementation(async () => false);
        await act(async () => rt.params.onReceivedSessionResumed(resumedMsg()));
        rec.start.mockImplementation(async () => true);
        rec.start.mockClear();
        await tapMic();
        // The carhop's 30 s nudge finishing must not start a second capture.
        await act(async () =>
            rt.params.onReceivedResponseDone({ response: { output: [{ content: [{ transcript: "Anything else?" }] }] } })
        );
        expect(rec.start).toHaveBeenCalledTimes(1);
    });
});
