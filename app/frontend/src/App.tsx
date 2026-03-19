import { useState, useEffect, useRef, useMemo } from "react";
import { Mic, MicOff, Menu, MessageSquare, LogOut, Github } from "lucide-react";
import { useTranslation } from "react-i18next";

import { Card } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Sheet, SheetContent, SheetHeader, SheetTitle, SheetTrigger } from "@/components/ui/sheet";

import StatusMessage from "@/components/ui/status-message";
import MenuPanel from "@/components/ui/menu-panel";
import OrderSummary, { calculateOrderSummary, OrderSummaryProps } from "@/components/ui/order-summary";
import TranscriptPanel from "@/components/ui/transcript-panel";
import Settings from "@/components/ui/settings";
import useRealTime from "@/hooks/useRealtime";
import useAzureSpeech from "@/hooks/useAzureSpeech";
import useAudioRecorder from "@/hooks/useAudioRecorder";
import useAudioPlayer from "@/hooks/useAudioPlayer";

import { ExtensionMiddleTierToolResponse, ExtensionRoundTripToken, ExtensionSessionMetadata } from "./types";

import { ThemeProvider, useTheme } from "./context/theme-context";
import { DummyDataProvider, useDummyDataContext } from "@/context/dummy-data-context";
import { AzureSpeechProvider, useAzureSpeechOnContext } from "@/context/azure-speech-context";
import { AuthProvider, useAuth } from "@/context/auth-context";

import dummyTranscriptsData from "@/data/dummyTranscripts.json";
import dummyOrderData from "@/data/dummyOrder.json";
import azureLogo from "@/assets/azurelogo.svg";
import sonicLogo from "@/assets/sonic-logo.svg";

type HighlightTone = "red" | "blue" | "yellow";

type SessionIdentifiersState = {
    sessionToken: string;
    roundTripIndex: number;
    roundTripToken: string;
};

const heroHighlights: Array<{ title: string; detail: string; tone: HighlightTone }> = [
    {
        title: "Rewards Ready",
        detail: "Voice orders auto-sync with Sonic rewards and deals",
        tone: "red"
    },
    {
        title: "Azure Infusion",
        detail: "Azure OpenAI + Speech keep conversations flowing",
        tone: "blue"
    },
    {
        title: "Live Menu",
        detail: "Azure AI Search keeps Sonic menu items current",
        tone: "yellow"
    }
];

const heroCallouts = [
    { label: "Slush of the Day", value: "Cherry Limeade", accent: "#E40046" },
    { label: "Carhop Pick", value: "SuperSONIC Cheeseburger", accent: "#285780" }
];

