import { render, screen } from "@testing-library/react";
import StatusMessage from "../status-message";

describe("StatusMessage", () => {
    it("renders the idle helper when recording is disabled", () => {
        render(<StatusMessage isRecording={false} />);
        expect(screen.getByText("status.notRecordingMessage")).toBeInTheDocument();
    });

    it("renders the live equalizer label while recording", () => {
        const { container } = render(<StatusMessage isRecording />);
        expect(screen.getByText("status.conversationInProgress")).toBeInTheDocument();
        expect(container.querySelector(".listening-equalizer")).not.toBeNull();
    });

    it("tells the guest the session ended after inactivity", () => {
        render(<StatusMessage isRecording={false} notice="idle" />);
        expect(screen.getByText("status.sessionEndedIdle")).toBeInTheDocument();
    });

    it("tells the guest the connection dropped", () => {
        render(<StatusMessage isRecording={false} notice="lost" />);
        expect(screen.getByText("status.connectionLost")).toBeInTheDocument();
    });

    it.each([
        ["reconnecting", "status.reconnecting"],
        ["resumed", "status.resumed"],
        ["tapToResume", "status.resumedTapToContinue"],
        ["resumeRejected", "status.resumeRejected"],
        ["superseded", "status.superseded"]
    ] as const)("renders the %s notice when the mic is off", (notice, key) => {
        render(<StatusMessage isRecording={false} notice={notice} />);
        expect(screen.getByText(key)).toBeInTheDocument();
    });

    it("shows the reconnected line while listening after a resume", () => {
        const { container } = render(<StatusMessage isRecording notice="resumed" />);
        expect(screen.getByText("status.resumed")).toBeInTheDocument();
        expect(screen.queryByText("status.conversationInProgress")).not.toBeInTheDocument();
        expect(container.querySelector(".listening-equalizer")).not.toBeNull();
    });

    it.each([
        ["rateLimited", "status.rateLimited"],
        ["rateLimitedFinal", "status.rateLimitedFinal"]
    ] as const)("shows the %s rate-limit notice while listening", (notice, key) => {
        const { container } = render(<StatusMessage isRecording notice={notice} />);
        expect(screen.getByText(key)).toBeInTheDocument();
        expect(screen.queryByText("status.conversationInProgress")).not.toBeInTheDocument();
        expect(container.querySelector(".listening-equalizer")).not.toBeNull();
    });

    it("other notices don't replace the listening label", () => {
        render(<StatusMessage isRecording notice="reconnecting" />);
        expect(screen.getByText("status.conversationInProgress")).toBeInTheDocument();
    });
});
