var BasisWebScreenShareLibrary = {
  $BasisWebShare__deps: ['$BasisWebMedia'],
  $BasisWebShare: {
    captures: {},
    next: 1,
    parked: null,
    levels: [[3600, 108000, 0x1f], [5120, 216000, 0x20], [8192, 245760, 0x28], [8704, 522240, 0x2a], [22080, 589824, 0x32], [36864, 983040, 0x33], [36864, 2073600, 0x34], [139264, 4177920, 0x3c]],

    ptr: function (value) {
      return typeof value === 'bigint' ? Number(value) : value;
    },

    worklet: [
      "class BasisShareIn extends AudioWorkletProcessor {",
      "  constructor() { super(); this.size = 960; this.left = new Float32Array(960); this.right = new Float32Array(960); this.fill = 0; this.alive = true; this.port.onmessage = (e) => { if (e.data === 0) this.alive = false; }; }",
      "  process(inputs) {",
      "    const input = inputs[0];",
      "    if (input && input.length) { const a = input[0], b = input.length > 1 ? input[1] : input[0]; for (let k = 0; k < a.length; k++) { this.left[this.fill] = a[k]; this.right[this.fill] = b[k]; if (++this.fill === this.size) { const out = new Float32Array(this.size * 2); out.set(this.left, 0); out.set(this.right, this.size); this.port.postMessage(out, [out.buffer]); this.fill = 0; } } }",
      "    return this.alive;",
      "  }",
      "}",
      "registerProcessor('basis-share-in', BasisShareIn);"
    ].join('\n'),

    supported: function () {
      return typeof navigator !== 'undefined' && navigator.mediaDevices && navigator.mediaDevices.getDisplayMedia && typeof VideoEncoder !== 'undefined' && typeof VideoFrame !== 'undefined' && typeof EncodedVideoChunk !== 'undefined';
    },

    status: function (c) {
      return JSON.stringify({ state: c.state, error: c.error, encoder: c.encoderName, audio_error: c.audioError, width: c.width, height: c.height, bitrate_kbps: c.bitrate, hardware: false, audio: c.audioOn, captured: c.captured, encoded: c.encoded, dropped: c.dropped, awaiting: c.armed });
    },

    stop: function (c, state, error) {
      if (c.state === 'error' || c.state === 'ended' || c.state === 'closed') return;
      c.state = state;
      c.error = error || '';
      BasisWebShare.release(c);
    },

    park: function (c) {
      var stream = c.stream, video = stream ? stream.getVideoTracks()[0] : null;
      if (!video || video.readyState !== 'live') return;
      BasisWebShare.unpark();
      var parked = { stream: new MediaStream(stream.getTracks().map(function (track) { return track.clone(); })), audioAsked: !!c.cfg.audio, label: c.label, timer: 0 };
      parked.timer = setTimeout(function () { if (BasisWebShare.parked === parked) BasisWebShare.unpark(); }, 3000);
      BasisWebShare.parked = parked;
    },

    unpark: function () {
      var parked = BasisWebShare.parked;
      BasisWebShare.parked = null;
      if (!parked) return null;
      clearTimeout(parked.timer);
      parked.stream.getTracks().forEach(function (track) { track.stop(); });
      return parked;
    },

    adopt: function (c) {
      var parked = BasisWebShare.parked, video = parked ? parked.stream.getVideoTracks()[0] : null;
      if (!video || video.readyState !== 'live' || (c.cfg.audio && !parked.audioAsked)) return false;
      BasisWebShare.parked = null;
      clearTimeout(parked.timer);
      BasisWebShare.run(c, parked.stream);
      return true;
    },

    release: function (c, park) {
      c.disarm();
      if (park) BasisWebShare.park(c);
      if (c.reader) c.reader.cancel().catch(function () {});
      c.reader = null;
      if (c.timer) clearTimeout(c.timer);
      c.timer = 0;
      if (c.stream) c.stream.getTracks().forEach(function (track) { track.onended = null; track.stop(); });
      c.stream = null;
      if (c.grab) {
        c.grab.srcObject = null;
        c.grab = null;
      }
      if (c.encoder && c.encoder.state !== 'closed') try { c.encoder.close(); } catch (e) {}
      if (c.audioEncoder && c.audioEncoder.state !== 'closed') try { c.audioEncoder.close(); } catch (e) {}
      c.encoder = c.audioEncoder = null;
      if (c.node) {
        c.node.port.postMessage(0);
        c.node.port.onmessage = null;
        c.node.disconnect();
        c.node = null;
      }
      if (c.audioContext) c.audioContext.close().catch(function () {});
      c.audioContext = null;
      c.audioOn = false;
    },

    arm: function (c) {
      if (c.armed) return;
      c.armed = true;
      var names = ['pointerup', 'keyup', 'touchend'];
      var fire = function () {
        c.disarm();
        if (c.state === 'starting') BasisWebShare.start(c);
      };
      c.disarm = function () {
        c.armed = false;
        names.forEach(function (name) { document.removeEventListener(name, fire, true); });
        c.disarm = function () {};
      };
      names.forEach(function (name) { document.addEventListener(name, fire, true); });
    },

    start: function (c) {
      var cfg = c.cfg, fps = Math.max(1, cfg.fps || 30);
      if (cfg.audio && !c.audioContext && typeof AudioContext !== 'undefined') {
        try { c.audioContext = new AudioContext({ sampleRate: 48000 }); } catch (e) { c.audioError = 'screenShare.error.webNoAudio'; }
      }
      var video = { frameRate: { ideal: fps, max: fps }, cursor: cfg.cursor ? 'always' : 'never' };
      if (cfg.max_width > 0) video.width = { max: cfg.max_width };
      if (cfg.max_height > 0) video.height = { max: cfg.max_height };
      var options = { video: video, audio: cfg.audio ? { echoCancellation: false, noiseSuppression: false, autoGainControl: false, suppressLocalAudioPlayback: false } : false, selfBrowserSurface: 'exclude', surfaceSwitching: 'include', systemAudio: cfg.audio ? 'include' : 'exclude' };
      var request;
      c.asked = true;
      try { request = navigator.mediaDevices.getDisplayMedia(options); } catch (e) { request = Promise.reject(e); }
      request.then(function (stream) {
        if (c.state !== 'starting') {
          stream.getTracks().forEach(function (track) { track.stop(); });
          return;
        }
        BasisWebShare.run(c, stream);
      }, function (error) {
        if (c.state !== 'starting') return;
        var name = error ? error.name : '';
        var gesture = name === 'InvalidStateError' || (name === 'NotAllowedError' && navigator.userActivation && !navigator.userActivation.isActive && !c.retried);
        if (gesture && !c.retried) {
          c.retried = true;
          BasisWebShare.arm(c);
          return;
        }
        console.warn('[BasisWebShare] getDisplayMedia: ' + error);
        BasisWebShare.stop(c, 'error', name === 'NotAllowedError' ? 'screenShare.error.webDenied' : name === 'NotFoundError' || name === 'NotSupportedError' ? 'screenShare.error.unsupportedWeb' : 'screenShare.error.open');
      });
    },

    run: function (c, stream) {
      c.stream = stream;
      var track = stream.getVideoTracks()[0];
      if (!track) return BasisWebShare.stop(c, 'error', 'screenShare.error.open');
      c.label = track.label || '';
      track.onended = function () { BasisWebShare.stop(c, 'ended', 'screenShare.error.webStopped'); };
      if (typeof MediaStreamTrackProcessor !== 'undefined') {
        try {
          c.reader = new MediaStreamTrackProcessor({ track: track }).readable.getReader();
        } catch (e) {
          c.reader = null;
        }
      }
      if (c.reader) {
        var reader = c.reader;
        var pump = function () {
          reader.read().then(function (result) {
            if (result.done || c.reader !== reader) {
              if (result.value) result.value.close();
              return;
            }
            BasisWebShare.frame(c, result.value);
            pump();
          }, function () {});
        };
        pump();
      } else {
        var grab = document.createElement('video');
        grab.muted = true;
        grab.playsInline = true;
        grab.srcObject = new MediaStream([track]);
        c.grab = grab;
        grab.play().catch(function () {});
        var tick = function () {
          if (c.grab !== grab) return;
          if (grab.readyState >= 2 && grab.videoWidth > 0) {
            var frame = null;
            try { frame = new VideoFrame(grab, { timestamp: Math.round(performance.now() * 1000) }); } catch (e) {}
            if (frame) BasisWebShare.frame(c, frame);
          }
          c.timer = setTimeout(tick, Math.max(4, 1000 / Math.max(1, c.cfg.fps || 30) / 2));
        };
        tick();
      }
      var audio = stream.getAudioTracks()[0];
      if (c.cfg.audio) {
        if (audio && !c.audioContext && typeof AudioContext !== 'undefined') try { c.audioContext = new AudioContext({ sampleRate: 48000 }); } catch (e) {}
        if (!audio) c.audioError = 'screenShare.error.webNoAudio';
        else if (typeof AudioEncoder === 'undefined' || typeof AudioData === 'undefined' || !c.audioContext || !c.audioContext.audioWorklet) c.audioError = 'screenShare.error.webNoAudioEncoder';
        else BasisWebShare.audio(c, audio);
      }
    },

    pick: function (c, width, height) {
      var cfg = c.cfg, fps = Math.max(1, cfg.fps || 30), maxWidth = cfg.max_width > 0 ? cfg.max_width : width, maxHeight = cfg.max_height > 0 ? cfg.max_height : height;
      var scale = Math.min(1, maxWidth / width, maxHeight / height);
      var w = Math.max(16, Math.round(width * scale) & ~1), h = Math.max(16, Math.round(height * scale) & ~1);
      var blocks = Math.ceil(w / 16) * Math.ceil(h / 16), level = 0x3c;
      for (var i = 0; i < BasisWebShare.levels.length; i++) {
        if (blocks <= BasisWebShare.levels[i][0] && blocks * fps <= BasisWebShare.levels[i][1]) {
          level = BasisWebShare.levels[i][2];
          break;
        }
      }
      var hex = (level < 16 ? '0' : '') + level.toString(16);
      var candidates = ['avc1.42e0' + hex, 'avc1.4d00' + hex, 'avc1.6400' + hex].map(function (codec) {
        return { codec: codec, width: w, height: h, bitrate: c.bitrate * 1000, framerate: fps, latencyMode: 'realtime', bitrateMode: 'variable', avc: { format: 'annexb' }, hardwareAcceleration: 'no-preference' };
      });
      var attempt = function (index) {
        if (index >= candidates.length) return Promise.resolve(null);
        var config = candidates[index];
        return VideoEncoder.isConfigSupported(config).then(function (result) {
          return result && result.supported ? config : attempt(index + 1);
        }, function () { return attempt(index + 1); });
      };
      return attempt(0);
    },

    encoder: function (c, config) {
      var encoder = new VideoEncoder({
        output: function (chunk) {
          if (c.encoder !== encoder) return;
          var data = new Uint8Array(chunk.byteLength);
          chunk.copyTo(data);
          if (c.video.length >= 90) {
            c.video.length = 0;
            c.dropped++;
            c.keyRequested = true;
          }
          c.video.push({ data: data, key: chunk.type === 'key', pts: chunk.timestamp });
          c.encoded++;
          if (c.state === 'starting') c.state = 'running';
        },
        error: function (error) {
          if (c.encoder !== encoder) return;
          console.warn('[BasisWebShare] video encoder: ' + error);
          BasisWebShare.stop(c, 'error', 'screenShare.error.webEncoder');
        }
      });
      encoder.configure(config);
      c.encoder = encoder;
      c.config = config;
      c.width = config.width;
      c.height = config.height;
      c.encoderName = 'WebCodecs ' + config.codec;
      c.keyRequested = true;
    },

    frame: function (c, frame) {
      var now = performance.now(), interval = 1000 / Math.max(1, c.cfg.fps || 30);
      if (c.state !== 'starting' && c.state !== 'running') return frame.close();
      if (!c.encoder) {
        if (!c.picking) {
          c.picking = true;
          BasisWebShare.pick(c, frame.displayWidth, frame.displayHeight).then(function (config) {
            if (c.state !== 'starting') return;
            if (!config) return BasisWebShare.stop(c, 'error', 'screenShare.error.webEncoder');
            try { BasisWebShare.encoder(c, config); } catch (e) { BasisWebShare.stop(c, 'error', 'screenShare.error.webEncoder'); }
          });
        }
        return frame.close();
      }
      if (now - c.lastFrame < interval * 0.85) return frame.close();
      c.lastFrame = now;
      c.captured++;
      if (c.encoder.encodeQueueSize > 2) {
        c.dropped++;
        return frame.close();
      }
      if (c.t0 < 0) c.t0 = now;
      var timestamp = c.ptsBase + Math.round((now - c.t0) * 1000), input;
      try {
        if (frame.displayWidth === c.width && frame.displayHeight === c.height) input = new VideoFrame(frame, { timestamp: timestamp });
        else input = new VideoFrame(BasisWebShare.fit(c, frame), { timestamp: timestamp });
      } catch (e) {
        frame.close();
        return;
      }
      frame.close();
      var preview = c.preview ? BasisWebMedia.sessions[c.preview] : null;
      if (preview) BasisWebMedia.offer(preview, input.clone());
      var key = c.keyRequested || now - c.lastKey >= Math.max(1000, c.cfg.keyframe_interval_ms || 10000);
      try {
        c.encoder.encode(input, { keyFrame: key });
        if (key) {
          c.keyRequested = false;
          c.lastKey = now;
        }
      } catch (e) {
        c.dropped++;
      }
      input.close();
    },

    fit: function (c, frame) {
      if (!c.canvas) {
        c.canvas = typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(c.width, c.height) : document.createElement('canvas');
        c.canvas.width = c.width;
        c.canvas.height = c.height;
        c.canvasContext = c.canvas.getContext('2d');
      }
      var g = c.canvasContext, scale = Math.min(c.width / frame.displayWidth, c.height / frame.displayHeight), w = frame.displayWidth * scale, h = frame.displayHeight * scale;
      g.fillStyle = '#000';
      g.fillRect(0, 0, c.width, c.height);
      g.drawImage(frame, (c.width - w) / 2, (c.height - h) / 2, w, h);
      return c.canvas;
    },

    audio: function (c, track) {
      var context = c.audioContext, url = URL.createObjectURL(new Blob([BasisWebShare.worklet], { type: 'application/javascript' }));
      context.resume().catch(function () {});
      context.audioWorklet.addModule(url).then(function () {
        URL.revokeObjectURL(url);
        if (c.audioContext !== context || !c.stream) return;
        var encoder = new AudioEncoder({
          output: function (chunk) {
            if (c.audioEncoder !== encoder) return;
            var data = new Uint8Array(chunk.byteLength);
            chunk.copyTo(data);
            if (c.audioQueue.length >= 150) c.audioQueue.shift();
            c.audioQueue.push({ data: data, pts: chunk.timestamp });
          },
          error: function (error) {
            if (c.audioEncoder !== encoder) return;
            console.warn('[BasisWebShare] audio encoder: ' + error);
            c.audioEncoder = null;
            c.audioOn = false;
            c.audioError = 'screenShare.error.webNoAudioEncoder';
          }
        });
        var config = { codec: 'opus', sampleRate: 48000, numberOfChannels: 2, bitrate: Math.max(16, c.cfg.audio_bitrate_kbps || 128) * 1000 };
        return AudioEncoder.isConfigSupported(config).then(function (result) {
          if (!result || !result.supported) throw new Error('opus encoding is not supported');
          encoder.configure(config);
          c.audioEncoder = encoder;
          var source = context.createMediaStreamSource(new MediaStream([track]));
          var node = new AudioWorkletNode(context, 'basis-share-in', { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1], channelCount: 2, channelCountMode: 'explicit', channelInterpretation: 'speakers' });
          var sink = context.createGain();
          sink.gain.value = 0;
          node.port.onmessage = function (e) {
            if (c.audioEncoder !== encoder || c.state !== 'running') return;
            var now = performance.now();
            if (c.t0 < 0) return;
            if (c.audioStart < 0 || Math.abs(c.ptsBase + (now - c.t0) * 1000 - (c.audioStart + c.audioSamples * 1e6 / 48000)) > 200000) {
              c.audioStart = c.ptsBase + Math.round((now - c.t0) * 1000) - 20000;
              c.audioSamples = 0;
            }
            var data = new AudioData({ format: 'f32-planar', sampleRate: 48000, numberOfFrames: 960, numberOfChannels: 2, timestamp: Math.round(c.audioStart + c.audioSamples * 1e6 / 48000), data: e.data });
            c.audioSamples += 960;
            try { encoder.encode(data); } catch (err) {}
            data.close();
          };
          source.connect(node);
          node.connect(sink);
          sink.connect(context.destination);
          c.node = node;
          c.audioOn = true;
          c.audioError = '';
        });
      }).catch(function (error) {
        URL.revokeObjectURL(url);
        console.warn('[BasisWebShare] audio capture: ' + error);
        c.audioOn = false;
        c.audioError = 'screenShare.error.webNoAudioEncoder';
      });
    }
  },

  BasisWebShare_Supported__deps: ['$BasisWebShare'],
  BasisWebShare_Supported: function () {
    return BasisWebShare.supported() ? 1 : 0;
  },

  BasisWebShare_Open__deps: ['$BasisWebShare'],
  BasisWebShare_Open: function (descriptor, length) {
    if (!BasisWebShare.supported()) return -1;
    var cfg;
    try {
      var p = BasisWebShare.ptr(descriptor);
      cfg = JSON.parse(new TextDecoder().decode(HEAPU8.slice(p, p + length)));
    } catch (e) {
      return -2;
    }
    var id = BasisWebShare.next++;
    var c = { id: id, cfg: cfg, state: 'starting', error: '', audioError: '', encoderName: 'WebCodecs H.264', width: 0, height: 0, bitrate: Math.max(100, cfg.bitrate_kbps || 4000), captured: 0, encoded: 0, dropped: 0, armed: false, disarm: function () {}, asked: false, retried: false, stream: null, reader: null, grab: null, timer: 0, encoder: null, picking: false, config: null, audioContext: null, audioEncoder: null, node: null, audioOn: false, video: [], audioQueue: [], keyRequested: true, lastKey: 0, lastFrame: -1e9, t0: -1, ptsBase: Math.max(0, cfg.pts_base_us || 0), audioStart: -1, audioSamples: 0, preview: 0, canvas: null };
    BasisWebShare.captures[id] = c;
    if (BasisWebShare.adopt(c)) return id;
    BasisWebShare.unpark();
    if (navigator.userActivation && !navigator.userActivation.isActive) BasisWebShare.arm(c);
    else BasisWebShare.start(c);
    return id;
  },

  BasisWebShare_Close__deps: ['$BasisWebShare'],
  BasisWebShare_Close: function (id) {
    var c = BasisWebShare.captures[id];
    if (!c) return;
    BasisWebShare.release(c, c.state === 'running');
    c.state = 'closed';
    delete BasisWebShare.captures[id];
  },

  BasisWebShare_Status__deps: ['$BasisWebShare'],
  BasisWebShare_Status: function (id, buffer, capacity) {
    var c = BasisWebShare.captures[id];
    if (!c) return -1;
    var bytes = new TextEncoder().encode(BasisWebShare.status(c));
    if (capacity >= bytes.length) HEAPU8.set(bytes, BasisWebShare.ptr(buffer));
    return bytes.length;
  },

  BasisWebShare_Pending__deps: ['$BasisWebShare'],
  BasisWebShare_Pending: function (id) {
    var c = BasisWebShare.captures[id];
    return c ? c.video.length + c.audioQueue.length : 0;
  },

  BasisWebShare_ReadVideo__deps: ['$BasisWebShare'],
  BasisWebShare_ReadVideo: function (id, buffer, capacity, info) {
    var c = BasisWebShare.captures[id];
    if (!c) return -1;
    if (!c.video.length) return 0;
    var item = c.video[0], view = new DataView(HEAPU8.buffer), at = BasisWebShare.ptr(info);
    view.setFloat64(at, item.pts, true);
    view.setFloat64(at + 8, item.data.length, true);
    view.setFloat64(at + 16, item.key ? 1 : 0, true);
    view.setFloat64(at + 24, c.width, true);
    view.setFloat64(at + 32, c.height, true);
    if (item.data.length > capacity) return -4;
    c.video.shift();
    HEAPU8.set(item.data, BasisWebShare.ptr(buffer));
    return item.data.length;
  },

  BasisWebShare_ReadAudio__deps: ['$BasisWebShare'],
  BasisWebShare_ReadAudio: function (id, buffer, capacity, info) {
    var c = BasisWebShare.captures[id];
    if (!c) return -1;
    if (!c.audioQueue.length) return 0;
    var item = c.audioQueue[0];
    if (item.data.length > capacity) {
      c.audioQueue.shift();
      return 0;
    }
    c.audioQueue.shift();
    new DataView(HEAPU8.buffer).setFloat64(BasisWebShare.ptr(info), item.pts, true);
    HEAPU8.set(item.data, BasisWebShare.ptr(buffer));
    return item.data.length;
  },

  BasisWebShare_RequestKeyframe__deps: ['$BasisWebShare'],
  BasisWebShare_RequestKeyframe: function (id) {
    var c = BasisWebShare.captures[id];
    if (c) c.keyRequested = true;
  },

  BasisWebShare_SetBitrate__deps: ['$BasisWebShare'],
  BasisWebShare_SetBitrate: function (id, kbps) {
    var c = BasisWebShare.captures[id];
    if (!c || kbps <= 0 || kbps === c.bitrate) return;
    c.bitrate = kbps;
    if (!c.encoder || c.encoder.state !== 'configured' || !c.config) return;
    c.config.bitrate = kbps * 1000;
    try { c.encoder.configure(c.config); } catch (e) { console.warn('[BasisWebShare] bitrate change: ' + e); }
  },

  BasisWebShare_SetPreview__deps: ['$BasisWebShare'],
  BasisWebShare_SetPreview: function (id, session) {
    var c = BasisWebShare.captures[id];
    if (c) c.preview = session;
  }
};

autoAddDeps(BasisWebScreenShareLibrary, '$BasisWebShare');
mergeInto(LibraryManager.library, BasisWebScreenShareLibrary);
