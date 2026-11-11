// Desktop pet viewer: renders one Live2D model and applies host-provided input frames.
// The host (WPF) owns all model-file access; this page only fetches relative URLs from the
// restricted virtual host it was served from, and never executes anything from the model package.
(function () {
  'use strict';

  var CORE_MAJOR_VERSION = 6;

  var canvas = document.getElementById('stage');
  var overlayContainer = document.getElementById('overlays');
  var statusElement = document.getElementById('status');

  var model = null;
  var mocWrapper = null;
  var renderer = null;
  var gl = null;
  var modelMatrix = null;
  var projection = null;
  var textures = [];
  var running = false;
  // Desired render state from the host; a load that finishes while the window is hidden must not
  // start rendering on its own.
  var wantRunning = true;
  var rafHandle = 0;

  // Preloaded hand overlays; input only toggles visibility so a key press never triggers IO.
  var handLayers = new Map();
  var handImageStates = {};
  var currentHandImage = null;

  var inputState = null;
  var pendingFrame = null;
  var loadGeneration = 0;

  function showStatus(message, isError) {
    if (!statusElement) return;
    if (!message) {
      statusElement.hidden = true;
      statusElement.textContent = '';
      statusElement.className = '';
      return;
    }
    statusElement.hidden = false;
    statusElement.textContent = message;
    statusElement.className = isError ? 'error' : '';
  }

  function post(message) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(JSON.stringify(message));
    }
  }

  function fail(message, loadId) {
    showStatus(message, true);
    post({ type: 'error', message: message, loadId: loadId || 0 });
  }

  // The host supplies the one allowed model origin at load time; every model path is joined onto
  // it and must stay underneath it, so absolute URLs and ".." never resolve anywhere else.
  var modelBase = null;

  function resolveUnder(base, relativePath) {
    if (!base || typeof relativePath !== 'string' || relativePath.length === 0) return null;
    var url;
    try {
      url = new URL(relativePath, base);
    } catch (e) {
      return null;
    }
    if (url.protocol !== 'https:') return null;
    if (url.origin !== new URL(base).origin) return null;
    if (url.href.indexOf(base) !== 0) return null;
    return url;
  }

  async function fetchBytes(relativePath) {
    var url = resolveUnder(modelBase, relativePath);
    if (!url) throw new Error('拒绝加载模型外的资源');
    var response;
    try {
      response = await fetch(url.href);
    } catch (fetchError) {
      throw new Error('资源请求失败：' + relativePath + ' (' + url.href + ')');
    }
    if (!response.ok) throw new Error('资源加载失败：' + relativePath + ' HTTP ' + response.status);
    return await response.arrayBuffer();
  }

  function loadImage(relativePath) {
    return new Promise(function (resolve, reject) {
      var url = resolveUnder(modelBase, relativePath);
      if (!url) {
        reject(new Error('拒绝加载模型外的资源'));
        return;
      }
      var image = new Image();
      image.onload = function () { resolve(image); };
      image.onerror = function () { reject(new Error('图片加载失败：' + relativePath)); };
      image.src = url.href;
    });
  }

  function resizeCanvas() {
    var dpr = window.devicePixelRatio || 1;
    var width = Math.max(1, Math.round(canvas.clientWidth * dpr));
    var height = Math.max(1, Math.round(canvas.clientHeight * dpr));
    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width;
      canvas.height = height;
      return true;
    }
    return false;
  }

  function releaseModel() {
    running = false;
    if (rafHandle) {
      cancelAnimationFrame(rafHandle);
      rafHandle = 0;
    }
    currentHandImage = null;
    handLayers = new Map();
    if (overlayContainer) overlayContainer.textContent = '';
    for (var i = 0; i < textures.length; i++) {
      try { gl.deleteTexture(textures[i]); } catch (e) { /* context may already be gone */ }
    }
    textures = [];
    if (renderer) {
      try { renderer.release(); } catch (e) { /* ignore teardown errors */ }
      renderer = null;
    }
    if (mocWrapper) {
      try {
        if (model) mocWrapper.deleteModel(model);
        mocWrapper.release();
      } catch (e) { /* ignore teardown errors */ }
      mocWrapper = null;
    }
    model = null;
    modelMatrix = null;
    projection = null;
    inputState = null;
    pendingFrame = null;
  }

  /**
   * Preloads every configured hand overlay into detached elements. Nothing is attached to the DOM
   * here: the caller commits the layers only after confirming its load generation is still current,
   * so a superseded load can never inject stale images into the new model.
   */
  function preloadHandLayers(payload) {
    var specs = payload.hands || [];
    return Promise.all(specs.map(function (spec) {
      return loadImage(spec.image).then(function (image) {
        var layer = document.createElement('img');
        layer.src = image.src;
        layer.alt = '';
        layer.draggable = false;
        layer.hidden = true;
        return { path: spec.image, layer: layer };
      });
    }));
  }

  function commitHandLayers(layers) {
    handLayers = new Map();
    handImageStates = {};
    if (overlayContainer) overlayContainer.textContent = '';
    for (var i = 0; i < layers.length; i++) {
      handLayers.set(layers[i].path, layers[i].layer);
      handImageStates[layers[i].path] = true;
      if (overlayContainer) overlayContainer.appendChild(layers[i].layer);
    }
  }

  async function loadModel(payload) {
    var generation = ++loadGeneration;
    var hostLoadId = typeof payload.loadId === 'number' ? payload.loadId : 0;
    releaseModel();
    showStatus('正在加载模型…', false);

    if (!window.Live2DCubismCore) { fail('Cubism Core 未加载，请重新安装应用', hostLoadId); return; }
    if (!window.CubismBundle) { fail('渲染组件未加载，请重新安装应用', hostLoadId); return; }

    var coreVersion = window.Live2DCubismCore.Version.csmGetVersion();
    if ((coreVersion >>> 24) !== CORE_MAJOR_VERSION) {
      fail('Cubism Core 版本不匹配，请重新安装应用', hostLoadId);
      return;
    }

    modelBase = typeof payload.baseUrl === 'string' ? payload.baseUrl : null;
    if (!modelBase || modelBase.slice(-1) !== '/') {
      fail('模型路径无效', hostLoadId);
      return;
    }

    try {
      var mocBytes = await fetchBytes(payload.moc);
      if (generation !== loadGeneration) return;

      var preloaded;
      try {
        preloaded = await Promise.all([
          preloadHandLayers(payload),
          Promise.all((payload.textures || []).map(loadImage))
        ]);
      } catch (preloadError) {
        if (generation !== loadGeneration) return;
        fail('模型图片加载失败，请检查模型文件夹是否完整', hostLoadId);
        return;
      }

      if (generation !== loadGeneration) return;
      var handLayerSpecs = preloaded[0];
      var images = preloaded[1];
      if (images.length === 0) { fail('模型缺少贴图', hostLoadId); return; }

      window.CubismBundle.CubismFramework.startUp(new window.CubismBundle.Option());
      window.CubismBundle.CubismFramework.initialize();

      mocWrapper = window.CubismBundle.CubismMoc.create(mocBytes, true);
      if (!mocWrapper) { fail('模型文件损坏，请重新选择模型文件夹', hostLoadId); return; }
      model = mocWrapper.createModel();
      if (!model) { fail('模型无法初始化，请更换模型文件夹', hostLoadId); return; }

      resizeCanvas();
      gl = canvas.getContext('webgl', { premultipliedAlpha: true, alpha: true, antialias: true });
      if (!gl) { fail('当前环境不支持 WebGL，无法显示模型', hostLoadId); return; }

      renderer = new window.CubismBundle.CubismRenderer_WebGL(canvas.width, canvas.height);
      renderer.initialize(model);
      renderer.startUp(gl);
      renderer.loadShaders('vendor/shaders/');
      renderer.setIsPremultipliedAlpha(true);

      for (var index = 0; index < images.length; index++) {
        var glTexture = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, glTexture);
        gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, 1);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, images[index]);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.generateMipmap(gl.TEXTURE_2D);
        renderer.bindTexture(index, glTexture);
        textures.push(glTexture);
      }

      if (generation !== loadGeneration) return;
      commitHandLayers(handLayerSpecs);

      // Model units, not screen pixels. Screen pixels only enter viewport and projection.
      modelMatrix = new window.CubismBundle.CubismModelMatrix(
        model.getCanvasWidth(),
        model.getCanvasHeight());
      projection = new window.CubismBundle.CubismMatrix44();
      projection.scale(1, canvas.width / canvas.height);

      inputState = window.DesktopPetInput.createInputState(
        {
          hands: (payload.hands || []).map(function (spec) {
            return { virtualKeys: spec.virtualKeys, imageRelativePath: spec.image };
          })
        },
        {
          sceneWidth: model.getCanvasWidth() > 0 ? model.getCanvasWidth() : 1,
          sceneHeight: model.getCanvasHeight() > 0 ? model.getCanvasHeight() : 1,
          loadedHandImages: handImageStates
        });

      showStatus('', false);
      running = wantRunning;
      post({
        type: 'ready',
        loadId: hostLoadId,
        coreVersion: coreVersion,
        modelCanvas: { width: model.getCanvasWidth(), height: model.getCanvasHeight() },
        canvas: { width: canvas.width, height: canvas.height },
        handCount: (payload.hands || []).length,
        handOverlaysLoaded: Object.keys(handImageStates).filter(function (key) { return handImageStates[key]; }).length
      });
      if (running) {
        rafHandle = requestAnimationFrame(tick);
      }
    } catch (error) {
      if (generation !== loadGeneration) return;
      releaseModel();
      fail(error && error.message ? error.message : String(error), hostLoadId);
    }
  }

  /**
   * Writes parameter values through the Framework's public API. Inputs are normalised fractions;
   * booleans become 0/1, and every value is clamped into the model's own min/max range because many
   * parameters are not -1..1.
   */
  function applyParams(params) {
    if (!model || !params) return;
    for (var id in params) {
      if (!Object.prototype.hasOwnProperty.call(params, id)) continue;

      var handle = window.CubismBundle.CubismFramework.getIdManager().getId(id);
      var index = model.getParameterIndex(handle);
      if (index < 0 || index >= model.getParameterCount()) continue;

      var value = params[id];
      if (typeof value === 'boolean') value = value ? 1 : 0;
      if (typeof value !== 'number' || !isFinite(value)) continue;

      var minimum = model.getParameterMinimumValue(index);
      var maximum = model.getParameterMaximumValue(index);
      if (id === 'ParamMouseX' || id === 'ParamMouseY') {
        var neutral = model.getParameterDefaultValue(index);
        value = neutral + value * (value >= 0 ? maximum - neutral : neutral - minimum);
      }
      if (value < minimum) value = minimum;
      if (value > maximum) value = maximum;

      model.setParameterValueByIndex(index, value);
    }
  }

  /** Reports the Live2D parameter values the viewer actually applied, for diagnostics. */
  function readParams() {
    if (!model || !window.DesktopPetInput) return null;
    var paramIds = window.DesktopPetInput.params;
    var ids = [paramIds.mouseX, paramIds.mouseY,
      paramIds.mouseLeftDown, paramIds.mouseRightDown,
      paramIds.leftHandDown];
    var values = {};
    for (var i = 0; i < ids.length; i++) {
      var handle = window.CubismBundle.CubismFramework.getIdManager().getId(ids[i]);
      var index = model.getParameterIndex(handle);
      values[ids[i]] = index >= 0 && index < model.getParameterCount()
        ? model.getParameterValueByIndex(index) : null;
    }

    return values;
  }

  function setHandOverlay(imageRelativePath) {
    if (imageRelativePath === currentHandImage) return;
    currentHandImage = imageRelativePath;
    handLayers.forEach(function (layer, path) {
      layer.hidden = path !== imageRelativePath;
    });
  }

  function tick() {
    if (!running || !model || !renderer) return;
    frameCounter++;

    if (pendingFrame) {
      var frame = pendingFrame;
      pendingFrame = null;
      var applied = inputState.applyInput(frame);
      applyParams(applied.params);
      setHandOverlay(applied.handImage);
    }

    model.update();

    var mvp = new window.CubismBundle.CubismMatrix44();
    mvp.setMatrix(projection.getArray());
    mvp.multiplyByMatrix(modelMatrix);
    renderer.setMvpMatrix(mvp);

    gl.viewport(0, 0, gl.drawingBufferWidth, gl.drawingBufferHeight);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    renderer.setRenderState(null, [0, 0, gl.drawingBufferWidth, gl.drawingBufferHeight]);
    renderer.drawModel('vendor/shaders/');

    rafHandle = requestAnimationFrame(tick);
  }

  function stopRendering() {
    running = false;
    wantRunning = false;
    if (rafHandle) {
      cancelAnimationFrame(rafHandle);
      rafHandle = 0;
    }
    pendingFrame = null;
  }

  function handleHostMessage(raw) {
    var message;
    try {
      message = typeof raw === 'string' ? JSON.parse(raw) : raw;
    } catch (e) {
      return;
    }
    if (!message || typeof message.type !== 'string') return;

    switch (message.type) {
      case 'load':
        loadModel(message);
        break;
      case 'input':
        if (!inputState) return;
        pendingFrame = {
          keys: Array.isArray(message.keys) ? message.keys : [],
          mouseX: message.mouseX,
          mouseY: message.mouseY,
          leftDown: !!message.leftDown,
          rightDown: !!message.rightDown
        };
        break;
      case 'pause':
        stopRendering();
        break;
      case 'resume':
        wantRunning = true;
        if (model && renderer && !running) {
          running = true;
          rafHandle = requestAnimationFrame(tick);
        }
        break;
      case 'unload':
        // Invalidate any in-flight load so it cannot create a model after being unloaded.
        loadGeneration++;
        stopRendering();
        wantRunning = false;
        releaseModel();
        showStatus('', false);
        break;
      case 'stats':
        post({
          type: 'stats',
          modelLoaded: !!model,
          running: running,
          handImage: currentHandImage,
          handsLoaded: handLayers.size,
          frame: frameCounter,
          input: inputState ? inputState.getState() : null,
          params: readParams()
        });
        break;
    }
  }

  var frameCounter = 0;

  window.addEventListener('error', function (event) {
    post({ type: 'page-error', message: String(event.message) });
  });

  window.addEventListener('resize', function () {
    if (!model || !renderer) return;
    resizeCanvas();
    renderer.setRenderTargetSize(canvas.width, canvas.height);
    projection = new window.CubismBundle.CubismMatrix44();
    projection.scale(1, canvas.width / canvas.height);
  });

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', function (event) {
      handleHostMessage(event.data);
    });
    post({ type: 'viewer-ready' });
  } else {
    showStatus('此页面需要在应用内打开', true);
  }
})();
