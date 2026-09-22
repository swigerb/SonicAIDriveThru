import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import Settings from "../settings";
import { DummyDataProvider } from "@/context/dummy-data-context";
import { AzureSpeechProvider } from "@/context/azure-speech-context";
import { DEFAULT_VOICE, VOICE_OPTIONS, resolveVoice } from "@/lib/voices";

// Every voice gpt-realtime-2.1 accepts (the service's own list when it rejects
// anything else, probed live 2026-09-22).
const GA_REALTIME_VOICES = ["alloy", "ash", "ballad", "cedar", "coral", "echo", "marin", "sage", "shimmer", "verse"];

function renderSettings(voiceChoice: string) {
    return render(
        <AzureSpeechProvider>
            <DummyDataProvider>
                <Settings
                    isMobile={false}
                    showSessionTokens={false}
                    onShowSessionTokensChange={() => {}}
                    verboseLogging={false}
                    onVerboseLoggingChange={() => {}}
                    logToFile={false}
                    onLogToFileChange={() => {}}
                    voiceChoice={voiceChoice}
                    onVoiceChoiceChange={() => {}}
                />
            </DummyDataProvider>
        </AzureSpeechProvider>
    );
}

describe("Carhop voice picker", () => {
    beforeEach(() => localStorage.clear());

    it("offers every gpt-realtime-2.1 voice and defaults to marin", async () => {
        renderSettings(resolveVoice(localStorage.getItem("voiceChoice")));
        await userEvent.click(screen.getByRole("button", { name: /open settings/i }));

        const picker = (await screen.findByLabelText("Select carhop voice")) as HTMLSelectElement;
        const offered = within(picker)
            .getAllByRole("option")
            .map(o => (o as HTMLOptionElement).value);

        expect([...offered].sort()).toEqual(GA_REALTIME_VOICES);
        expect(new Set(offered).size).toBe(offered.length);
        expect(picker.value).toBe("marin");
    });

    it("marks the OpenAI-recommended voices", async () => {
        renderSettings(DEFAULT_VOICE);
        await userEvent.click(screen.getByRole("button", { name: /open settings/i }));
        const picker = await screen.findByLabelText("Select carhop voice");

        const recommended = within(picker)
            .getAllByRole("option")
            .filter(o => /recommended/i.test(o.textContent ?? ""))
            .map(o => (o as HTMLOptionElement).value);
        expect(recommended.sort()).toEqual(["cedar", "marin"]);
        expect(VOICE_OPTIONS.filter(v => v.recommended).map(v => v.value).sort()).toEqual(["cedar", "marin"]);
    });

    it("keeps a stored valid voice and replaces an unknown one with the default", () => {
        expect(DEFAULT_VOICE).toBe("marin");
        expect(resolveVoice("shimmer")).toBe("shimmer");
        expect(resolveVoice("nova")).toBe("marin");
        expect(resolveVoice(null)).toBe("marin");
        expect(resolveVoice("")).toBe("marin");
    });
});
