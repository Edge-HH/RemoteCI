import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const script = name => readFileSync(new URL(`../RemoteCI.Server/wwwroot/${name}.js`, import.meta.url), "utf8");

function recorder() {
    let Recorder;
    const sandbox = {
        AudioWorkletProcessor: class { constructor() { this.port = { messages: [], postMessage(value) { this.messages.push(value); } }; } },
        registerProcessor: (_, type) => { Recorder = type; },
    };
    vm.runInNewContext(script("voice-recorder-worklet"), sandbox);
    return new Recorder();
}

test("PCM conversion clips samples and preserves signed little-endian values", () => {
    const instance = recorder();
    instance.process([[new Float32Array([-2, -1, 0, 1, 2])]]);
    const view = new DataView(instance.port.messages[0].pcm);
    assert.deepEqual(Array.from({ length: 5 }, (_, i) => view.getInt16(i * 2, true)), [-32768, -32768, 0, 32767, 32767]);
});

test("recorder caps exactly 60 seconds and sends no more PCM after stop", () => {
    const instance = recorder();
    const input = [[new Float32Array(128).fill(0.25)]];
    for (let i = 0; i < 7600; i++) instance.process(input);
    assert.equal(instance.port.messages.reduce((sum, x) => sum + (x.pcm?.byteLength ?? 0), 0), 1_920_000);
    assert.equal(instance.port.messages.filter(x => x.stopped).length, 1);
    const manuallyStopped = recorder();
    manuallyStopped.port.onmessage();
    manuallyStopped.process(input);
    assert.equal(manuallyStopped.port.messages.length, 1);
    assert.equal(manuallyStopped.port.messages[0].stopped, true);
});

function browser(getUserMedia) {
    const elements = Object.fromEntries(["record", "stop", "discard", "send", "preview", "status"].map(name => [name, {
        disabled: name !== "record", hidden: true, handlers: {},
        addEventListener(type, fn) { this.handlers[type] = fn; },
        pause() {}, load() {}, removeAttribute() {},
    }]));
    const form = {
        handlers: {}, dataset: { workletUrl: "/voice-recorder-worklet.js" },
        querySelector: query => elements[query.match(/data-voice-(\w+)/)?.[1]],
        addEventListener(type, fn) { this.handlers[type] = fn; },
    };
    const document = { hidden: false, handlers: {}, querySelector: () => form,
        addEventListener(type, fn) { this.handlers[type] = fn; } };
    const window = { isSecureContext: true, handlers: {}, addEventListener(type, fn) { this.handlers[type] = fn; } };
    vm.runInNewContext(script("voice-message"), { document, window, navigator: { mediaDevices: { getUserMedia } },
        URL, Blob, setTimeout, clearTimeout });
    return { elements, document, window };
}

test("a late microphone permission result cannot start recording after leaving the page", async () => {
    let grant;
    let stopped = false;
    const app = browser(() => new Promise(resolve => { grant = resolve; }));
    const pending = app.elements.record.handlers.click();
    app.window.handlers.pagehide();
    grant({ getTracks: () => [{ stop: () => { stopped = true; } }] });
    await pending;
    assert.equal(stopped, true);
    assert.equal(app.elements.send.disabled, true);
});

test("hiding page during permission prompt cancels capture and restores recording button", async () => {
    let grant;
    let stopped = false;
    const app = browser(() => new Promise(resolve => { grant = resolve; }));
    const pending = app.elements.record.handlers.click();
    app.document.hidden = true;
    app.document.handlers.visibilitychange();
    grant({ getTracks: () => [{ stop: () => { stopped = true; } }] });
    await pending;
    assert.equal(stopped, true);
    assert.equal(app.elements.record.disabled, false);
});

test("permission refusal remains retryable and never enables send", async () => {
    const error = new Error("denied"); error.name = "NotAllowedError";
    const app = browser(() => Promise.reject(error));
    await app.elements.record.handlers.click();
    assert.match(app.elements.status.textContent, /权限被拒绝/);
    assert.equal(app.elements.record.disabled, false);
    assert.equal(app.elements.send.disabled, true);
});
