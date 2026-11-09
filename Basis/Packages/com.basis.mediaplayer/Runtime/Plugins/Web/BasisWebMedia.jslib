var BasisWebMediaLibrary = {
  $BasisWebMedia__deps: ['$WEBAudio'],
  $BasisWebMedia: {
    sessions: {},
    next: 1,
    hlsWaiters: null,
    unlockArmed: false,
    hlsUrl: 'https://cdn.jsdelivr.net/npm/hls.js@1.7.3/dist/hls.min.js',
    hlsIntegrity: 'sha384-cciJ0zi8d1uMKC2zJd7jvPY4HQt7W4ByUI/FlMkltvBi31aW61rcpVBhpmW8/NwX',

    ptr: function (value) {
      return typeof value === 'bigint' ? Number(value) : value;
    },

    bytes: function (pointer, length) {
      var p = BasisWebMedia.ptr(pointer);
      return HEAPU8.slice(p, p + length);
    },

    putDoubles: function (pointer, values) {
      var view = new DataView(HEAPU8.buffer), p = BasisWebMedia.ptr(pointer);
      for (var i = 0; i < values.length; i++) view.setFloat64(p + i * 8, values[i], true);
    },

    context: function () {
      return typeof WEBAudio !== 'undefined' && WEBAudio.audioContext ? WEBAudio.audioContext : null;
    },

    event: function (s, code, stage, detail) {
      if (s.events.length >= 256) s.events.shift();
      s.events.push({ code: code, stage: stage, detail: detail, at: performance.now() - s.opened });
    },

    fail: function (s, category, sub, key) {
      if (s.state === 6) return;
      s.state = 6;
      s.errorCode = category * 100 + sub;
      s.category = category;
      BasisWebMedia.event(s, 13, category === 3 ? 3 : category === 4 ? 5 : category === 2 ? 1 : 0, key);
      console.warn('[BasisWebMedia] session ' + s.id + ' failed: ' + key);
      BasisWebMedia.halt(s);
    },

    halt: function (s) {
      if (s.hls) {
        try { s.hls.destroy(); } catch (e) {}
        s.hls = null;
      }
      if (s.pc) {
        try { s.pc.close(); } catch (e) {}
        s.pc = null;
        if (s.resource) fetch(s.resource, { method: 'DELETE' }).catch(function () {});
        s.resource = null;
      }
      if (s.stream) s.stream.getTracks().forEach(function (track) { track.stop(); });
      s.stream = null;
      if (s.video) {
        var v = s.video;
        s.video = null;
        try {
          v.pause();
          v.removeAttribute('src');
          v.srcObject = null;
          v.load();
        } catch (e) {}
      }
      if (s.decoder && s.decoder.state !== 'closed') try { s.decoder.close(); } catch (e) {}
      if (s.audioDecoder && s.audioDecoder.state !== 'closed') try { s.audioDecoder.close(); } catch (e) {}
      s.decoder = s.audioDecoder = null;
      if (s.frame) s.frame.close();
      s.frame = null;
      if (s.audioNode) try { s.audioNode.disconnect(); } catch (e) {}
      if (s.gainNode) try { s.gainNode.disconnect(); } catch (e) {}
      s.audioNode = s.gainNode = null;
    },

    blockedHost: function (host) {
      host = host.toLowerCase().replace(/^\[|\]$/g, '');
      if (host === 'localhost' || /\.(localhost|local|internal|lan|home\.arpa)$/.test(host)) return true;
      var m = /^(\d+)\.(\d+)\.(\d+)\.(\d+)$/.exec(host);
      if (m) {
        var a = +m[1], b = +m[2];
        return a === 0 || a === 10 || a === 127 || (a === 100 && b >= 64 && b < 128) || (a === 169 && b === 254) || (a === 172 && b >= 16 && b < 32) || (a === 192 && b === 168);
      }
      return host === '::' || host === '::1' || /^f[cd][0-9a-f]{2}:/.test(host) || /^fe[89ab][0-9a-f]:/.test(host) || /^::ffff:/.test(host);
    },

    armUnlock: function () {
      if (BasisWebMedia.unlockArmed || typeof document === 'undefined') return;
      BasisWebMedia.unlockArmed = true;
      var unlock = function () {
        BasisWebMedia.unlockArmed = false;
        ['pointerup', 'keydown', 'touchend'].forEach(function (name) { document.removeEventListener(name, unlock, true); });
        var context = BasisWebMedia.context();
        if (context && context.state === 'suspended') context.resume().catch(function () {});
        for (var id in BasisWebMedia.sessions) {
          var s = BasisWebMedia.sessions[id];
          if (!s.autoMuted || !s.video) continue;
          s.autoMuted = false;
          s.video.muted = s.routedStream;
          if (s.want && s.video.paused) BasisWebMedia.play(s);
        }
      };
      ['pointerup', 'keydown', 'touchend'].forEach(function (name) { document.addEventListener(name, unlock, true); });
    },

    play: function (s) {
      var v = s.video;
      if (!v || s.state === 6) return;
      var promise;
      s.playPending = true;
      try { promise = v.play(); } catch (e) { promise = null; }
      if (!promise || !promise.then) {
        s.playPending = false;
        return;
      }
      promise.then(function () {
        if (s.video === v) s.playPending = false;
      }, function (error) {
        if (s.video !== v) return;
        s.playPending = false;
        if (!error || error.name !== 'NotAllowedError' || v.muted) return;
        v.muted = true;
        s.autoMuted = true;
        BasisWebMedia.armUnlock();
        if (s.want) BasisWebMedia.play(s);
      });
    },

    route: function (s) {
      var context = BasisWebMedia.context();
      if (s.audioNode || s.direct || !context) return false;
      try {
        if (!s.gainNode) {
          s.gainNode = context.createGain();
          s.gainNode.gain.value = s.gain;
          s.gainNode.connect(context.destination);
        }
        if (s.kind === 'element') s.audioNode = s.stream ? context.createMediaStreamSource(s.stream) : context.createMediaElementSource(s.video);
        if (s.audioNode) s.audioNode.connect(s.gainNode);
        return true;
      } catch (e) {
        console.warn('[BasisWebMedia] audio routing failed, using the element volume: ' + e);
        s.direct = true;
        return false;
      }
    },

    probe: function (url) {
      var abort = typeof AbortController !== 'undefined' ? new AbortController() : null;
      var options = { mode: 'cors', credentials: 'omit', cache: 'no-store' };
      if (abort) options.signal = abort.signal;
      return fetch(url, options).then(function () {
        if (abort) abort.abort();
        return 'ok';
      }, function () {
        return fetch(url, { mode: 'no-cors', credentials: 'omit', cache: 'no-store' }).then(function () { return 'cors'; }, function () { return 'network'; });
      });
    },

    elementFailed: function (s, v) {
      if (s.video !== v || s.state === 6) return;
      var code = v.error ? v.error.code : 0;
      if (code === 1) return;
      var key = code === 3 ? 'mediaPlayer.web.decode' : code === 4 ? 'mediaPlayer.web.format' : 'mediaPlayer.web.network';
      if (!s.cors || !/^https?:/i.test(s.url)) return BasisWebMedia.fail(s, code === 3 ? 3 : 1, code || 9, key);
      BasisWebMedia.probe(s.url).then(function (result) {
        if (s.video !== v || s.state === 6) return;
        if (result === 'cors') {
          console.warn('[BasisWebMedia] ' + s.url + ' sends no CORS headers; retrying for sound only');
          BasisWebMedia.halt(s);
          s.cors = false;
          s.direct = true;
          s.video = BasisWebMedia.element(s);
          s.video.volume = Math.max(0, Math.min(1, s.gain));
          s.video.src = s.url;
          if (s.want) BasisWebMedia.play(s);
          return;
        }
        BasisWebMedia.fail(s, result === 'network' ? 1 : 2, code || 9, result === 'network' ? 'mediaPlayer.web.network' : key);
      });
    },

    element: function (s) {
      var v = document.createElement('video');
      v.playsInline = true;
      v.setAttribute('playsinline', '');
      v.preload = 'auto';
      if (s.cors) v.crossOrigin = 'anonymous';
      v.addEventListener('loadedmetadata', function () {
        if (s.video !== v) return;
        if (s.liveHint === 1 || (s.liveHint !== 2 && (v.duration === Infinity || s.hlsLive))) s.live = true;
        if (!s.cors && v.videoWidth > 0) BasisWebMedia.fail(s, 1, 3, 'mediaPlayer.web.cors');
      });
      v.addEventListener('waiting', function () { if (s.video === v) s.waiting = true; });
      ['playing', 'canplay', 'pause'].forEach(function (name) { v.addEventListener(name, function () { if (s.video === v) s.waiting = false; }); });
      v.addEventListener('seeked', function () {
        if (s.video !== v) return;
        s.waiting = false;
        s.dirty = true;
        if (s.seekUs < 0) return;
        BasisWebMedia.event(s, 10, 1, 'to ' + s.seekUs + 'us, landed at ' + Math.round(v.currentTime * 1e6) + 'us');
        s.seekUs = -1;
      });
      v.addEventListener('error', function () { BasisWebMedia.elementFailed(s, v); });
      if (v.requestVideoFrameCallback) {
        var frame = function (now, metadata) {
          if (s.video !== v) return;
          s.rvfc = true;
          s.dirty = true;
          if (metadata && metadata.presentedFrames) s.decoded = metadata.presentedFrames;
          v.requestVideoFrameCallback(frame);
        };
        v.requestVideoFrameCallback(frame);
      }
      return v;
    },

    withHls: function (done) {
      if (typeof window !== 'undefined' && window.Hls) return done(window.Hls);
      if (BasisWebMedia.hlsWaiters) {
        BasisWebMedia.hlsWaiters.push(done);
        return;
      }
      BasisWebMedia.hlsWaiters = [done];
      var script = document.createElement('script');
      var finish = function () {
        var waiters = BasisWebMedia.hlsWaiters || [];
        BasisWebMedia.hlsWaiters = null;
        waiters.forEach(function (waiter) { waiter(window.Hls || null); });
      };
      script.src = BasisWebMedia.hlsUrl;
      script.integrity = BasisWebMedia.hlsIntegrity;
      script.crossOrigin = 'anonymous';
      script.async = true;
      script.onload = finish;
      script.onerror = function () {
        console.warn('[BasisWebMedia] hls.js could not be loaded from ' + BasisWebMedia.hlsUrl);
        finish();
      };
      document.head.appendChild(script);
    },

    loadHls: function (s, url) {
      if (typeof MediaSource === 'undefined' && typeof ManagedMediaSource === 'undefined') return BasisWebMedia.fail(s, 2, 4, 'mediaPlayer.web.hls');
      BasisWebMedia.withHls(function (Hls) {
        if (BasisWebMedia.sessions[s.id] !== s || s.state === 6 || !s.video) return;
        if (!Hls || !Hls.isSupported()) return BasisWebMedia.fail(s, 2, 4, 'mediaPlayer.web.hls');
        var hls = new Hls({ enableWorker: true, lowLatencyMode: true, backBufferLength: 30 });
        s.hls = hls;
        hls.on(Hls.Events.LEVEL_LOADED, function (name, data) {
          if (s.hls !== hls || !data || !data.details) return;
          s.hlsLive = !!data.details.live;
          if (s.hlsLive && s.liveHint !== 2) s.live = true;
        });
        hls.on(Hls.Events.ERROR, function (name, data) {
          if (s.hls !== hls || !data || !data.fatal) return;
          if (data.type === Hls.ErrorTypes.MEDIA_ERROR && !s.recovered) {
            s.recovered = true;
            hls.recoverMediaError();
            return;
          }
          if (data.type !== Hls.ErrorTypes.NETWORK_ERROR) return BasisWebMedia.fail(s, 3, 2, 'mediaPlayer.web.format');
          if (data.response && data.response.code) return BasisWebMedia.fail(s, 1, 5, 'mediaPlayer.web.network');
          BasisWebMedia.probe(url).then(function (result) {
            if (s.hls === hls) BasisWebMedia.fail(s, 1, result === 'cors' ? 3 : 5, result === 'cors' ? 'mediaPlayer.web.cors' : 'mediaPlayer.web.network');
          });
        });
        hls.loadSource(url);
        hls.attachMedia(s.video);
        if (s.want) BasisWebMedia.play(s);
      });
    },

    whep: function (s, url) {
      if (typeof RTCPeerConnection === 'undefined') return BasisWebMedia.fail(s, 5, 2, 'mediaPlayer.web.unsupportedTransport');
      s.live = true;
      var pc = new RTCPeerConnection({ bundlePolicy: 'max-bundle' });
      var stream = new MediaStream();
      s.pc = pc;
      s.stream = stream;
      pc.addTransceiver('video', { direction: 'recvonly' });
      pc.addTransceiver('audio', { direction: 'recvonly' });
      pc.ontrack = function (e) {
        if (s.pc !== pc || !s.video) return;
        stream.addTrack(e.track);
        if (e.track.kind === 'audio' && !s.audioNode && BasisWebMedia.route(s)) {
          s.routedStream = true;
          s.video.muted = true;
        }
        if (!s.video.srcObject) {
          s.video.srcObject = stream;
          if (s.want) BasisWebMedia.play(s);
        }
      };
      pc.onconnectionstatechange = function () {
        if (s.pc === pc && pc.connectionState === 'failed') BasisWebMedia.fail(s, 1, 6, 'mediaPlayer.web.network');
      };
      pc.createOffer().then(function (offer) {
        return pc.setLocalDescription(offer);
      }).then(function () {
        return new Promise(function (done) {
          if (pc.iceGatheringState === 'complete') return done();
          var timer = setTimeout(done, 2000);
          pc.addEventListener('icegatheringstatechange', function () {
            if (pc.iceGatheringState !== 'complete') return;
            clearTimeout(timer);
            done();
          });
        });
      }).then(function () {
        return fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/sdp' }, body: pc.localDescription.sdp });
      }).then(function (response) {
        if (response.status !== 201 && response.status !== 200) throw new Error('WHEP answer ' + response.status);
        var location = response.headers.get('Location');
        if (location) s.resource = new URL(location, url).href;
        return response.text();
      }).then(function (sdp) {
        if (s.pc === pc) return pc.setRemoteDescription({ type: 'answer', sdp: sdp });
      }).catch(function (error) {
        if (s.pc !== pc) return;
        console.warn('[BasisWebMedia] WHEP: ' + error);
        BasisWebMedia.fail(s, 1, error && error.name === 'TypeError' ? 3 : 6, error && error.name === 'TypeError' ? 'mediaPlayer.web.cors' : 'mediaPlayer.web.network');
      });
    },

    start: function (s, url) {
      var match = /^([a-z][a-z0-9+.-]*):/i.exec(url), scheme = match ? match[1].toLowerCase() : '';
      if (!scheme || scheme.length === 1 || scheme === 'file') return BasisWebMedia.fail(s, 5, 1, 'mediaPlayer.web.localFile');
      var whep = scheme === 'whep' || scheme === 'wheps';
      if (whep) url = (scheme === 'whep' ? 'http' : 'https') + url.substring(scheme.length);
      else if (scheme !== 'http' && scheme !== 'https' && scheme !== 'blob' && scheme !== 'data') return BasisWebMedia.fail(s, 5, 2, 'mediaPlayer.web.unsupportedTransport');
      var parsed = null;
      try { parsed = new URL(url); } catch (e) { return BasisWebMedia.fail(s, 5, 4, 'mediaPlayer.web.network'); }
      if (/^https?:$/.test(parsed.protocol)) {
        if (!s.allowLocal && BasisWebMedia.blockedHost(parsed.hostname)) return BasisWebMedia.fail(s, 5, 3, 'mediaPlayer.web.localAddress');
        if (parsed.protocol === 'http:' && typeof location !== 'undefined' && location.protocol === 'https:' && !BasisWebMedia.blockedHost(parsed.hostname)) {
          parsed.protocol = 'https:';
          url = parsed.href;
        }
      }
      s.url = url;
      s.video = BasisWebMedia.element(s);
      if (whep) return BasisWebMedia.whep(s, url);
      var hls = /\.m3u8$/i.test(parsed.pathname) || /\.m3u8(\?|$)/i.test(parsed.search);
      var native = !!s.video.canPlayType('application/vnd.apple.mpegurl');
      if (hls && native && /Apple/.test(navigator.vendor || '')) s.direct = true;
      BasisWebMedia.route(s);
      if (hls && !native) return BasisWebMedia.loadHls(s, url);
      s.video.src = url;
      if (s.want) BasisWebMedia.play(s);
    },

    stateOf: function (s) {
      var v = s.video;
      if (!v || v.readyState < 1) return 1;
      if (v.ended) return 5;
      if (v.seeking || (s.want && (s.waiting || s.playPending))) return 2;
      return v.paused ? 4 : 3;
    },

    sync: function (s, now) {
      var v = s.video, rate = 1;
      if (s.syncTarget >= 0 && !s.live && !v.paused && !v.seeking && v.readyState >= 3) {
        var target = s.syncTarget + (now - s.syncAt) / 1000, drift = target - v.currentTime;
        if (Math.abs(drift) > 2 && isFinite(v.duration) && target < v.duration) {
          v.currentTime = target;
          BasisWebMedia.event(s, 17, 7, 'sync seek to ' + Math.round(target * 1e6) + 'us, drift ' + Math.round(drift * 1000) + 'ms');
        } else if (Math.abs(drift) > (s.slewing ? 0.03 : 0.08)) {
          rate = 1 + Math.max(-0.05, Math.min(0.05, drift * 0.1));
          s.slewing = true;
        } else {
          s.slewing = false;
        }
      } else {
        s.slewing = false;
      }
      if (Math.abs(v.playbackRate - rate) > 0.0005) v.playbackRate = rate;
      s.ppm = Math.round((rate - 1) * 1e6);
    },

    upload: function (s, source, width, height) {
      var gl = typeof GLctx !== 'undefined' ? GLctx : null, texture = gl && GL.textures ? GL.textures[s.texture] : null;
      if (!texture) return false;
      var gl2 = typeof WebGL2RenderingContext !== 'undefined' && gl instanceof WebGL2RenderingContext;
      var bound = gl.getParameter(gl.TEXTURE_BINDING_2D), flip = gl.getParameter(gl.UNPACK_FLIP_Y_WEBGL), premultiply = gl.getParameter(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL);
      var unpack = gl2 ? gl.getParameter(gl.PIXEL_UNPACK_BUFFER_BINDING) : null, row = gl2 ? gl.getParameter(gl.UNPACK_ROW_LENGTH) : 0, skipRows = gl2 ? gl.getParameter(gl.UNPACK_SKIP_ROWS) : 0, skipPixels = gl2 ? gl.getParameter(gl.UNPACK_SKIP_PIXELS) : 0;
      gl.bindTexture(gl.TEXTURE_2D, texture);
      if (flip) gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
      if (premultiply) gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
      if (unpack) gl.bindBuffer(gl.PIXEL_UNPACK_BUFFER, null);
      if (row) gl.pixelStorei(gl.UNPACK_ROW_LENGTH, 0);
      if (skipRows) gl.pixelStorei(gl.UNPACK_SKIP_ROWS, 0);
      if (skipPixels) gl.pixelStorei(gl.UNPACK_SKIP_PIXELS, 0);
      var ok = true, input = source;
      for (var attempt = 0; attempt < 2; attempt++) {
        try {
          if (s.viaCanvas && typeof VideoFrame !== 'undefined' && source instanceof VideoFrame) input = BasisWebMedia.draw(s, source, width, height);
          if (gl2) gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, width, height, gl.RGBA, gl.UNSIGNED_BYTE, input);
          else gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, gl.RGBA, gl.UNSIGNED_BYTE, input);
          ok = true;
          break;
        } catch (e) {
          ok = false;
          if (e && e.name === 'SecurityError') {
            BasisWebMedia.fail(s, 4, 1, 'mediaPlayer.web.cors');
            break;
          }
          if (s.viaCanvas || typeof VideoFrame === 'undefined' || !(source instanceof VideoFrame)) {
            console.warn('[BasisWebMedia] texture upload failed: ' + e);
            break;
          }
          s.viaCanvas = true;
        }
      }
      if (ok && s.presented === 0) {
        var error = gl.getError();
        if (error) console.warn('[BasisWebMedia] first texture upload reported GL error ' + error);
      }
      if (flip) gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
      if (premultiply) gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
      if (unpack) gl.bindBuffer(gl.PIXEL_UNPACK_BUFFER, unpack);
      if (row) gl.pixelStorei(gl.UNPACK_ROW_LENGTH, row);
      if (skipRows) gl.pixelStorei(gl.UNPACK_SKIP_ROWS, skipRows);
      if (skipPixels) gl.pixelStorei(gl.UNPACK_SKIP_PIXELS, skipPixels);
      gl.bindTexture(gl.TEXTURE_2D, bound);
      return ok;
    },

    draw: function (s, frame, width, height) {
      if (!s.canvas || s.canvas.width !== width || s.canvas.height !== height) {
        s.canvas = typeof OffscreenCanvas !== 'undefined' ? new OffscreenCanvas(width, height) : document.createElement('canvas');
        s.canvas.width = width;
        s.canvas.height = height;
        s.canvasContext = s.canvas.getContext('2d');
      }
      s.canvasContext.drawImage(frame, 0, 0, width, height);
      return s.canvas;
    },

    offer: function (s, frame) {
      if (!s || s.state === 6) {
        frame.close();
        return;
      }
      if (s.frame) s.frame.close();
      s.frame = frame;
      s.width = frame.displayWidth;
      s.height = frame.displayHeight;
      s.decoded++;
      if (s.firstPts < 0) s.firstPts = frame.timestamp;
      s.lastPts = frame.timestamp;
    },

    codec: function (data) {
      for (var i = 0; i + 4 < data.length; i++) {
        if (data[i] !== 0 || data[i + 1] !== 0) continue;
        var at = data[i + 2] === 1 ? i + 3 : data[i + 2] === 0 && data[i + 3] === 1 ? i + 4 : -1;
        if (at < 0 || at + 3 >= data.length) continue;
        if ((data[at] & 0x1f) !== 7) {
          i = at;
          continue;
        }
        var hex = function (value) { return (value < 16 ? '0' : '') + value.toString(16); };
        return 'avc1.' + hex(data[at + 1]) + hex(data[at + 2]) + hex(data[at + 3]);
      }
      return null;
    },

    configure: function (s, codec, width, height) {
      if (s.decoder && s.decoder.state !== 'closed') try { s.decoder.close(); } catch (e) {}
      var decoder = new VideoDecoder({
        output: function (frame) {
          if (s.decoder === decoder) BasisWebMedia.offer(s, frame);
          else frame.close();
        },
        error: function (error) {
          if (s.decoder !== decoder) return;
          console.warn('[BasisWebMedia] video decoder: ' + error);
          s.decoder = null;
          s.needKey = true;
        }
      });
      var config = { codec: codec, optimizeForLatency: true };
      if (width > 0 && height > 0) {
        config.codedWidth = width;
        config.codedHeight = height;
      }
      try {
        decoder.configure(config);
      } catch (e) {
        console.warn('[BasisWebMedia] ' + codec + ' is not supported: ' + e);
        return false;
      }
      s.decoder = decoder;
      s.codec = codec;
      s.codedWidth = width;
      s.codedHeight = height;
      return true;
    },

    pushVideo: function (s, data, pts, key, width, height) {
      if (typeof VideoDecoder === 'undefined') return 4;
      if (key) {
        var codec = BasisWebMedia.codec(data) || s.codec || 'avc1.42e01f';
        if ((!s.decoder || s.decoder.state === 'closed' || codec !== s.codec || width !== s.codedWidth || height !== s.codedHeight) && !BasisWebMedia.configure(s, codec, width, height)) return 4;
      }
      if (!s.decoder || s.decoder.state !== 'configured' || (s.needKey && !key)) return 1;
      if (!key && s.decoder.decodeQueueSize > 8) {
        s.needKey = true;
        return 1;
      }
      try {
        s.decoder.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp: pts, data: data }));
      } catch (e) {
        s.needKey = true;
        return 1;
      }
      s.needKey = false;
      return 0;
    },

    pushAudio: function (s, data, pts, rate, channels) {
      var context = BasisWebMedia.context();
      if (typeof AudioDecoder === 'undefined' || !context) return 0;
      if (!s.audioDecoder || s.audioDecoder.state === 'closed' || s.audioRate !== rate || s.audioChannels !== channels) {
        if (s.audioDecoder && s.audioDecoder.state !== 'closed') try { s.audioDecoder.close(); } catch (e) {}
        var decoder = new AudioDecoder({
          output: function (audio) { BasisWebMedia.schedule(s, audio); },
          error: function (error) {
            console.warn('[BasisWebMedia] audio decoder: ' + error);
            if (s.audioDecoder === decoder) s.audioDecoder = null;
          }
        });
        try {
          decoder.configure({ codec: 'opus', sampleRate: rate, numberOfChannels: channels });
        } catch (e) {
          return 0;
        }
        s.audioDecoder = decoder;
        s.audioRate = rate;
        s.audioChannels = channels;
      }
      try { s.audioDecoder.decode(new EncodedAudioChunk({ type: 'key', timestamp: pts, data: data })); } catch (e) {}
      return 0;
    },

    schedule: function (s, audio) {
      var context = BasisWebMedia.context();
      if (!context || context.state !== 'running' || s.state === 6) {
        audio.close();
        return;
      }
      if (!s.gainNode) {
        s.gainNode = context.createGain();
        s.gainNode.gain.value = s.gain;
        s.gainNode.connect(context.destination);
      }
      var frames = audio.numberOfFrames, channels = audio.numberOfChannels, buffer = context.createBuffer(channels, frames, audio.sampleRate), plane = new Float32Array(frames);
      for (var c = 0; c < channels; c++) {
        audio.copyTo(plane, { planeIndex: c, format: 'f32-planar' });
        buffer.copyToChannel(plane, c);
      }
      audio.close();
      var node = context.createBufferSource(), now = context.currentTime;
      node.buffer = buffer;
      node.connect(s.gainNode);
      if (s.audioAt < now + 0.01 || s.audioAt > now + 0.4) s.audioAt = now + 0.08;
      node.start(s.audioAt);
      s.audioAt += buffer.duration;
    }
  },

  BasisWebMedia_Supported__deps: ['$BasisWebMedia'],
  BasisWebMedia_Supported: function () {
    return 1 | (typeof VideoDecoder !== 'undefined' && typeof EncodedVideoChunk !== 'undefined' ? 2 : 0) | (typeof AudioDecoder !== 'undefined' ? 4 : 0);
  },

  BasisWebMedia_Open__deps: ['$BasisWebMedia'],
  BasisWebMedia_Open: function (descriptor, length) {
    var d;
    try { d = JSON.parse(new TextDecoder().decode(BasisWebMedia.bytes(descriptor, length))); } catch (e) { return -2; }
    var id = BasisWebMedia.next++;
    var s = { id: id, kind: 'element', state: 1, want: true, errorCode: 0, category: 0, events: [], opened: performance.now(), video: null, hls: null, pc: null, stream: null, resource: null, texture: 0, dirty: false, rvfc: false, lastTime: -1, presented: 0, decoded: 0, width: 0, height: 0, live: false, hlsLive: false, liveHint: d.liveness === 'live' ? 1 : d.liveness === 'vod' ? 2 : 0, allowLocal: !!d.allow_local_addresses, seekUs: -1, syncTarget: -1, syncAt: 0, slewing: false, ppm: 0, gain: 1, gainNode: null, audioNode: null, direct: false, routedStream: false, autoMuted: false, cors: true, playPending: false, waiting: false, frame: null, decoder: null, codec: '', codedWidth: 0, codedHeight: 0, needKey: true, audioDecoder: null, audioAt: 0, firstPts: -1, lastPts: 0 };
    BasisWebMedia.sessions[id] = s;
    var url = String(d.url || '');
    if (/^basis-push:\/\//i.test(url)) {
      s.kind = 'push';
      s.state = 2;
      return id;
    }
    try { BasisWebMedia.start(s, url); } catch (e) {
      console.warn('[BasisWebMedia] open failed: ' + e);
      BasisWebMedia.fail(s, 6, 1, 'mediaPlayer.web.network');
    }
    return id;
  },

  BasisWebMedia_Close__deps: ['$BasisWebMedia'],
  BasisWebMedia_Close: function (id) {
    var s = BasisWebMedia.sessions[id];
    if (!s) return;
    s.state = 6;
    BasisWebMedia.halt(s);
    delete BasisWebMedia.sessions[id];
  },

  BasisWebMedia_Poll__deps: ['$BasisWebMedia'],
  BasisWebMedia_Poll: function (id, output) {
    var s = BasisWebMedia.sessions[id];
    if (!s) return -1;
    var v = s.video, position = 0, duration = 0, banked = 0, decoded = s.decoded, width = s.width, height = s.height;
    if (s.kind === 'element' && v) {
      if (s.state !== 6) {
        s.state = BasisWebMedia.stateOf(s);
        BasisWebMedia.sync(s, performance.now());
      }
      width = s.cors ? v.videoWidth : 0;
      height = s.cors ? v.videoHeight : 0;
      position = v.currentTime || 0;
      duration = s.live || !isFinite(v.duration) ? 0 : v.duration;
      for (var i = 0; i < v.buffered.length; i++) if (v.buffered.start(i) <= position && v.buffered.end(i) >= position) banked = v.buffered.end(i) - position;
      if (v.getVideoPlaybackQuality) decoded = v.getVideoPlaybackQuality().totalVideoFrames;
    } else if (s.kind === 'push') {
      if (s.state !== 6) s.state = s.width > 0 ? 3 : 2;
      if (s.firstPts >= 0) position = (s.lastPts - s.firstPts) / 1e6;
    }
    BasisWebMedia.putDoubles(output, [s.state, width, height, Math.round(position * 1e6), Math.round(duration * 1e6), decoded, s.presented, s.errorCode, s.category, Math.round(banked * 1000), s.ppm]);
    return 0;
  },

  BasisWebMedia_Play__deps: ['$BasisWebMedia'],
  BasisWebMedia_Play: function (id) {
    var s = BasisWebMedia.sessions[id];
    if (!s || s.state === 6) return;
    s.want = true;
    if (s.video && (s.video.paused || s.video.ended)) BasisWebMedia.play(s);
  },

  BasisWebMedia_Pause__deps: ['$BasisWebMedia'],
  BasisWebMedia_Pause: function (id) {
    var s = BasisWebMedia.sessions[id];
    if (!s || s.state === 6) return;
    s.want = false;
    if (s.video) s.video.pause();
  },

  BasisWebMedia_Seek__deps: ['$BasisWebMedia'],
  BasisWebMedia_Seek: function (id, seconds) {
    var s = BasisWebMedia.sessions[id];
    if (!s || s.state === 6 || !s.video) return;
    var v = s.video, us = Math.round(seconds * 1e6);
    if (s.live) {
      BasisWebMedia.event(s, 10, 1, 'to ' + us + 'us, refused: live source');
      return;
    }
    if (isFinite(v.duration) && v.duration > 0) seconds = Math.min(seconds, v.duration);
    s.seekUs = us;
    v.currentTime = Math.max(0, seconds);
    if (v.readyState === 0) {
      BasisWebMedia.event(s, 10, 1, 'to ' + us + 'us, landed before load');
      s.seekUs = -1;
    }
    if (s.want && (v.paused || v.ended)) BasisWebMedia.play(s);
  },

  BasisWebMedia_SetSyncTarget__deps: ['$BasisWebMedia'],
  BasisWebMedia_SetSyncTarget: function (id, seconds) {
    var s = BasisWebMedia.sessions[id];
    if (!s) return;
    s.syncTarget = seconds;
    s.syncAt = performance.now();
  },

  BasisWebMedia_SetGain__deps: ['$BasisWebMedia'],
  BasisWebMedia_SetGain: function (id, gain) {
    var s = BasisWebMedia.sessions[id];
    if (!s || s.gain === gain) return;
    s.gain = gain;
    if (s.gainNode) s.gainNode.gain.value = gain;
    else if (s.video) s.video.volume = Math.max(0, Math.min(1, gain));
  },

  BasisWebMedia_SetTexture__deps: ['$BasisWebMedia'],
  BasisWebMedia_SetTexture: function (id, texture) {
    var s = BasisWebMedia.sessions[id];
    if (!s) return;
    s.texture = texture;
    s.dirty = true;
  },

  BasisWebMedia_Present__deps: ['$BasisWebMedia'],
  BasisWebMedia_Present: function (id, width, height) {
    var s = BasisWebMedia.sessions[id];
    if (!s || !s.texture || s.state === 6 || width <= 0 || height <= 0) return 0;
    var source;
    if (s.kind === 'push') {
      source = s.frame;
      if (!source || source.displayWidth !== width || source.displayHeight !== height) return 0;
      s.frame = null;
    } else {
      var v = s.video;
      if (!v || !s.cors || v.readyState < 2 || v.videoWidth !== width || v.videoHeight !== height) return 0;
      if (!s.dirty && s.presented > 0 && (s.rvfc || v.currentTime === s.lastTime)) return 0;
      s.dirty = false;
      s.lastTime = v.currentTime;
      source = v;
    }
    var ok = BasisWebMedia.upload(s, source, width, height);
    if (s.kind === 'push') source.close();
    if (!ok) return 0;
    s.presented++;
    return 1;
  },

  BasisWebMedia_NextEvent__deps: ['$BasisWebMedia'],
  BasisWebMedia_NextEvent: function (id, detail, capacity, info) {
    var s = BasisWebMedia.sessions[id];
    if (!s || !s.events.length) return -1;
    var e = s.events.shift(), bytes = new TextEncoder().encode(e.detail), count = Math.min(bytes.length, capacity), at = BasisWebMedia.ptr(info) >> 2;
    HEAPU8.set(bytes.subarray(0, count), BasisWebMedia.ptr(detail));
    HEAP32[at] = e.code;
    HEAP32[at + 1] = e.stage;
    HEAP32[at + 2] = Math.round(e.at);
    return count;
  },

  BasisWebMedia_PushVideo__deps: ['$BasisWebMedia'],
  BasisWebMedia_PushVideo: function (id, data, length, pts, key, width, height) {
    var s = BasisWebMedia.sessions[id];
    if (!s || s.kind !== 'push' || s.state === 6 || length <= 0) return -2;
    return BasisWebMedia.pushVideo(s, BasisWebMedia.bytes(data, length), pts, key !== 0, width, height);
  },

  BasisWebMedia_PushAudio__deps: ['$BasisWebMedia'],
  BasisWebMedia_PushAudio: function (id, data, length, pts, rate, channels) {
    var s = BasisWebMedia.sessions[id];
    if (!s || s.kind !== 'push' || s.state === 6 || length <= 0) return -2;
    return BasisWebMedia.pushAudio(s, BasisWebMedia.bytes(data, length), pts, rate, channels);
  }
};

autoAddDeps(BasisWebMediaLibrary, '$BasisWebMedia');
mergeInto(LibraryManager.library, BasisWebMediaLibrary);