function SonicApp() {
    const [isRecording, setIsRecording] = useState(false);
    const [isMobile, setIsMobile] = useState(false);
    const { useAzureSpeechOn } = useAzureSpeechOnContext();
    const { useDummyData } = useDummyDataContext();
    const { theme } = useTheme();
    const { logout, authEnabled } = useAuth();

    const [transcripts, setTranscripts] = useState<Array<{ text: string; isUser: boolean; timestamp: Date }>>([]);
    const dummyTranscripts = useMemo<Array<{ text: string; isUser: boolean; timestamp: Date }>>(
        () =>
            dummyTranscriptsData.map(transcript => ({
                ...transcript,
                timestamp: new Date(transcript.timestamp)
            })),
        []
    );

    const initialOrder: OrderSummaryProps = {
        items: [],
        total: 0,
        tax: 0,
        finalTotal: 0
    };

    const dummyOrder: OrderSummaryProps = calculateOrderSummary(dummyOrderData);

    const [order, setOrder] = useState<OrderSummaryProps>(initialOrder);
    const [sessionIdentifiers, setSessionIdentifiers] = useState<SessionIdentifiersState | null>(null);
    const [showSessionTokens, setShowSessionTokens] = useState<boolean>(() => {
        if (typeof window === "undefined") return true;
        const stored = localStorage.getItem("showSessionTokens");
        return stored === null ? true : stored === "true";
    });

    useEffect(() => {
        localStorage.setItem("showSessionTokens", showSessionTokens.toString());
    }, [showSessionTokens]);

    const handleSessionIdentifiers = (message: ExtensionSessionMetadata | ExtensionRoundTripToken) => {
        setSessionIdentifiers({
            sessionToken: message.sessionToken,
            roundTripIndex: message.roundTripIndex,
            roundTripToken: message.roundTripToken
        });
    };

    const isSessionActiveRef = useRef(false);
    const awaitingGreetingDoneRef = useRef(false);
    const greetingAudioSeenRef = useRef(false);
    const startMicInFlightRef = useRef<Promise<void> | null>(null);

    const realtime = useRealTime({
        enableInputAudioTranscription: true,
        onWebSocketOpen: () => console.log("WebSocket connection opened"),
        onWebSocketClose: () => console.log("WebSocket connection closed"),
        onWebSocketError: event => console.error("WebSocket error:", event),
        onReceivedError: message => console.error("error", message),
        onReceivedResponseAudioDelta: message => {
            if (!isSessionActiveRef.current) return;
            greetingAudioSeenRef.current = true;
            playAudio(message.delta);
        },
        onReceivedInputAudioBufferSpeechStarted: () => {
            stopAudioPlayer();
        },
        onReceivedExtensionMiddleTierToolResponse: ({ tool_name, tool_result }: ExtensionMiddleTierToolResponse) => {
            if (tool_name === "update_order") {
                const orderSummary: OrderSummaryProps = JSON.parse(tool_result);
                setOrder(orderSummary);

                console.log("Order Total:", orderSummary.total);
                console.log("Tax:", orderSummary.tax);
                console.log("Final Total:", orderSummary.finalTotal);
            }
        },
        onReceivedSessionMetadata: handleSessionIdentifiers,
        onReceivedRoundTripToken: handleSessionIdentifiers,
        onReceivedInputAudioTranscriptionCompleted: message => {
            const newTranscriptItem = {
                text: message.transcript,
                isUser: true,
                timestamp: new Date()
            };
            setTranscripts(prev => [...prev, newTranscriptItem]);
        },
        onReceivedResponseDone: message => {
            const transcript = message.response.output.map(output => output.content?.map(content => content.transcript).join(" ")).join(" ");
            if (!transcript) return;

            const newTranscriptItem = {
                text: transcript,
                isUser: false,
                timestamp: new Date()
            };
            setTranscripts(prev => [...prev, newTranscriptItem]);

            if (awaitingGreetingDoneRef.current && isSessionActiveRef.current) {
                awaitingGreetingDoneRef.current = false;

                if (!startMicInFlightRef.current) {
                    startMicInFlightRef.current = (async () => {
                        // If we received audio deltas for the greeting, wait until playback drains.
                        if (greetingAudioSeenRef.current) {
                            await waitForAudioDrain(2500);
                            // Small extra delay for device/driver buffer drain.
                            await new Promise(resolve => setTimeout(resolve, 150));
                        }

                        if (!isSessionActiveRef.current) return;
                        await startAudioRecording();
                    })().finally(() => {
                        startMicInFlightRef.current = null;
                    });
                }
            }
        }
    });

    const azureSpeech = useAzureSpeech({
        onReceivedToolResponse: ({ tool_name, tool_result }: ExtensionMiddleTierToolResponse) => {
            if (tool_name === "update_order") {
                const orderSummary: OrderSummaryProps = JSON.parse(tool_result);
                setOrder(orderSummary);

                console.log("Order Total:", orderSummary.total);
                console.log("Tax:", orderSummary.tax);
                console.log("Final Total:", orderSummary.finalTotal);
            }
        },
        onSpeechToTextTranscriptionCompleted: (message: { transcript: string }) => {
            const newTranscriptItem = {
                text: message.transcript,
                isUser: true,
                timestamp: new Date()
            };
            setTranscripts(prev => [...prev, newTranscriptItem]);
        },
        onModelResponseDone: (message: { response: { output: Array<{ content?: Array<{ transcript: string }> }> } }) => {
            const transcript = message.response.output
                .map(output => output.content?.map(content => content.transcript).join(" "))
                .join(" ");
            if (!transcript) return;

            const newTranscriptItem = {
                text: transcript,
                isUser: false,
                timestamp: new Date()
            };
            setTranscripts(prev => [...prev, newTranscriptItem]);
        },
        onError: (error: unknown) => console.error("Error:", error)
    });

    const { reset: resetAudioPlayer, play: playAudio, stop: stopAudioPlayer, waitForDrain: waitForAudioDrain } =
        useAudioPlayer();
    const { start: startAudioRecording, stop: stopAudioRecording } = useAudioRecorder({
        onAudioRecorded: useAzureSpeechOn ? azureSpeech.addUserAudio : realtime.addUserAudio
    });

    const onToggleListening = async () => {
        if (!isRecording) {
            setSessionIdentifiers(null);

            // Start session and playback immediately, but delay mic capture until the greeting finishes.
            isSessionActiveRef.current = true;
            awaitingGreetingDoneRef.current = !useAzureSpeechOn;
            greetingAudioSeenRef.current = false;

            await resetAudioPlayer();

            if (useAzureSpeechOn) {
                // AzureSpeech mode doesn't play a synthesized greeting audio stream.
                azureSpeech.startSession();
                await startAudioRecording();
            } else {
                realtime.startSession();

                // Safety: if we never receive the greeting completion, start the mic after a short timeout.
                window.setTimeout(() => {
                    if (!isSessionActiveRef.current) return;
                    if (!awaitingGreetingDoneRef.current) return;
                    awaitingGreetingDoneRef.current = false;
                    if (startMicInFlightRef.current) return;
                    startMicInFlightRef.current = startAudioRecording().finally(() => {
                        startMicInFlightRef.current = null;
                    });
                }, 5000);
            }

            setIsRecording(true);
        } else {
            await stopAudioRecording();
            stopAudioPlayer();
            isSessionActiveRef.current = false;
            awaitingGreetingDoneRef.current = false;
            if (useAzureSpeechOn) {
                azureSpeech.inputAudioBufferClear();
            } else {
                realtime.inputAudioBufferClear();
            }
            setIsRecording(false);
        }
    };

    const { t } = useTranslation();

    useEffect(() => {
        const checkMobile = () => {
            setIsMobile(window.innerWidth < 768);
        };
        checkMobile();
        window.addEventListener("resize", checkMobile);
        return () => window.removeEventListener("resize", checkMobile);
    }, []);

    return (
        <div className={`min-h-screen bg-background p-4 text-foreground ${theme}`}>
            <div className="mx-auto max-w-7xl space-y-6">
                <div className="flex flex-col gap-3 text-sm font-semibold text-primary md:flex-row md:items-center md:justify-between">
                    <a
                        href="https://github.com/swigerb/sonic-voice-assistant"
                        target="_blank"
                        rel="noopener noreferrer"
                        className="inline-flex items-center gap-1 rounded-full bg-white/80 px-3 py-1 text-primary transition hover:text-accent"
                        title="View Sonic Voice Ordering source"
                    >
                        <Github className="h-4 w-4" />
                        <span>Source on GitHub</span>
                    </a>
                    <div className="flex items-center gap-2">
                        <Settings
                            isMobile={isMobile}
                            showSessionTokens={showSessionTokens}
                            onShowSessionTokensChange={setShowSessionTokens}
                        />
                        {authEnabled && (
                            <Button variant="ghost" size="icon" className="rounded-full" onClick={logout} title="Logout">
                                <LogOut className="h-4 w-4" />
                            </Button>
                        )}
                    </div>
                </div>

                {sessionIdentifiers && showSessionTokens && <SessionTokenBanner identifiers={sessionIdentifiers} />}

                <BrandHero />

                <div className="grid grid-cols-1 gap-4 md:grid-cols-3 md:gap-8">
                    {/* Mobile Menu Button */}
                    <Sheet>
                        <SheetTrigger asChild>
                            <Button variant="outline" className="mb-4 flex w-full items-center justify-center md:hidden">
                                <Menu className="mr-2 h-4 w-4" />
                                View Sonic Menu
                            </Button>
                        </SheetTrigger>
                        <SheetContent side="left" className="w-[300px] sm:w-[400px]">
                            <SheetHeader>
                                <SheetTitle>Sonic Favorites</SheetTitle>
                            </SheetHeader>
                            <div className="h-[calc(100vh-4rem)] overflow-auto pr-4">
                                <MenuPanel />
                            </div>
                        </SheetContent>
                    </Sheet>

                    {/* Desktop Menu Panel */}
                    <Card className="hidden p-6 md:block">
                        <h2 className="mb-4 text-center font-semibold text-primary">Sonic Favorites</h2>
                        <div className="h-[calc(100vh-13rem)] overflow-auto pr-4">
                            <MenuPanel />
                        </div>
                    </Card>

                    {/* Center Panel - Recording Button and Order Summary */}
                    <Card className="p-6 md:overflow-auto">
                        <div className="space-y-8">
                            <OrderSummary order={useDummyData ? dummyOrder : order} />
                            <div className="mb-4 flex flex-col items-center justify-center">
                                <Button
                                    onClick={onToggleListening}
                                    className={`h-12 w-60 border-none font-semibold shadow-lg transition-colors ${
                                        isRecording ? "bg-[#285780] text-white hover:bg-[#18344D]" : "bg-[#E40046] text-white hover:bg-[#C31B24]"
                                    }`}
                                    aria-label={isRecording ? t("app.stopRecording") : t("app.startRecording")}
                                >
                                    {isRecording ? (
                                        <>
                                            <MicOff className="mr-2 h-4 w-4" />
                                            {t("app.stopConversation")}
                                        </>
                                    ) : (
                                        <>
                                            <Mic className="mr-2 h-6 w-6" />
                                        </>
                                    )}
                                </Button>
                                <StatusMessage isRecording={isRecording} />
                            </div>
                        </div>
                    </Card>

                    {/* Mobile Transcript Button */}
                    <Sheet>
                        <SheetTrigger asChild>
                            <Button variant="outline" className="mt-4 flex w-full items-center justify-center md:hidden">
                                <MessageSquare className="mr-2 h-4 w-4" />
                                Transcript
                            </Button>
                        </SheetTrigger>
                        <SheetContent side="right" className="w-[300px] sm:w-[400px]">
                            <SheetHeader>
                                <SheetTitle>Guest Conversation</SheetTitle>
                            </SheetHeader>
                            <div className="h-[calc(100vh-4rem)] overflow-auto pr-4">
                                <TranscriptPanel transcripts={useDummyData ? dummyTranscripts : transcripts} />
                            </div>
                        </SheetContent>
                    </Sheet>

                    {/* Desktop Transcript Panel */}
                    <Card className="hidden p-6 md:block">
                        <h2 className="mb-4 text-center font-semibold text-primary">Guest Conversation</h2>
                        <div className="h-[calc(100vh-13rem)] overflow-auto pr-4">
                            <TranscriptPanel transcripts={useDummyData ? dummyTranscripts : transcripts} />
                        </div>
                    </Card>
                </div>
            </div>
            <footer className="mx-auto mt-8 max-w-4xl space-y-2 text-center text-xs text-muted-foreground">
                <p className="font-semibold uppercase tracking-[0.35em] text-[#285780]/80">{t("app.footer")}</p>
                <p className="text-[11px] leading-relaxed text-[#18344D]/80">
                    Disclaimer: This project is a non-commercial demo application created for educational and illustrative purposes only. It is not
                    affiliated with, endorsed, or sponsored by Inspire Brands, Inc. or Sonic Corp. Any references to Sonic Drive-In or use of Sonic-inspired colors or
                    themes are solely for demonstration and do not represent an official product.
                </p>
            </footer>
        </div>
    );
}

