var BasisWebLipSyncLibrary = {
  $BasisWebLipSync__deps: ['$BasisWebLipSyncKernel'],
  $BasisWebLipSync: {
    state: 0,
    worker: null,
    sessions: {},
    next: 1,
    pending: [],
    frames: 0,
    busy: 0,
    dropped: 0,
    limit: 100,
    backlog: 200,

    ptr: function (value) {
      return typeof value === 'bigint' ? Number(value) : value;
    },

    read: function (bytes) {
      var pos = 0, tensors = {}, inputs = {};
      function varint() {
        var value = 0, scale = 1, byte;
        do {
          byte = bytes[pos++];
          value += (byte & 127) * scale;
          scale *= 128;
        } while (byte & 128);
        return value;
      }
      function text(start, stop) {
        return new TextDecoder().decode(bytes.subarray(start, stop));
      }
      function message(end, visit) {
        while (pos < end) {
          var key = varint(), field = Math.floor(key / 8), wire = key & 7;
          if (wire === 0) {
            visit(field, 0, varint(), 0);
          } else if (wire === 2) {
            var length = varint(), stop = pos + length;
            visit(field, 2, pos, stop);
            pos = stop;
          } else if (wire === 1) {
            pos += 8;
          } else if (wire === 5) {
            pos += 4;
          } else {
            throw new Error('unsupported protobuf wire type ' + wire);
          }
        }
      }
      message(bytes.length, function (field, wire, start, stop) {
        if (field !== 7 || wire !== 2) return;
        message(stop, function (field, wire, start, stop) {
          if (wire !== 2) return;
          if (field === 5) {
            var tensor = { name: '', dims: [], type: 0, start: 0, end: 0 };
            message(stop, function (field, wire, a, b) {
              if (field === 1 && wire === 0) tensor.dims.push(a);
              else if (field === 1) while (pos < b) tensor.dims.push(varint());
              else if (field === 2 && wire === 0) tensor.type = a;
              else if (field === 8 && wire === 2) tensor.name = text(a, b);
              else if ((field === 9 || field === 4) && wire === 2) { tensor.start = a; tensor.end = b; }
            });
            tensors[tensor.name] = tensor;
          } else if (field === 11) {
            var input = { name: '', dims: [] };
            message(stop, function (field, wire, a, b) {
              if (field === 1 && wire === 2) input.name = text(a, b);
              else if (field === 2 && wire === 2) message(b, function (field, wire, a, b) {
                if (field === 1 && wire === 2) message(b, function (field, wire, a, b) {
                  if (field === 2 && wire === 2) message(b, function (field, wire, a, b) {
                    if (field !== 1 || wire !== 2) return;
                    var dim = -1;
                    message(b, function (field, wire, value) { if (field === 1 && wire === 0) dim = value; });
                    input.dims.push(dim);
                  });
                });
              });
            });
            inputs[input.name] = input.dims;
          }
        });
      });
      return { tensors: tensors, inputs: inputs };
    },

    weights: function (bytes) {
      var model = BasisWebLipSync.read(bytes), tensors = model.tensors, dilations = [1, 2, 4, 8, 16, 32, 1, 2, 4, 8];
      for (var c = 0; c < dilations.length; c++) {
        var cache = model.inputs['cache_' + c];
        if (!cache || cache.join() !== [1, 256, 2 * dilations[c]].join()) throw new Error('cache_' + c + ' is [' + cache + '], the web kernel expects [1,256,' + 2 * dilations[c] + ']');
      }
      var plan = [['m.cmvn.mean', [80]], ['m.cmvn.std', [80]], ['m.input_proj.weight', [256, 80, 1]], ['m.input_proj.bias', [256]]];
      for (var b = 0; b < dilations.length; b++) {
        var p = 'm.blocks.' + b + '.';
        plan.push([p + 'norm.weight', [256]], [p + 'norm.bias', [256]], [p + 'dw.weight', [256, 1, 3]], [p + 'dw.bias', [256]], [p + 'pw1.weight', [512, 256, 1]], [p + 'pw1.bias', [512]], [p + 'pw2.weight', [256, 512, 1]], [p + 'pw2.bias', [256]]);
      }
      var head = Object.keys(tensors).filter(function (name) { return tensors[name].dims.join() === '256,15'; });
      if (head.length !== 1) throw new Error('expected one 256x15 head weight, found ' + head.length);
      plan.push(['m.out_norm.weight', [256]], ['m.out_norm.bias', [256]], ['m.head.bias', [15]], [head[0], [256, 15]]);
      var size = function (dims) { return dims.reduce(function (a, b) { return a * b; }, 1); };
      var out = new Float32Array(plan.reduce(function (sum, entry) { return sum + size(entry[1]); }, 0)), offset = 0;
      plan.forEach(function (entry) {
        var tensor = tensors[entry[0]], count = size(entry[1]);
        if (!tensor) throw new Error('the model has no ' + entry[0]);
        if (tensor.type !== 1 || tensor.dims.join() !== entry[1].join() || tensor.end - tensor.start !== count * 4) throw new Error(entry[0] + ' is [' + tensor.dims + '], the web kernel expects [' + entry[1] + ']');
        new Uint8Array(out.buffer, offset * 4, count * 4).set(bytes.subarray(tensor.start, tensor.end));
        offset += count;
      });
      return out;
    },

    source: [
      "var kernel = null, net = 0, sessions = {}, room = { frames: 0, entries: 0, mel: 0, logits: 0, list: 0, counts: 0 };",
      "function fail(error) { net = 0; postMessage({ type: 'failed', error: String(error && error.stack || error) }); }",
      "function grow(frames, entries) {",
      "  if (frames > room.frames) { if (room.mel) { kernel.free(room.mel); kernel.free(room.logits); } room.frames = Math.max(frames, room.frames * 2, 64); room.mel = kernel.malloc(room.frames * 320); room.logits = kernel.malloc(room.frames * 60); }",
      "  if (entries > room.entries) { if (room.list) { kernel.free(room.list); kernel.free(room.counts); } room.entries = Math.max(entries, room.entries * 2, 16); room.list = kernel.malloc(room.entries * 4); room.counts = kernel.malloc(room.entries * 4); }",
      "  if (!room.mel || !room.logits || !room.list || !room.counts) throw new Error('out of kernel memory');",
      "}",
      "function step(m) {",
      "  var started = performance.now(), count = m.ids.length, frames = m.mel.length / 80;",
      "  grow(frames, count);",
      "  for (var k = 0; k < count; k++) {",
      "    var session = sessions[m.ids[k]];",
      "    if (!session) { session = kernel.basis_ols_session_create(); if (!session) throw new Error('out of kernel memory'); sessions[m.ids[k]] = session; }",
      "    var words = new Int32Array(kernel.memory.buffer);",
      "    words[(room.list >> 2) + k] = session;",
      "    words[(room.counts >> 2) + k] = m.counts[k];",
      "  }",
      "  new Float32Array(kernel.memory.buffer, room.mel, m.mel.length).set(m.mel);",
      "  if (kernel.basis_ols_step(net, room.list, room.counts, count, room.mel, room.logits) !== frames) throw new Error('the kernel rejected a batch');",
      "  var logits = new Float32Array(kernel.memory.buffer, room.logits, frames * 15).slice();",
      "  postMessage({ type: 'result', ids: m.ids, counts: m.counts, epochs: m.epochs, logits: logits, ms: performance.now() - started }, [m.ids.buffer, m.counts.buffer, m.epochs.buffer, logits.buffer]);",
      "}",
      "onmessage = function (event) {",
      "  var m = event.data;",
      "  try {",
      "    if (m.type === 'step') { if (net) step(m); }",
      "    else if (m.type === 'reset') { if (sessions[m.id]) kernel.basis_ols_session_reset(sessions[m.id]); }",
      "    else if (m.type === 'destroy') { if (sessions[m.id]) { kernel.basis_ols_session_destroy(sessions[m.id]); delete sessions[m.id]; } }",
      "    else if (m.type === 'init') {",
      "      WebAssembly.instantiate(m.kernel, { env: { emscripten_notify_memory_growth: function () {} } }).then(function (result) {",
      "        kernel = result.instance.exports;",
      "        kernel._initialize();",
      "        var count = kernel.basis_ols_weight_count();",
      "        if (count !== m.weights.length) throw new Error('the model has ' + m.weights.length + ' weights, the kernel expects ' + count);",
      "        var at = kernel.malloc(count * 4);",
      "        if (!at) throw new Error('out of kernel memory');",
      "        new Float32Array(kernel.memory.buffer, at, count).set(m.weights);",
      "        net = kernel.basis_ols_net_create(at, count);",
      "        kernel.free(at);",
      "        if (!net) throw new Error('out of kernel memory');",
      "        postMessage({ type: 'ready' });",
      "      }).catch(fail);",
      "    }",
      "  } catch (error) { fail(error); }",
      "};"
    ].join('\n'),

    receive: function (event) {
      var m = event.data;
      if (m.type === 'result') {
        BasisWebLipSync.busy += m.ms;
        for (var k = 0, at = 0; k < m.ids.length; k++) {
          var frames = m.counts[k], session = BasisWebLipSync.sessions[m.ids[k]];
          BasisWebLipSync.frames += frames;
          if (session) {
            session.inflight -= frames;
            if (session.epoch === m.epochs[k]) {
              session.results.push(m.logits.subarray(at * 15, (at + frames) * 15));
              session.queued += frames;
              while (session.queued > BasisWebLipSync.backlog && session.results.length > 1) {
                session.queued -= session.results[0].length / 15 - session.offset;
                session.results.shift();
                session.offset = 0;
              }
            }
          }
          at += frames;
        }
      } else if (m.type === 'ready') {
        BasisWebLipSync.state = 2;
      } else if (m.type === 'failed') {
        BasisWebLipSync.state = 3;
        console.warn('[OpenLipSync] web inference worker failed: ' + m.error);
      }
    },

    start: function (bytes) {
      if (BasisWebLipSync.worker) BasisWebLipSync.worker.terminate();
      BasisWebLipSync.worker = null;
      BasisWebLipSync.sessions = {};
      BasisWebLipSync.pending = [];
      BasisWebLipSync.state = 1;
      try {
        var weights = BasisWebLipSync.weights(bytes);
        var encoded = atob(BasisWebLipSyncKernel()), kernel = new Uint8Array(encoded.length);
        for (var i = 0; i < encoded.length; i++) kernel[i] = encoded.charCodeAt(i);
        var url = URL.createObjectURL(new Blob([BasisWebLipSync.source], { type: 'application/javascript' }));
        var worker = new Worker(url);
        var release = function () { if (url) URL.revokeObjectURL(url); url = null; };
        worker.onmessage = function (event) { release(); if (BasisWebLipSync.worker === worker) BasisWebLipSync.receive(event); };
        worker.onerror = function (event) {
          release();
          if (event && event.preventDefault) event.preventDefault();
          if (BasisWebLipSync.worker !== worker) return;
          BasisWebLipSync.state = 3;
          console.warn('[OpenLipSync] web inference worker error: ' + (event && event.message || event));
        };
        worker.postMessage({ type: 'init', kernel: kernel, weights: weights }, [kernel.buffer, weights.buffer]);
        BasisWebLipSync.worker = worker;
      } catch (error) {
        BasisWebLipSync.state = 3;
        console.warn('[OpenLipSync] web lip sync is unavailable: ' + error);
      }
      return BasisWebLipSync.state;
    },

    drop: function (id) {
      BasisWebLipSync.pending = BasisWebLipSync.pending.filter(function (entry) { return entry.id !== id; });
    }
  },

  BasisWebLipSync_Initialize__deps: ['$BasisWebLipSync'],
  BasisWebLipSync_Initialize: function (model, length) {
    var start = BasisWebLipSync.ptr(model);
    return BasisWebLipSync.start(HEAPU8.slice(start, start + length));
  },

  BasisWebLipSync_State__deps: ['$BasisWebLipSync'],
  BasisWebLipSync_State: function () {
    return BasisWebLipSync.state;
  },

  BasisWebLipSync_CreateSession__deps: ['$BasisWebLipSync'],
  BasisWebLipSync_CreateSession: function () {
    if (BasisWebLipSync.state !== 2) return 0;
    var id = BasisWebLipSync.next++;
    BasisWebLipSync.sessions[id] = { epoch: 0, inflight: 0, waiting: 0, results: [], offset: 0, queued: 0 };
    return id;
  },

  BasisWebLipSync_DestroySession__deps: ['$BasisWebLipSync'],
  BasisWebLipSync_DestroySession: function (id) {
    if (!BasisWebLipSync.sessions[id]) return;
    delete BasisWebLipSync.sessions[id];
    BasisWebLipSync.drop(id);
    if (BasisWebLipSync.worker) BasisWebLipSync.worker.postMessage({ type: 'destroy', id: id });
  },

  BasisWebLipSync_ResetSession__deps: ['$BasisWebLipSync'],
  BasisWebLipSync_ResetSession: function (id) {
    var session = BasisWebLipSync.sessions[id];
    if (!session) return;
    session.epoch++;
    session.results = [];
    session.offset = 0;
    session.queued = 0;
    session.waiting = 0;
    BasisWebLipSync.drop(id);
    if (BasisWebLipSync.worker) BasisWebLipSync.worker.postMessage({ type: 'reset', id: id });
  },

  BasisWebLipSync_Push__deps: ['$BasisWebLipSync'],
  BasisWebLipSync_Push: function (id, mel, frames) {
    var session = BasisWebLipSync.sessions[id];
    if (!session || frames <= 0 || BasisWebLipSync.state !== 2) return 0;
    if (session.inflight + session.waiting + frames > BasisWebLipSync.limit) {
      BasisWebLipSync.dropped += frames;
      return 0;
    }
    var start = BasisWebLipSync.ptr(mel) / 4;
    BasisWebLipSync.pending.push({ id: id, frames: frames, mel: HEAPF32.slice(start, start + frames * 80) });
    session.waiting += frames;
    return frames;
  },

  BasisWebLipSync_Flush__deps: ['$BasisWebLipSync'],
  BasisWebLipSync_Flush: function () {
    var pending = BasisWebLipSync.pending;
    if (!pending.length || !BasisWebLipSync.worker || BasisWebLipSync.state !== 2) return 0;
    var groups = [], index = {}, total = 0;
    for (var i = 0; i < pending.length; i++) {
      var entry = pending[i], group = index[entry.id];
      if (!group) {
        group = index[entry.id] = { id: entry.id, frames: 0, parts: [] };
        groups.push(group);
      }
      group.frames += entry.frames;
      group.parts.push(entry.mel);
      total += entry.frames;
    }
    var ids = new Int32Array(groups.length), counts = new Int32Array(groups.length), epochs = new Int32Array(groups.length), mel = new Float32Array(total * 80);
    for (var k = 0, at = 0; k < groups.length; k++) {
      var session = BasisWebLipSync.sessions[groups[k].id];
      ids[k] = groups[k].id;
      counts[k] = groups[k].frames;
      epochs[k] = session.epoch;
      session.inflight += session.waiting;
      session.waiting = 0;
      for (var j = 0; j < groups[k].parts.length; j++) {
        mel.set(groups[k].parts[j], at);
        at += groups[k].parts[j].length;
      }
    }
    BasisWebLipSync.pending = [];
    BasisWebLipSync.worker.postMessage({ type: 'step', ids: ids, counts: counts, epochs: epochs, mel: mel }, [ids.buffer, counts.buffer, epochs.buffer, mel.buffer]);
    return total;
  },

  BasisWebLipSync_Take__deps: ['$BasisWebLipSync'],
  BasisWebLipSync_Take: function (id, logits, maxFrames) {
    var session = BasisWebLipSync.sessions[id];
    if (!session) return 0;
    var start = BasisWebLipSync.ptr(logits) / 4, copied = 0;
    while (copied < maxFrames && session.results.length) {
      var chunk = session.results[0], take = Math.min(chunk.length / 15 - session.offset, maxFrames - copied);
      HEAPF32.set(chunk.subarray(session.offset * 15, (session.offset + take) * 15), start + copied * 15);
      copied += take;
      session.offset += take;
      if (session.offset * 15 >= chunk.length) {
        session.results.shift();
        session.offset = 0;
      }
    }
    session.queued -= copied;
    return copied;
  },

  BasisWebLipSync_Stats__deps: ['$BasisWebLipSync'],
  BasisWebLipSync_Stats: function (output) {
    var start = BasisWebLipSync.ptr(output) / 4, inflight = 0;
    for (var id in BasisWebLipSync.sessions) inflight += BasisWebLipSync.sessions[id].inflight;
    HEAPF32[start] = BasisWebLipSync.frames;
    HEAPF32[start + 1] = BasisWebLipSync.busy;
    HEAPF32[start + 2] = BasisWebLipSync.dropped;
    HEAPF32[start + 3] = inflight;
    return BasisWebLipSync.state;
  }
};

autoAddDeps(BasisWebLipSyncLibrary, '$BasisWebLipSync');
mergeInto(LibraryManager.library, BasisWebLipSyncLibrary);
