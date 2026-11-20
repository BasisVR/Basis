Module['BasisWebXR'] = Module['BasisWebXR'] || (function () {
  var X = { state: 0, error: 0, supported: 0, data: 0, device: null, context: null, session: null, binding: null, layer: null, space: null, pipelines: {}, sampler: null, uniforms: [], eyeWidth: 0, eyeHeight: 0, savedMatch: undefined, savedSize: null, loop: null, loopRaf: null };
  var hasXR = typeof navigator !== 'undefined' && !!navigator.xr && !!navigator.gpu;
  if (!hasXR) {
    X.supported = 2;
    return X;
  }
  var requestAdapter = navigator.gpu.requestAdapter;
  navigator.gpu.requestAdapter = function (options) {
    var request = Object.assign({}, options || {});
    request.xrCompatible = true;
    return requestAdapter.call(navigator.gpu, request);
  };
  if (typeof GPUAdapter !== 'undefined') {
    var requestDevice = GPUAdapter.prototype.requestDevice;
    GPUAdapter.prototype.requestDevice = function () {
      return requestDevice.apply(this, arguments).then(function (device) {
        if (!X.device) X.device = device;
        return device;
      });
    };
  }
  if (typeof GPUCanvasContext !== 'undefined') {
    var configure = GPUCanvasContext.prototype.configure;
    GPUCanvasContext.prototype.configure = function (config) {
      var next = Object.assign({}, config);
      next.usage = (next.usage === undefined ? GPUTextureUsage.RENDER_ATTACHMENT : next.usage) | GPUTextureUsage.TEXTURE_BINDING;
      X.context = this;
      X.canvasFormat = next.format;
      return configure.call(this, next);
    };
  }
  function refreshSupport() {
    if (typeof XRGPUBinding === 'undefined') {
      X.supported = 2;
      return;
    }
    navigator.xr.isSessionSupported('immersive-vr').then(function (ok) {
      X.supported = ok ? 1 : 2;
    }, function () {
      X.supported = 2;
    });
  }
  refreshSupport();
  navigator.xr.addEventListener('devicechange', refreshSupport);
  if (typeof document !== 'undefined') document.addEventListener('visibilitychange', function () {
    if (document.visibilityState === 'visible') refreshSupport();
  });

  var shader = 'struct P { rect: vec4f, decode: vec4f };' +
    '@group(0) @binding(0) var source: texture_2d<f32>;' +
    '@group(0) @binding(1) var linearSampler: sampler;' +
    '@group(0) @binding(2) var<uniform> p: P;' +
    'struct V { @builtin(position) position: vec4f, @location(0) uv: vec2f };' +
    '@vertex fn vs(@builtin(vertex_index) i: u32) -> V { var o: V; let uv = vec2f(f32((i << 1u) & 2u), f32(i & 2u)); o.position = vec4f(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0); o.uv = uv; return o; }' +
    'fn toLinear(c: vec3f) -> vec3f { return select(pow((c + 0.055) / 1.055, vec3f(2.4)), c / 12.92, c <= vec3f(0.04045)); }' +
    '@fragment fn fs(v: V) -> @location(0) vec4f { let c = textureSampleLevel(source, linearSampler, p.rect.xy + v.uv * p.rect.zw, 0.0); if (p.decode.x > 0.5) { return vec4f(toLinear(c.rgb), 1.0); } return vec4f(c.rgb, 1.0); }';

  function heap() {
    return HEAPF32;
  }

  function write(index, value) {
    heap()[X.data / 4 + index] = value;
  }

  function writeArray(index, values) {
    heap().set(values, X.data / 4 + index);
  }

  function pipeline(format) {
    if (X.pipelines[format]) return X.pipelines[format];
    var module = X.device.createShaderModule({ code: shader });
    X.pipelines[format] = X.device.createRenderPipeline({ layout: 'auto', vertex: { module: module, entryPoint: 'vs' }, fragment: { module: module, entryPoint: 'fs', targets: [{ format: format }] }, primitive: { topology: 'triangle-list' } });
    return X.pipelines[format];
  }

  function conjugate(q) {
    return [-q[0], -q[1], -q[2], q[3]];
  }

  function multiply(a, b) {
    return [a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1], a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0], a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3], a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2]];
  }

  function rotate(q, v) {
    var r = multiply(multiply(q, [v[0], v[1], v[2], 0]), conjugate(q));
    return [r[0], r[1], r[2]];
  }

  function writePose(index, position, rotation) {
    writeArray(index, [position[0], position[1], -position[2]]);
    writeArray(index + 3, [-rotation[0], -rotation[1], rotation[2], rotation[3]]);
  }

  function vec(p) {
    return [p.x, p.y, p.z];
  }

  function quat(q) {
    return [q.x, q.y, q.z, q.w];
  }

  function button(gamepad, index, field) {
    var b = gamepad && gamepad.buttons[index];
    if (!b) return 0;
    if (field === 'touched') return b.touched || b.pressed ? 1 : 0;
    if (field === 'pressed') return b.pressed ? 1 : 0;
    return b.value;
  }

  function writeControllers(frame) {
    var seen = [false, false];
    var sources = X.session.inputSources;
    for (var i = 0; i < sources.length; i++) {
      var source = sources[i];
      var hand = source.handedness === 'left' ? 0 : source.handedness === 'right' ? 1 : -1;
      if (hand < 0) continue;
      var base = 60 + hand * 40;
      var grip = source.gripSpace ? frame.getPose(source.gripSpace, X.space) : null;
      var aim = frame.getPose(source.targetRaySpace, X.space);
      if (!grip && !aim) continue;
      seen[hand] = true;
      var gripPose = grip || aim, aimPose = aim || grip;
      write(base, 1);
      writePose(base + 1, vec(gripPose.transform.position), quat(gripPose.transform.orientation));
      writePose(base + 8, vec(aimPose.transform.position), quat(aimPose.transform.orientation));
      var g = source.gamepad;
      var axes = g ? g.axes : [];
      write(base + 15, button(g, 0, 'value'));
      write(base + 16, button(g, 1, 'value'));
      write(base + 17, axes.length > 3 ? axes[2] : 0);
      write(base + 18, axes.length > 3 ? -axes[3] : 0);
      write(base + 19, axes.length > 1 ? axes[0] : 0);
      write(base + 20, axes.length > 1 ? -axes[1] : 0);
      write(base + 21, button(g, 4, 'pressed'));
      write(base + 22, button(g, 5, 'pressed'));
      write(base + 23, button(g, 3, 'pressed'));
      write(base + 24, button(g, 2, 'pressed'));
      write(base + 25, button(g, 4, 'touched'));
      write(base + 26, button(g, 5, 'touched'));
      write(base + 27, button(g, 3, 'touched'));
      write(base + 28, button(g, 0, 'touched'));
      write(base + 29, button(g, 6, 'pressed'));
      write(base + 30, button(g, 0, 'pressed'));
      write(base + 31, button(g, 1, 'pressed'));
      write(base + 32, button(g, 2, 'touched'));
    }
    for (var h = 0; h < 2; h++) if (!seen[h]) write(60 + h * 40, 0);
  }

  function views(pose) {
    var left = null, right = null;
    for (var i = 0; i < pose.views.length; i++) {
      var view = pose.views[i];
      if (view.eye === 'right') right = view;
      else if (!left) left = view;
    }
    return right ? [left, right] : [left];
  }

  function onFrame(frame) {
    if (!X.data) return;
    var pose = frame.getViewerPose(X.space);
    if (!pose) {
      write(0, 0);
      return;
    }
    var eyes = views(pose);
    if (!X.eyeWidth) {
      var sub = X.binding.getViewSubImage(X.layer, eyes[0]);
      X.eyeWidth = sub.viewport.width;
      X.eyeHeight = sub.viewport.height;
    }
    var canvas = Module.canvas;
    if (canvas && (canvas.width !== X.eyeWidth * eyes.length || canvas.height !== X.eyeHeight)) {
      canvas.width = X.eyeWidth * eyes.length;
      canvas.height = X.eyeHeight;
    }
    var headPosition = vec(pose.transform.position), headRotation = quat(pose.transform.orientation), inverse = conjugate(headRotation);
    write(1, eyes.length);
    write(2, X.eyeWidth);
    write(3, X.eyeHeight);
    writePose(36, headPosition, headRotation);
    for (var e = 0; e < 2; e++) {
      var view = eyes[Math.min(e, eyes.length - 1)];
      var p = vec(view.transform.position);
      var local = rotate(inverse, [p[0] - headPosition[0], p[1] - headPosition[1], p[2] - headPosition[2]]);
      writeArray(e === 0 ? 4 : 20, view.projectionMatrix);
      writePose(e === 0 ? 43 : 50, local, multiply(inverse, quat(view.transform.orientation)));
    }
    writeControllers(frame);
    write(0, 1);
    X.pose = pose;
  }

  function present(frame) {
    var pose = X.pose;
    X.pose = null;
    if (!pose || !X.context) return;
    var source = X.context.getCurrentTexture();
    var eyes = views(pose);
    var sourceView = source.createView();
    var encoder = X.device.createCommandEncoder();
    for (var e = 0; e < eyes.length; e++) {
      var sub = X.binding.getViewSubImage(X.layer, eyes[e]);
      var format = X.layerFormat;
      var target = sub.colorTexture.createView(sub.getViewDescriptor ? sub.getViewDescriptor() : undefined);
      var uniform = X.uniforms[e];
      X.device.queue.writeBuffer(uniform, 0, new Float32Array([eyes.length === 2 ? e * 0.5 : 0, 0, eyes.length === 2 ? 0.5 : 1, 1, /-srgb$/.test(format) ? 1 : 0, 0, 0, 0]));
      var p = pipeline(format);
      var group = X.device.createBindGroup({ layout: p.getBindGroupLayout(0), entries: [{ binding: 0, resource: sourceView }, { binding: 1, resource: X.sampler }, { binding: 2, resource: { buffer: uniform } }] });
      var pass = encoder.beginRenderPass({ colorAttachments: [{ view: target, loadOp: 'clear', storeOp: 'store', clearValue: { r: 0, g: 0, b: 0, a: 1 } }] });
      pass.setViewport(sub.viewport.x, sub.viewport.y, sub.viewport.width, sub.viewport.height, 0, 1);
      pass.setPipeline(p);
      pass.setBindGroup(0, group);
      pass.draw(3);
      pass.end();
    }
    X.device.queue.submit([encoder.finish()]);
  }

  function loopObject() {
    if (typeof MainLoop !== 'undefined' && MainLoop && MainLoop.requestAnimationFrame) return { raf: MainLoop, loop: MainLoop };
    if (typeof Browser !== 'undefined' && Browser && Browser.requestAnimationFrame) return { raf: Browser, loop: Browser.mainLoop };
    return null;
  }

  function restart(target) {
    if (!target || !target.loop || !target.loop.pause) return;
    target.loop.pause();
    target.loop.resume();
  }

  function hookLoop() {
    var target = loopObject();
    if (!target) return false;
    X.loop = target;
    X.loopRaf = target.raf.requestAnimationFrame;
    target.raf.requestAnimationFrame = function (func) {
      if (X.session && X.state === 2) {
        return X.session.requestAnimationFrame(function (time, frame) {
          onFrame(frame);
          func(time);
          present(frame);
        });
      }
      return X.loopRaf.call(target.raf, func);
    };
    restart(target);
    return true;
  }

  function unhookLoop() {
    var target = X.loop;
    if (!target) return;
    target.raf.requestAnimationFrame = X.loopRaf;
    X.loop = null;
    setTimeout(function () {
      restart(target);
    }, 0);
  }

  function clear() {
    if (X.data) for (var i = 0; i < 160; i++) write(i, 0);
  }

  function finish(state) {
    var session = X.session;
    X.session = null;
    X.binding = null;
    X.layer = null;
    X.space = null;
    X.pose = null;
    X.eyeWidth = 0;
    X.eyeHeight = 0;
    clear();
    unhookLoop();
    if (X.savedMatch !== undefined) {
      Module.matchWebGLToCanvasSize = X.savedMatch;
      X.savedMatch = undefined;
    }
    if (X.savedSize && Module.canvas) {
      Module.canvas.width = X.savedSize[0];
      Module.canvas.height = X.savedSize[1];
    }
    X.savedSize = null;
    X.state = state;
    return session;
  }

  function fail(code, reason) {
    console.warn('[Basis] WebXR could not start: ' + reason);
    X.error = code;
    var session = finish(3);
    if (session) session.end().catch(function () {});
  }

  function started(session) {
    X.session = session;
    session.addEventListener('end', function () {
      if (X.session === session) finish(4);
    });
    try {
      X.binding = new XRGPUBinding(session, X.device);
      X.layerFormat = X.binding.getPreferredColorFormat();
      X.layer = X.binding.createProjectionLayer({ colorFormat: X.layerFormat });
      session.updateRenderState({ layers: [X.layer] });
      X.sampler = X.sampler || X.device.createSampler({ magFilter: 'linear', minFilter: 'linear' });
      if (!X.uniforms.length) for (var i = 0; i < 2; i++) X.uniforms.push(X.device.createBuffer({ size: 32, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST }));
    } catch (e) {
      fail(4, e);
      return;
    }
    session.requestReferenceSpace('local-floor').catch(function () {
      return session.requestReferenceSpace('local');
    }).then(function (space) {
      if (X.session !== session) return;
      X.space = space;
      X.savedMatch = Module.matchWebGLToCanvasSize;
      Module.matchWebGLToCanvasSize = false;
      if (Module.canvas) X.savedSize = [Module.canvas.width, Module.canvas.height];
      X.state = 2;
      if (!hookLoop()) fail(4, 'the main loop could not be moved onto the XR session');
    }, function (e) {
      fail(3, e);
    });
  }

  X.request = function () {
    if (X.state === 1 || X.state === 2) return;
    X.error = 0;
    if (typeof XRGPUBinding === 'undefined') {
      X.error = 1;
      X.state = 3;
      return;
    }
    if (!X.device) {
      X.error = 4;
      X.state = 3;
      return;
    }
    X.state = 1;
    navigator.xr.requestSession('immersive-vr', { requiredFeatures: ['webgpu'], optionalFeatures: ['local-floor'] }).then(started, function (e) {
      fail(3, e);
    });
  };

  X.end = function () {
    var session = X.session;
    if (X.state === 1 || X.state === 2) finish(0);
    else X.state = 0;
    if (session) session.end().catch(function () {});
  };

  X.haptic = function (hand, amplitude, duration) {
    if (!X.session) return;
    var sources = X.session.inputSources;
    for (var i = 0; i < sources.length; i++) {
      var source = sources[i];
      if ((hand === 0 && source.handedness !== 'left') || (hand === 1 && source.handedness !== 'right')) continue;
      var gamepad = source.gamepad;
      var actuator = gamepad && gamepad.hapticActuators && gamepad.hapticActuators[0];
      if (actuator && actuator.pulse) actuator.pulse(amplitude, duration * 1000);
      else if (gamepad && gamepad.vibrationActuator && gamepad.vibrationActuator.playEffect) gamepad.vibrationActuator.playEffect('dual-rumble', { duration: duration * 1000, strongMagnitude: amplitude, weakMagnitude: amplitude });
    }
  };

  return X;
})();
