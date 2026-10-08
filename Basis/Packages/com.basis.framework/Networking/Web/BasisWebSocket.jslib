var BasisWebSocketLibrary = {
  $BasisWS: {
    sockets: {},
    nextId: 1,
    shedBytes: 4 * 1024 * 1024,
    maxBytes: 64 * 1024 * 1024,
    staleMs: 1000,

    ptr: function (value) {
      return typeof value === 'bigint' ? Number(value) : value;
    },

    readVarUInt: function (bytes, offset, end) {
      var value = 0;
      var shift = 0;
      for (var index = 0; index < 5; index++) {
        if (offset >= end) return null;
        var current = bytes[offset++];
        value += (current & 0x7F) * Math.pow(2, shift);
        if ((current & 0x80) === 0) return { value: value, next: offset };
        shift += 7;
      }
      return null;
    },

    utcTicks: function () {
      return BigInt(Date.now()) * BigInt(10000) + BigInt('621355968000000000');
    },

    pong: function (state, bytes, bodyOffset) {
      if (!state.socket || state.socket.readyState !== 1) return;
      var frame = new Uint8Array(18);
      frame[0] = 17;
      frame[1] = 6;
      frame.set(bytes.subarray(bodyOffset, bodyOffset + 8), 2);
      new DataView(frame.buffer).setBigInt64(10, BasisWS.utcTicks(), true);
      try {
        state.socket.send(frame);
      } catch (e) {
      }
    },

    receive: function (state, bytes) {
      var shedding = state.queuedBytes > BasisWS.shedBytes || (performance.now() - state.lastPoll) > BasisWS.staleMs;
      var keep = null;
      var offset = 0;
      var end = bytes.length;
      while (offset < end) {
        var header = BasisWS.readVarUInt(bytes, offset, end);
        if (header === null || header.value < 1 || header.next + header.value > end) {
          keep = null;
          break;
        }
        var frameStart = offset;
        var kindAt = header.next;
        var frameEnd = kindAt + header.value;
        var kind = bytes[kindAt];
        var drop = false;
        if (kind === 5 && header.value >= 9) {
          BasisWS.pong(state, bytes, kindAt + 1);
          drop = true;
        } else if (shedding && kind === 0 && header.value >= 3) {
          var method = bytes[kindAt + 2];
          drop = method === 4 || method === 1;
        }
        if (drop) {
          if (keep === null) {
            keep = [];
            if (frameStart > 0) keep.push(bytes.subarray(0, frameStart));
          }
        } else if (keep !== null) {
          keep.push(bytes.subarray(frameStart, frameEnd));
        }
        offset = frameEnd;
      }
      var message = bytes;
      if (keep !== null) {
        var total = 0;
        for (var part = 0; part < keep.length; part++) total += keep[part].length;
        if (total === 0) return;
        message = new Uint8Array(total);
        var cursor = 0;
        for (var piece = 0; piece < keep.length; piece++) {
          message.set(keep[piece], cursor);
          cursor += keep[piece].length;
        }
      }
      state.queue.push({ type: 2, data: message });
      state.queuedBytes += message.length;
      if (state.queuedBytes > BasisWS.maxBytes) {
        try {
          state.socket.close(1009, 'client fell behind');
        } catch (e) {
        }
      }
    }
  },

  BasisWS_Open: function (urlPtr, protocolPtr) {
    var url = UTF8ToString(BasisWS.ptr(urlPtr));
    var protocol = UTF8ToString(BasisWS.ptr(protocolPtr));
    if (typeof location !== 'undefined' && location.protocol === 'https:' && url.indexOf('ws://') === 0) {
      var host = url.substring(5).split('/')[0].split(':')[0];
      if (host !== 'localhost' && host !== '127.0.0.1' && host !== '[::1]') url = 'wss://' + url.substring(5);
    }
    var id = BasisWS.nextId++;
    var state = { socket: null, queue: [], queuedBytes: 0, closeCode: 0, lastPoll: performance.now() };
    BasisWS.sockets[id] = state;
    try {
      var socket = protocol.length > 0 ? new WebSocket(url, [protocol]) : new WebSocket(url);
      socket.binaryType = 'arraybuffer';
      socket.onopen = function () {
        state.queue.push({ type: 1 });
      };
      socket.onmessage = function (event) {
        if (event.data instanceof ArrayBuffer) BasisWS.receive(state, new Uint8Array(event.data));
      };
      socket.onerror = function () {
        state.queue.push({ type: 4 });
      };
      socket.onclose = function (event) {
        state.closeCode = event.code;
        state.queue.push({ type: 3 });
      };
      state.socket = socket;
    } catch (e) {
      state.queue.push({ type: 4 });
      state.queue.push({ type: 3 });
    }
    return id;
  },

  BasisWS_PeekType: function (id) {
    var state = BasisWS.sockets[id];
    if (!state) return 3;
    state.lastPoll = performance.now();
    return state.queue.length === 0 ? 0 : state.queue[0].type;
  },

  BasisWS_PeekSize: function (id) {
    var state = BasisWS.sockets[id];
    if (!state || state.queue.length === 0 || state.queue[0].type !== 2) return 0;
    return state.queue[0].data.length;
  },

  BasisWS_Take: function (id, bufferPtr, capacity) {
    var state = BasisWS.sockets[id];
    if (!state || state.queue.length === 0) return 0;
    var item = state.queue[0];
    if (item.type !== 2) {
      state.queue.shift();
      return 0;
    }
    if (item.data.length > capacity) return -1;
    state.queue.shift();
    state.queuedBytes -= item.data.length;
    HEAPU8.set(item.data, BasisWS.ptr(bufferPtr));
    return item.data.length;
  },

  BasisWS_Send: function (id, bufferPtr, offset, length) {
    var state = BasisWS.sockets[id];
    if (!state || !state.socket || state.socket.readyState !== 1) return -1;
    var start = BasisWS.ptr(bufferPtr) + offset;
    try {
      state.socket.send(HEAPU8.slice(start, start + length));
    } catch (e) {
      return -1;
    }
    return length;
  },

  BasisWS_BufferedAmount: function (id) {
    var state = BasisWS.sockets[id];
    return state && state.socket ? state.socket.bufferedAmount : 0;
  },

  BasisWS_CloseCode: function (id) {
    var state = BasisWS.sockets[id];
    return state ? state.closeCode : 0;
  },

  BasisWS_Close: function (id, code) {
    var state = BasisWS.sockets[id];
    if (!state || !state.socket) return;
    try {
      if (state.socket.readyState === 0 || state.socket.readyState === 1) state.socket.close(code);
    } catch (e) {
    }
  },

  BasisWS_Free: function (id) {
    var state = BasisWS.sockets[id];
    if (!state) return;
    if (state.socket) {
      state.socket.onopen = null;
      state.socket.onmessage = null;
      state.socket.onerror = null;
      state.socket.onclose = null;
      try {
        if (state.socket.readyState === 0 || state.socket.readyState === 1) state.socket.close(1000);
      } catch (e) {
      }
    }
    delete BasisWS.sockets[id];
  }
};

autoAddDeps(BasisWebSocketLibrary, '$BasisWS');
mergeInto(LibraryManager.library, BasisWebSocketLibrary);
