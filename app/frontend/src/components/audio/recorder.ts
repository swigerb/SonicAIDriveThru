export class Recorder {
    onDataAvailable: (buffer: Iterable<number>) => void;
    private audioContext: AudioContext | null = null;
    private mediaStream: MediaStream | null = null;
    private mediaStreamSource: MediaStreamAudioSourceNode | null = null;
    private workletNode: AudioWorkletNode | null = null;
    private gainNode: GainNode | null = null;
    private workletReady = false;
    private isMuted = false;

    public constructor(onDataAvailable: (buffer: Iterable<number>) => void) {
        this.onDataAvailable = onDataAvailable;
    }

    async start(stream: MediaStream) {
        try {
            // Reuse existing AudioContext instead of recreating (expensive operation)
            if (!this.audioContext || this.audioContext.state === "closed") {
                this.audioContext = new AudioContext({ sampleRate: 24000 });
                this.workletReady = false;
            }

            if (this.audioContext.state === "suspended") {
                await this.audioContext.resume();
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

            this.workletNode = new AudioWorkletNode(this.audioContext, "audio-processor-worklet");
            this.workletNode.port.onmessage = event => {
                this.onDataAvailable(event.data.buffer);
            };

            // Audio flow: mediaStreamSource → gainNode → workletNode
            this.mediaStreamSource.connect(this.gainNode);
            this.gainNode.connect(this.workletNode);
        } catch (error) {
            this.stop();
        }
    }

    async stop() {
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
}
