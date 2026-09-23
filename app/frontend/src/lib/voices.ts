export interface VoiceOption {
    value: string;
    label: string;
    recommended?: boolean;
}

// Every built-in voice gpt-realtime-2.1 accepts. The service itself lists
// exactly these ten when it rejects anything else (fable/onyx/nova included),
// probed live 2026-09-22. OpenAI recommends marin and cedar for best quality.
export const VOICE_OPTIONS: readonly VoiceOption[] = [
    { value: "marin", label: "Marin — Warm & Natural (recommended)", recommended: true },
    { value: "cedar", label: "Cedar — Gentle & Natural (recommended)", recommended: true },
    { value: "shimmer", label: "Shimmer — Cheerful & Bright" },
    { value: "sage", label: "Sage — Calm & Soothing" },
    { value: "coral", label: "Coral — Warm & Confident" },
    { value: "ballad", label: "Ballad — Caring & Melodic" },
    { value: "ash", label: "Ash — Friendly & Upbeat" },
    { value: "verse", label: "Verse — Natural & Adaptable" },
    { value: "alloy", label: "Alloy — Neutral & Crisp" },
    { value: "echo", label: "Echo — Deep & Resonant" }
];

// Keep in sync with model.default_voice in app/backend/config.yaml.
export const DEFAULT_VOICE = "marin";

export function resolveVoice(stored: string | null | undefined): string {
    return stored && VOICE_OPTIONS.some(v => v.value === stored) ? stored : DEFAULT_VOICE;
}
