// AudioContext 固定为 16 kHz；仅采集单声道 PCM，输出保持静音以免麦克风回授。
class VoiceRecorder extends AudioWorkletProcessor {
    constructor() {
        super();
        this.samples = 0;
        this.stopped = false;
        this.port.onmessage = () => { this.stopped = true; this.port.postMessage({ stopped: true }); };
    }
    process(inputs) {
        if (this.stopped) return true;
        const input = inputs[0]?.[0];
        if (!input) return true;
        const count = Math.min(input.length, 16000 * 60 - this.samples);
        const pcm = new ArrayBuffer(count * 2);
        const view = new DataView(pcm);
        for (let i = 0; i < count; i++) {
            const sample = Math.max(-1, Math.min(1, input[i]));
            view.setInt16(i * 2, Math.round(sample * (sample < 0 ? 32768 : 32767)), true);
        }
        this.samples += count;
        this.port.postMessage({ pcm }, [pcm]);
        if (this.samples >= 16000 * 60) {
            this.stopped = true;
            this.port.postMessage({ stopped: true });
        }
        return true;
    }
}
registerProcessor("voice-recorder", VoiceRecorder);
