var BasisWebAudioLibrary = {
  $BasisWebAudio__deps: ['$WEBAudio'],
  $BasisWebAudio: {
    state: 0,
    voices: {},
    next: 1,
    mic: null,
    devices: [],
    deviceVersion: 0,
    deviceListener: false,

    refreshDevices: function () {
      if (!navigator.mediaDevices || !navigator.mediaDevices.enumerateDevices) return;
      navigator.mediaDevices.enumerateDevices().then(function (list) {
        var inputs = list.filter(function (device) { return device.kind === 'audioinput' && device.label; });
        var before = BasisWebAudio.devices.map(function (device) { return device.label; }).join('\n');
        var after = inputs.map(function (device) { return device.label; }).join('\n');
        BasisWebAudio.devices = inputs;
        if (before !== after) BasisWebAudio.deviceVersion++;
      }, function () {});
    },

    ptr: function (value) {
      return typeof value === 'bigint' ? Number(value) : value;
    },

    source: [
      "class BasisVoiceOut extends AudioWorkletProcessor {",
      "  constructor() {",
      "    super();",
      "    this.chunks = []; this.offset = 0; this.queued = 0; this.received = 0; this.underruns = 0; this.starved = true; this.sum = 0; this.count = 0; this.alive = true;",
      "    this.port.onmessage = (e) => { const d = e.data; if (d === 0) { this.alive = false; return; } this.chunks.push(d); this.queued += d.length; this.received += d.length; };",
      "  }",
      "  process(inputs, outputs) {",
      "    const out = outputs[0][0]; const n = out.length; let i = 0;",
      "    while (i < n && this.chunks.length) { const c = this.chunks[0]; const take = Math.min(n - i, c.length - this.offset); out.set(c.subarray(this.offset, this.offset + take), i); i += take; this.offset += take; if (this.offset >= c.length) { this.chunks.shift(); this.offset = 0; } }",
      "    this.queued -= i;",
      "    if (i < n) { out.fill(0, i); if (!this.starved) this.underruns++; this.starved = true; } else this.starved = false;",
      "    for (let k = 0; k < i; k++) this.sum += out[k] * out[k];",
      "    this.count += n;",
      "    if (this.count >= 4800) { this.port.postMessage([this.queued, this.received, currentTime, this.underruns, Math.sqrt(this.sum / this.count)]); this.sum = 0; this.count = 0; }",
      "    return this.alive;",
      "  }",
      "}",
      "registerProcessor('basis-voice-out', BasisVoiceOut);",
      "class BasisMicIn extends AudioWorkletProcessor {",
      "  constructor(options) { super(); this.size = options.processorOptions.chunk; this.buffer = new Float32Array(this.size); this.fill = 0; this.alive = true; this.port.onmessage = (e) => { if (e.data === 0) this.alive = false; }; }",
      "  process(inputs) {",
      "    const input = inputs[0];",
      "    if (input && input.length) { const a = input[0], b = input.length > 1 ? input[1] : null; for (let k = 0; k < a.length; k++) { this.buffer[this.fill++] = b ? (a[k] + b[k]) * 0.5 : a[k]; if (this.fill === this.size) { this.port.postMessage(this.buffer, [this.buffer.buffer]); this.buffer = new Float32Array(this.size); this.fill = 0; } } }",
      "    return this.alive;",
      "  }",
      "}",
      "registerProcessor('basis-mic-in', BasisMicIn);"
    ].join('\n'),

    load: function () {
      var context = WEBAudio.audioContext;
      if (BasisWebAudio.state !== 0) return BasisWebAudio.state;
      if (!context || !context.audioWorklet || typeof AudioWorkletNode === 'undefined') {
        BasisWebAudio.state = 3;
        return 3;
      }
      BasisWebAudio.state = 1;
      var url = URL.createObjectURL(new Blob([BasisWebAudio.source], { type: 'application/javascript' }));
      context.audioWorklet.addModule(url).then(function () {
        URL.revokeObjectURL(url);
        BasisWebAudio.state = 2;
        if (BasisWebAudio.mic && BasisWebAudio.mic.stream && !BasisWebAudio.mic.node) BasisWebAudio.connectMic(BasisWebAudio.mic);
      }, function (error) {
        URL.revokeObjectURL(url);
        console.error('[BasisWebAudio] AudioWorklet module failed: ' + error);
        BasisWebAudio.state = 3;
      });
      return 1;
    },

    queued: function (voice) {
      var context = WEBAudio.audioContext;
      var rate = context.sampleRate;
      var estimate = voice.report ? voice.report[0] + (voice.pushed - voice.report[1]) - (context.currentTime - voice.report[2]) * rate : voice.pushed - Math.max(0, context.currentTime - voice.started) * rate;
      return estimate > 0 ? Math.round(estimate) : 0;
    },

    connectMic: function (mic) {
      var context = WEBAudio.audioContext;
      mic.input = context.createMediaStreamSource(mic.stream);
      mic.node = new AudioWorkletNode(context, 'basis-mic-in', { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1], processorOptions: { chunk: mic.chunk } });
      mic.sink = context.createGain();
      mic.sink.gain.value = 0;
      mic.node.port.onmessage = function (e) {
        if (!mic.node) return;
        mic.queue.push(e.data);
        mic.available += e.data.length;
        while (mic.available - mic.offset > mic.limit && mic.queue.length > 1) {
          mic.available -= mic.queue[0].length;
          mic.dropped += mic.queue[0].length - mic.offset;
          mic.queue.shift();
          mic.offset = 0;
        }
      };
      mic.input.connect(mic.node);
      mic.node.connect(mic.sink);
      mic.sink.connect(context.destination);
      mic.state = 2;
    },

    releaseMic: function (mic) {
      if (mic.node) {
        mic.node.port.postMessage(0);
        mic.node.port.onmessage = null;
        mic.node.disconnect();
      }
      if (mic.input) mic.input.disconnect();
      if (mic.sink) mic.sink.disconnect();
      if (mic.stream) mic.stream.getTracks().forEach(function (track) { track.stop(); });
      mic.node = null;
      mic.input = null;
      mic.sink = null;
      mic.stream = null;
      mic.queue = [];
      mic.available = 0;
      mic.offset = 0;
    }
  },

  BasisWebAudio_Initialize__deps: ['$BasisWebAudio'],
  BasisWebAudio_Initialize: function () {
    return BasisWebAudio.load();
  },

  BasisWebAudio_State__deps: ['$BasisWebAudio'],
  BasisWebAudio_State: function () {
    return BasisWebAudio.state;
  },

  BasisWebAudio_SampleRate: function () {
    return WEBAudio.audioContext ? WEBAudio.audioContext.sampleRate : 0;
  },

  BasisWebAudio_ContextRunning: function () {
    return WEBAudio.audioContext && WEBAudio.audioContext.state === 'running' ? 1 : 0;
  },

  BasisWebAudio_CreateVoice__deps: ['$BasisWebAudio'],
  BasisWebAudio_CreateVoice: function () {
    if (BasisWebAudio.state !== 2) return 0;
    var context = WEBAudio.audioContext;
    var voice = { pushed: 0, started: context.currentTime, report: null, spatial: -1, underruns: 0, rms: 0 };
    voice.node = new AudioWorkletNode(context, 'basis-voice-out', { numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [1] });
    voice.panner = context.createPanner();
    voice.panner.panningModel = 'HRTF';
    voice.panner.distanceModel = 'linear';
    voice.panner.rolloffFactor = 0;
    voice.dry = context.createGain();
    voice.wet = context.createGain();
    voice.gain = context.createGain();
    voice.node.port.onmessage = function (e) {
      voice.report = e.data;
      voice.underruns = e.data[3];
      voice.rms = e.data[4];
    };
    voice.dry.connect(voice.gain);
    voice.wet.connect(voice.panner);
    voice.panner.connect(voice.gain);
    voice.gain.connect(context.destination);
    var id = BasisWebAudio.next++;
    BasisWebAudio.voices[id] = voice;
    return id;
  },

  BasisWebAudio_DestroyVoice__deps: ['$BasisWebAudio'],
  BasisWebAudio_DestroyVoice: function (id) {
    var voice = BasisWebAudio.voices[id];
    if (!voice) return;
    voice.node.port.postMessage(0);
    voice.node.port.onmessage = null;
    voice.node.disconnect();
    voice.dry.disconnect();
    voice.wet.disconnect();
    voice.panner.disconnect();
    voice.gain.disconnect();
    delete BasisWebAudio.voices[id];
  },

  BasisWebAudio_PushVoice__deps: ['$BasisWebAudio'],
  BasisWebAudio_PushVoice: function (id, buffer, count) {
    var voice = BasisWebAudio.voices[id];
    if (!voice || count <= 0) return 0;
    var start = BasisWebAudio.ptr(buffer) / 4;
    var chunk = HEAPF32.slice(start, start + count);
    if (voice.pushed === 0 && !voice.report) voice.started = WEBAudio.audioContext.currentTime;
    voice.node.port.postMessage(chunk, [chunk.buffer]);
    voice.pushed += count;
    return count;
  },

  BasisWebAudio_VoiceQueued__deps: ['$BasisWebAudio'],
  BasisWebAudio_VoiceQueued: function (id) {
    var voice = BasisWebAudio.voices[id];
    return voice ? BasisWebAudio.queued(voice) : 0;
  },

  BasisWebAudio_SetVoice__deps: ['$BasisWebAudio'],
  BasisWebAudio_SetVoice: function (id, x, y, z, gain, blend) {
    var voice = BasisWebAudio.voices[id];
    if (!voice) return;
    var spatial = blend > 0 ? 1 : 0;
    if (voice.spatial !== spatial) {
      voice.node.disconnect();
      voice.node.connect(voice.dry);
      if (spatial) voice.node.connect(voice.wet);
      voice.spatial = spatial;
    }
    var dry = Math.fround(1 - blend);
    if (voice.wet.gain.value !== blend) voice.wet.gain.value = blend;
    if (voice.dry.gain.value !== dry) voice.dry.gain.value = dry;
    var p = voice.panner;
    if (spatial) {
      if (p.positionX) {
        if (p.positionX.value !== x) p.positionX.value = x;
        if (p.positionY.value !== y) p.positionY.value = y;
        if (p.positionZ.value !== z) p.positionZ.value = z;
      } else {
        p.setPosition(x, y, z);
      }
    }
    if (voice.gain.gain.value !== gain) voice.gain.gain.value = gain;
  },

  BasisWebAudio_VoiceStats__deps: ['$BasisWebAudio'],
  BasisWebAudio_VoiceStats: function (id, output) {
    var voice = BasisWebAudio.voices[id];
    if (!voice) return 0;
    var start = BasisWebAudio.ptr(output) / 4;
    HEAPF32[start] = voice.rms;
    HEAPF32[start + 1] = voice.underruns;
    HEAPF32[start + 2] = voice.pushed / WEBAudio.audioContext.sampleRate;
    return 1;
  },

  BasisWebAudio_MicStart__deps: ['$BasisWebAudio'],
  BasisWebAudio_MicStart: function (chunk, echoCancellation, noiseSuppression, autoGainControl, deviceIndex) {
    var current = BasisWebAudio.mic;
    if (current && (current.state === 1 || current.state === 2)) return current.state;
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia || BasisWebAudio.state === 3) {
      BasisWebAudio.mic = { state: 4 };
      return 4;
    }
    BasisWebAudio.load();
    if (!BasisWebAudio.deviceListener && navigator.mediaDevices.addEventListener) {
      BasisWebAudio.deviceListener = true;
      navigator.mediaDevices.addEventListener('devicechange', BasisWebAudio.refreshDevices);
    }
    var context = WEBAudio.audioContext;
    var mic = { state: 1, chunk: chunk, queue: [], available: 0, offset: 0, dropped: 0, limit: Math.round(context.sampleRate * 0.5), stream: null, node: null, input: null, sink: null };
    BasisWebAudio.mic = mic;
    var audio = { echoCancellation: !!echoCancellation, noiseSuppression: !!noiseSuppression, autoGainControl: !!autoGainControl, channelCount: 1 };
    var device = deviceIndex >= 0 ? BasisWebAudio.devices[deviceIndex] : null;
    if (device) audio.deviceId = { exact: device.deviceId };
    navigator.mediaDevices.getUserMedia({ audio: audio, video: false }).then(function (stream) {
      BasisWebAudio.refreshDevices();
      if (BasisWebAudio.mic !== mic) {
        stream.getTracks().forEach(function (track) { track.stop(); });
        return;
      }
      mic.stream = stream;
      if (BasisWebAudio.state === 2) BasisWebAudio.connectMic(mic);
    }, function (error) {
      console.warn('[BasisWebAudio] microphone unavailable: ' + error);
      if (BasisWebAudio.mic === mic) mic.state = error && error.name === 'NotAllowedError' ? 3 : 4;
    });
    return 1;
  },

  BasisWebAudio_MicStop__deps: ['$BasisWebAudio'],
  BasisWebAudio_MicStop: function () {
    var mic = BasisWebAudio.mic;
    if (!mic) return;
    BasisWebAudio.releaseMic(mic);
    BasisWebAudio.mic = null;
  },

  BasisWebAudio_MicState__deps: ['$BasisWebAudio'],
  BasisWebAudio_MicState: function () {
    var mic = BasisWebAudio.mic;
    return mic ? mic.state : 0;
  },

  BasisWebAudio_MicDeviceVersion__deps: ['$BasisWebAudio'],
  BasisWebAudio_MicDeviceVersion: function () {
    return BasisWebAudio.deviceVersion;
  },

  BasisWebAudio_MicDevices__deps: ['$BasisWebAudio'],
  BasisWebAudio_MicDevices: function (buffer, size) {
    var names = BasisWebAudio.devices.map(function (device) { return device.label.replace(/\n/g, ' '); }).join('\n');
    var bytes = new TextEncoder().encode(names);
    var count = Math.min(bytes.length, size);
    HEAPU8.set(bytes.subarray(0, count), BasisWebAudio.ptr(buffer));
    return count;
  },

  BasisWebAudio_MicSampleRate__deps: ['$BasisWebAudio'],
  BasisWebAudio_MicSampleRate: function () {
    return WEBAudio.audioContext ? WEBAudio.audioContext.sampleRate : 0;
  },

  BasisWebAudio_MicAvailable__deps: ['$BasisWebAudio'],
  BasisWebAudio_MicAvailable: function () {
    var mic = BasisWebAudio.mic;
    return mic && mic.state === 2 ? mic.available - mic.offset : 0;
  },

  BasisWebAudio_MicRead__deps: ['$BasisWebAudio'],
  BasisWebAudio_MicRead: function (buffer, count) {
    var mic = BasisWebAudio.mic;
    if (!mic || mic.state !== 2 || count <= 0) return 0;
    var start = BasisWebAudio.ptr(buffer) / 4;
    var written = 0;
    while (written < count && mic.queue.length) {
      var chunk = mic.queue[0];
      var take = Math.min(count - written, chunk.length - mic.offset);
      HEAPF32.set(chunk.subarray(mic.offset, mic.offset + take), start + written);
      written += take;
      mic.offset += take;
      if (mic.offset >= chunk.length) {
        mic.available -= chunk.length;
        mic.queue.shift();
        mic.offset = 0;
      }
    }
    return written;
  }
};

autoAddDeps(BasisWebAudioLibrary, '$BasisWebAudio');
mergeInto(LibraryManager.library, BasisWebAudioLibrary);
