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
});