function BrandHero() {
    return (
        <section className="hero-card rounded-[32px] border border-white/40 bg-white/80 p-6 shadow-[0_25px_70px_rgba(40,87,128,0.18)] backdrop-blur-lg">
            <div className="flex flex-col gap-8 lg:flex-row lg:items-center">
                <div className="flex-1 space-y-5">
                    <div className="flex flex-wrap items-center gap-3">
                        <img src={sonicLogo} alt="Sonic Drive-In logo" className="h-10 w-auto drop-shadow-sm" loading="lazy" />
                        <span className="rounded-full bg-[#E40046]/10 px-3 py-1 text-xs font-black uppercase tracking-[0.3em] text-[#E40046]">
                            Voice Ordering Demo
                        </span>
                    </div>
                    <h1 className="text-4xl font-black leading-tight text-[#E40046] sm:text-5xl">Sonic ordering powered by Azure conversation intelligence</h1>
                    <p className="max-w-2xl text-base text-muted-foreground">
                        Recreate the Sonic Drive-In experience with slushes, burgers, and carhop favorites styled after America's Drive-In—now
                        voice activated with Azure OpenAI + Azure AI Search grounding.
                    </p>
                    <div className="grid gap-3 sm:grid-cols-3">
                        {heroHighlights.map(highlight => (
                            <HeroHighlightCard key={highlight.title} {...highlight} />
                        ))}
                    </div>
                    <div className="flex flex-wrap items-center gap-2 text-xs font-semibold uppercase tracking-[0.3em] text-muted-foreground">
                        <img src={azureLogo} alt="Microsoft Azure" className="h-6 w-auto" loading="lazy" />
                        <span>Azure OpenAI · Azure Speech · Azure AI Search</span>
                    </div>
                </div>
                <div className="relative flex flex-1 items-center justify-center">
                    <div className="absolute inset-0 -z-10 rounded-[32px] bg-gradient-to-br from-[#E40046]/10 via-[#F2F8FA] to-[#FEDD00]/15 opacity-80 blur-3xl"></div>
                    <div className="grid w-full gap-4 sm:grid-cols-2">
                        <div className="rounded-3xl border border-[#E40046]/20 bg-white/90 p-4 shadow-[0_25px_45px_rgba(228,0,70,0.12)]">
                            <div className="mb-3 flex items-center gap-3">
                                <div className="rounded-2xl bg-[#E40046]/10 p-3">
                                    <SlushArt />
                                </div>
                                <div>
                                    <p className="text-xs font-bold uppercase tracking-wide text-[#E40046]">Signature slushes</p>
                                    <p className="text-sm font-semibold text-[#18344D]">Cherry Limeade & more</p>
                                </div>
                            </div>
                            <ul className="text-xs font-medium text-[#18344D]/80">
                                {heroCallouts.map(callout => (
                                    <li key={callout.label} className="flex items-center justify-between rounded-full bg-white/80 px-3 py-1">
                                        <span>{callout.label}</span>
                                        <span style={{ color: callout.accent }}>{callout.value}</span>
                                    </li>
                                ))}
                            </ul>
                        </div>
                        <div className="rounded-3xl border border-[#285780]/25 bg-gradient-to-br from-[#285780]/10 to-[#FEDD00]/10 p-4 shadow-[0_25px_45px_rgba(40,87,128,0.15)]">
                            <div className="mb-3 flex items-center gap-3">
                                <div className="rounded-2xl bg-white/60 p-3">
                                    <BurgerArt />
                                </div>
                                <div>
                                    <p className="text-xs font-bold uppercase tracking-wide text-[#285780]">Carhop favorite</p>
                                    <p className="text-sm font-semibold text-[#18344D]">SuperSONIC® Double Cheeseburger</p>
                                </div>
                            </div>
                            <div className="rounded-2xl bg-white/80 p-3 text-sm font-semibold text-[#18344D]">
                                <p>100% pure beef with melty American cheese</p>
                                <p className="text-xs text-[#E40046]">Perfect pairing: Large Tots & a Shake</p>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        </section>
    );
}

