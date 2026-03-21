import { useRef } from "react";
import { Recorder } from "@/components/audio/recorder";

const BUFFER_SIZE = 4800;

// Pre-allocated lookup table for fast binary-to-base64 conversion
const CHUNK_SIZE = 8192;

type Parameters = {
    onAudioRecorded: (base64: string) => void;
};

export default function useAudioRecorder({ onAudioRecorded }: Parameters) {
    const audioRecorder = useRef<Recorder>();
    // Use a pre-allocated ring buffer to avoid O(n²) array copies
    const bufferRef = useRef<Uint8Array>(new Uint8Array(BUFFER_SIZE * 4));
    const bufferLenRef = useRef(0);

    const appendToBuffer = (newData: Uint8Array) => {
        const currentLen = bufferLenRef.current;
        const needed = currentLen + newData.length;

        // Grow buffer only when necessary (doubling strategy)
        if (needed > bufferRef.current.length) {
            const grown = new Uint8Array(Math.max(needed, bufferRef.current.length * 2));
            grown.set(bufferRef.current.subarray(0, currentLen));
            bufferRef.current = grown;
        }

        bufferRef.current.set(newData, currentLen);
        bufferLenRef.current = needed;
    };

    const handleAudioData = (data: Iterable<number>) => {
        const uint8Array = new Uint8Array(data);
        appendToBuffer(uint8Array);

        while (bufferLenRef.current >= BUFFER_SIZE) {
            const toSend = bufferRef.current.subarray(0, BUFFER_SIZE);

            // Fast base64 encode: process in chunks to avoid call-stack limits
            let binaryStr = "";
            for (let i = 0; i < BUFFER_SIZE; i += CHUNK_SIZE) {
                const end = Math.min(i + CHUNK_SIZE, BUFFER_SIZE);
                binaryStr += String.fromCharCode.apply(null, toSend.subarray(i, end) as unknown as number[]);
            }
            const base64 = btoa(binaryStr);

            // Shift remaining data to front (no new allocation)
            const remaining = bufferLenRef.current - BUFFER_SIZE;
            if (remaining > 0) {
                bufferRef.current.copyWithin(0, BUFFER_SIZE, bufferLenRef.current);
            }
            bufferLenRef.current = remaining;

            onAudioRecorded(base64);
        }
    };

    const start = async () => {
        if (!audioRecorder.current) {
            audioRecorder.current = new Recorder(handleAudioData);
        }
        bufferLenRef.current = 0;
        const stream = await navigator.mediaDevices.getUserMedia({
            audio: {
                sampleRate: 24000,
                channelCount: 1,
                echoCancellation: true,
                noiseSuppression: true,
                autoGainControl: false
            }
        });
        audioRecorder.current.start(stream);
    };

    const stop = async () => {
        await audioRecorder.current?.stop();
        bufferLenRef.current = 0;
    };

    const mute = () => {
        audioRecorder.current?.mute();
    };

    const unmute = () => {
        audioRecorder.current?.unmute();
    };

    return { start, stop, mute, unmute };
}
