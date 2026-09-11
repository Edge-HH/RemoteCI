(() => {
    const form = document.querySelector("[data-voice-form]");
    if (!form) return;
    const find = name => form.querySelector(`[data-voice-${name}]`);
    const record = find("record"), stop = find("stop"), discard = find("discard"), send = find("send");
    const preview = find("preview"), status = find("status");
    const offline = record.disabled;
    let stream, context, node, timer, previewUrl, pcm;
    let chunks = [], bytes = 0, recording = false, starting = false, generation = 0;

    function release() {
        clearTimeout(timer);
        stream?.getTracks().forEach(track => track.stop());
        stream = null;
        if (node) { node.port.onmessage = null; node.disconnect(); node = null; }
        if (context) { void context.close().catch(() => {}); context = null; }
        recording = false;
        stop.disabled = true;
    }
    function clearPreview() {
        preview.pause();
        preview.removeAttribute("src");
        preview.load();
        preview.hidden = true;
        if (previewUrl) URL.revokeObjectURL(previewUrl);
        previewUrl = null;
        pcm = null;
        send.disabled = discard.disabled = true;
    }
    function finish() {
        if (!recording) return;
        release();
        record.disabled = offline;
        if (!bytes) { status.textContent = "没有录到声音，请重新录制"; return; }
        pcm = new Uint8Array(bytes);
        let offset = 0;
        for (const chunk of chunks) { pcm.set(chunk, offset); offset += chunk.length; }
        chunks = [];
        // WAV 只用于本地试听；发送的仍是统一协议要求的原始 PCM。
        const header = new ArrayBuffer(44), view = new DataView(header);
        const text = (offset, value) => [...value].forEach((c, i) => view.setUint8(offset + i, c.charCodeAt(0)));
        text(0, "RIFF"); view.setUint32(4, 36 + bytes, true); text(8, "WAVEfmt ");
        view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
        view.setUint32(24, 16000, true); view.setUint32(28, 32000, true);
        view.setUint16(32, 2, true); view.setUint16(34, 16, true); text(36, "data"); view.setUint32(40, bytes, true);
        previewUrl = URL.createObjectURL(new Blob([header, pcm], { type: "audio/wav" }));
        preview.src = previewUrl;
        preview.hidden = false;
        send.disabled = offline;
        discard.disabled = false;
        status.textContent = `已录制 ${(bytes / 32000).toFixed(1)} 秒，可试听后发送`;
    }
    record.addEventListener("click", async () => {
        const current = ++generation;
        clearPreview();
        starting = true;
        record.disabled = true;
        status.textContent = "正在请求麦克风…";
        try {
            if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia)
                throw new Error("浏览器录音需要 HTTPS 或 localhost，请使用安全地址访问");
            const captured = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true } });
            if (current !== generation) { captured.getTracks().forEach(track => track.stop()); return; }
            stream = captured;
            context = new AudioContext({ sampleRate: 16000 });
            await context.audioWorklet.addModule(form.dataset.workletUrl);
            if (current !== generation) return;
            await context.resume();
            if (current !== generation) return;
            chunks = []; bytes = 0; recording = true; starting = false;
            node = new AudioWorkletNode(context, "voice-recorder");
            node.port.onmessage = ({ data }) => {
                if (data.pcm) {
                    const chunk = new Uint8Array(data.pcm);
                    chunks.push(chunk); bytes += chunk.length;
                    status.textContent = `录音中 ${(bytes / 32000).toFixed(1)} / 60 秒`;
                }
                if (data.stopped) finish();
            };
            context.createMediaStreamSource(stream).connect(node);
            node.connect(context.destination);
            stop.disabled = false;
            stream.getAudioTracks()[0].onended = finish;
            // 后台挂起时也释放麦克风；样本上限由 worklet 独立强制执行。
            timer = setTimeout(finish, 61000);
        } catch (error) {
            if (current !== generation) return;
            starting = false;
            release(); record.disabled = offline;
            status.textContent = error.name === "NotAllowedError" ? "麦克风权限被拒绝，请在浏览器设置中允许后重试" : `录音失败：${error.message}`;
        }
    });
    stop.addEventListener("click", () => { stop.disabled = true; node?.port.postMessage("stop"); });
    discard.addEventListener("click", () => { clearPreview(); status.textContent = "录音已丢弃"; });
    form.addEventListener("submit", async event => {
        event.preventDefault();
        if (!pcm || send.disabled) return;
        preview.pause();
        send.disabled = record.disabled = discard.disabled = true;
        status.textContent = "正在发送，等待 ClassIsland 回执…";
        try {
            const response = await fetch(form.action, {
                method: "POST", credentials: "same-origin",
                headers: { "Content-Type": "application/octet-stream", "X-CSRF-TOKEN": form.querySelector('[name="__RequestVerificationToken"]').value },
                body: pcm,
            });
            if (!response.ok || response.redirected) throw new Error("会话失效或发送失败，请刷新后重试");
            const result = await response.json();
            status.textContent = result.message;
            if (result.success) clearPreview();
        } catch (error) { status.textContent = `未确认发送成功：${error.message}`; }
        finally { record.disabled = offline; send.disabled = !pcm || offline; discard.disabled = !pcm; }
    });
    document.addEventListener("visibilitychange", () => {
        if (!document.hidden) return;
        preview.pause();
        if (recording) finish();
        else if (starting) {
            generation++; starting = false; release(); record.disabled = offline;
            status.textContent = "录音已取消，请回到页面后重试";
        }
    });
    window.addEventListener("pagehide", () => { generation++; release(); chunks = []; clearPreview(); });
})();