function HeroHighlightCard({ title, detail, tone }: { title: string; detail: string; tone: HighlightTone }) {
    const gradientMap: Record<HighlightTone, string> = {
        red: "from-[#E40046] to-[#FF4D7A]",
        blue: "from-[#285780] to-[#137AC9]",
        yellow: "from-[#FEDD00] to-[#FFE84D]"
    };

    return (
        <div className={`rounded-2xl bg-gradient-to-br ${gradientMap[tone]} p-3 text-white shadow-[0_10px_25px_rgba(0,0,0,0.08)]`}>
            <p className="text-xs uppercase tracking-[0.25em] text-white/80">{title}</p>
            <p className="text-sm font-semibold leading-tight">{detail}</p>
        </div>
    );
}

function SessionTokenBanner({ identifiers }: { identifiers: SessionIdentifiersState }) {
    const truncatedSession = formatToken(identifiers.sessionToken);
    const truncatedRoundTrip = formatToken(identifiers.roundTripToken, 6);

    return (
        <div className="flex flex-wrap gap-2 rounded-3xl border border-white/40 bg-white/90 p-3 font-mono text-xs text-primary shadow-sm">
            <div className="flex items-center gap-2" title={identifiers.sessionToken}>
                <span className="rounded-full bg-[#E40046]/10 px-2 py-1 font-semibold uppercase tracking-widest text-[#E40046]">Session Token</span>
                <span className="text-sm text-[#18344D]">{truncatedSession}</span>
            </div>
            <div className="flex items-center gap-2" title={identifiers.roundTripToken}>
                <span className="rounded-full bg-[#285780]/10 px-2 py-1 font-semibold uppercase tracking-widest text-[#285780]">
                    Round {identifiers.roundTripIndex}
                </span>
                <span className="text-sm text-[#18344D]">{truncatedRoundTrip}</span>
            </div>
        </div>
    );
}

