export class Recorder {
    onDataAvailable: (buffer: Iterable<number>) => void;
    onBargeIn: (() => void) | null;
    private audioContext: AudioContext | null = null;
    private mediaStream: MediaStream | null = null;
    private mediaStreamSource: MediaStreamAudioSourceNode | null = null;
    private workletNode: AudioWorkletNode | null = null;
    private gainNode: GainNode | null = null;
    private analyserNode: AnalyserNode | null = null;
    private bargeInTimer: ReturnType<typeof setInterval> | null = null;
    private workletReady = false;
    private isMuted = false;

    // RMS energy threshold for barge-in detection on the raw (pre-gain) stream.
    // Must be high enough that speaker echo doesn't trigger it, but low enough
    // that a user speaking clearly at drive-thru distance does.
    private static readonly BARGE_IN_THRESHOLD = 0.15;
    private static readonly BARGE_IN_CHECK_MS = 100;
    // A suspended AudioContext only resumes after a user gesture; don't hang on it.
    private static readonly RESUME_TIMEOUT_MS = 1500;

    public constructor(onDataAvailable: (buffer: Iterable<number>) => void, onBargeIn?: () => void) {
        this.onDataAvailable = onDataAvailable;
        this.onBargeIn = onBargeIn ?? null;
    }

    /** Resolves true when capture is running; false (mic released) otherwise. */
    async start(stream: MediaStream): Promise<boolean> {
        try {
            // Reuse existing AudioContext instead of recreating (expensive operation)
            if (!this.audioContext || this.audioContext.state === "closed") {
                this.audioContext = new AudioContext({ sampleRate: 24000 });
                this.workletReady = false;
            }

            if (this.audioContext.state === "suspended") {
                await Promise.race([
                    this.audioContext.resume(),
                    new Promise(resolve => setTimeout(resolve, Recorder.RESUME_TIMEOUT_MS))
                ]);
                if ((this.audioContext.state as AudioContextState) !== "running") {
                    throw new Error("AudioContext is suspended until a user gesture");
                }
            }

            if (!this.workletReady) {
                await this.audioContext.audioWorklet.addModule("./audio-processor-worklet.js");
                this.workletReady = true;
            }

            this.mediaStream = stream;
            this.mediaStreamSource = this.audioContext.createMediaStreamSource(this.mediaStream);

            // Create gain node for muting control
            this.gainNode = this.audioContext.createGain();
            this.gainNode.gain.value = this.isMuted ? 0 : 1;

            // AnalyserNode taps the RAW stream (before gain) for barge-in detection.
            // This lets us detect the user speaking even while the gain is muted.
            this.analyserNode = this.audioContext.createAnalyser();
            this.analyserNode.fftSize = 512;

            this.workletNode = new AudioWorkletNode(this.audioContext, "audio-processor-worklet");
            this.workletNode.port.onmessage = event => {
                this.onDataAvailable(event.data.buffer);
            };

            // Audio flow:
            //   mediaStreamSource → gainNode → workletNode  (data path — mutable)
            //   mediaStreamSource → analyserNode             (monitor path — always live)
            this.mediaStreamSource.connect(this.gainNode);
            this.mediaStreamSource.connect(this.analyserNode);
            this.gainNode.connect(this.workletNode);

            this.startBargeInMonitor();
            return true;
        } catch (error) {
            this.stop();
            stream.getTracks().forEach(track => track.stop());
            return false;
        }
    }

    async stop() {
        this.stopBargeInMonitor();

        if (this.mediaStream) {
            this.mediaStream.getTracks().forEach(track => track.stop());
            this.mediaStream = null;
        }

        // Disconnect nodes but keep AudioContext alive for reuse
        if (this.workletNode) {
            this.workletNode.disconnect();
            this.workletNode = null;
        }
        if (this.gainNode) {
            this.gainNode.disconnect();
            this.gainNode = null;
        }
        if (this.analyserNode) {
            this.analyserNode.disconnect();
            this.analyserNode = null;
        }
        if (this.mediaStreamSource) {
            this.mediaStreamSource.disconnect();
            this.mediaStreamSource = null;
        }
    }

    mute() {
        this.isMuted = true;
        if (this.gainNode) {
            this.gainNode.gain.value = 0;
        }
    }

    unmute() {
        this.isMuted = false;
        if (this.gainNode) {
            this.gainNode.gain.value = 1;
        }
    }

    private startBargeInMonitor() {
        if (this.bargeInTimer !== null) return;
        const dataArray = new Uint8Array(this.analyserNode?.fftSize ?? 512);

        this.bargeInTimer = setInterval(() => {
            if (!this.isMuted || !this.analyserNode || !this.onBargeIn) return;

            this.analyserNode.getByteTimeDomainData(dataArray);
            let sumSquares = 0;
            for (let i = 0; i < dataArray.length; i++) {
                const normalized = (dataArray[i] - 128) / 128;
                sumSquares += normalized * normalized;
            }
            const rms = Math.sqrt(sumSquares / dataArray.length);

            if (rms > Recorder.BARGE_IN_THRESHOLD) {
                // Unmute immediately so audio starts flowing to the server
                this.unmute();
                this.onBargeIn();
            }
        }, Recorder.BARGE_IN_CHECK_MS);
    }

    private stopBargeInMonitor() {
        if (this.bargeInTimer !== null) {
            clearInterval(this.bargeInTimer);
            this.bargeInTimer = null;
        }
    }
}