function formatToken(token: string, prefix: number = 8, suffix: number = 4): string {
    if (!token) {
        return "";
    }
    if (token.length <= prefix + suffix + 3) {
        return token;
    }
    return `${token.slice(0, prefix)}…${token.slice(-suffix)}`;
}

function SlushArt() {
    return (
        <svg width="48" height="48" viewBox="0 0 48 48" fill="none" role="img" aria-label="Sonic slush illustration">
            <path d="M16 8h16l-2 32H18L16 8z" fill="#74D2E7" stroke="#285780" strokeWidth="2" />
            <path d="M14 8h20v4H14z" fill="#285780" />
            <path d="M20 16c2 3 6 3 8 0" stroke="#FEDD00" strokeWidth="2" strokeLinecap="round" />
            <circle cx="22" cy="24" r="1.5" fill="#E40046" />
            <circle cx="28" cy="20" r="1.5" fill="#E40046" />
            <circle cx="24" cy="30" r="1.2" fill="#FEDD00" />
            <path d="M24 4v4" stroke="#E40046" strokeWidth="2" strokeLinecap="round" />
            <path d="M20 5l1 3" stroke="#285780" strokeWidth="1.5" strokeLinecap="round" />
        </svg>
    );
}

function BurgerArt() {
    return (
        <svg width="48" height="48" viewBox="0 0 48 48" fill="none" role="img" aria-label="Sonic burger illustration">
            <path d="M10 22c0-8 6-14 14-14s14 6 14 14H10z" fill="#FEDD00" stroke="#285780" strokeWidth="2" />
            <rect x="9" y="22" width="30" height="4" rx="1" fill="#328500" />
            <rect x="9" y="26" width="30" height="3" rx="1" fill="#E40046" />
            <rect x="9" y="29" width="30" height="4" rx="1" fill="#C9CFD4" />
            <path d="M10 33c0 4 6 7 14 7s14-3 14-7H10z" fill="#FEDD00" stroke="#285780" strokeWidth="2" />
            <circle cx="16" cy="16" r="1" fill="#E40046" />
            <circle cx="24" cy="13" r="1" fill="#E40046" />
            <circle cx="32" cy="16" r="1" fill="#E40046" />
        </svg>
    );
}

// Main app component with authentication wrapper
function App() {
    const { isAuthenticated, isLoading, authEnabled } = useAuth();

    if (isLoading) {
        return (
            <div className="flex min-h-screen items-center justify-center">
                <div className="text-center">
                    <div className="mx-auto mb-4 h-12 w-12 animate-spin rounded-full border-4 border-primary border-t-transparent"></div>
                    <p className="text-lg">Loading...</p>
                </div>
            </div>
        );
    }

    if (!isAuthenticated && authEnabled) {
        return null; // Auth provider will handle redirect
    }

    return <SonicApp />;
}

export default function RootApp() {
    return (
        <AuthProvider>
            <ThemeProvider>
                <DummyDataProvider>
                    <AzureSpeechProvider>
                        <App />
                    </AzureSpeechProvider>
                </DummyDataProvider>
            </ThemeProvider>
        </AuthProvider>
    );
}
