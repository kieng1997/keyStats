const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const assets = path.resolve(__dirname, '../../KeyStats/Resources/DesktopPet');
const modelsRoot = path.resolve(assets, 'Models');
// Tests default to the bundled real packages; KEYSTATS_REAL_PET_MODEL overrides for a local copy.
const modelRoot = process.env.KEYSTATS_REAL_PET_MODEL || path.join(modelsRoot, 'ds-standard');
const nailinRoot = process.env.KEYSTATS_NAILIN_PET_MODEL || path.join(modelsRoot, 'nailin-keyboard');

// Layers recorded by the mocked renderer, keyed by texture bind order.
async function createHarness(modelRoot, payload) {
  let hostMessage, nextFrame, coreModel;
  const messages = [];
  const drawOrders = [];
  const element = () => ({ hidden: true, textContent: '', className: '', appendChild() {} });
  const overlays = { hidden: false, children: [], appendChild(child) { this.children.push(child); } };
  Object.defineProperty(overlays, 'textContent', {
    set(value) { if (value === '') this.children.length = 0; }
  });
  const background = { hidden: true };
  const backgroundImage = {
    _src: null,
    set src(value) { this._src = value; },
    get src() { return this._src; },
    removeAttribute(name) { if (name === 'src') this._src = null; }
  };
  const canvas = { clientWidth: 340, clientHeight: 340, getContext: () => gl };
  const gl = { createTexture: () => ({}), bindTexture() {}, pixelStorei() {}, texImage2D() {},
    texParameteri() {}, generateMipmap() {}, deleteTexture() {}, viewport() {}, clearColor() {}, clear() {} };
  const context = { console, setTimeout, clearTimeout, TextEncoder, TextDecoder, WebAssembly, atob, btoa,
    URL, Map,
    document: {
      getElementById: id => (id === 'stage' ? canvas
        : id === 'overlays' ? overlays
        : id === 'background' ? background
        : id === 'background-image' ? backgroundImage
        : element()),
      createElement: element
    },
    requestAnimationFrame: callback => { nextFrame = callback; return 1; }, cancelAnimationFrame() {},
    Image: class { set src(value) { this._src = value; queueMicrotask(() => this.onload()); } get src() { return this._src; } },
    fetch: async url => {
      const relative = decodeURIComponent(new URL(url).pathname.slice('/model/'.length));
      const bytes = fs.readFileSync(path.join(modelRoot, relative));
      return { ok: true, arrayBuffer: async () => bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) };
    } };
  vm.createContext(context);
  const load = file => vm.runInContext(fs.readFileSync(path.join(assets, file), 'utf8'), context);
  load('vendor/live2dcubismcore.min.js');
  await new Promise(resolve => setTimeout(resolve, 500));
    context.window = context;
    context.addEventListener = () => {};
    context.chrome = { webview: { addEventListener: (_, callback) => { hostMessage = callback; },
      postMessage: text => messages.push(JSON.parse(text)) } };
    load('vendor/framework.js');
    const bundle = context.CubismBundle;
    const create = bundle.CubismMoc.create.bind(bundle.CubismMoc);
    let moc;
    bundle.CubismMoc.create = (...args) => {
      moc = create(...args);
      const createModel = moc.createModel.bind(moc);
      moc.createModel = () => { const model = createModel(); coreModel = model.getModel(); return model; };
      return moc;
    };
    bundle.CubismRenderer_WebGL = class {
      initialize() {} startUp() {} loadShaders() {} setIsPremultipliedAlpha() {} bindTexture() {}
      setMvpMatrix(mvp) { this.mvp = mvp.getArray(); } setRenderState() {}
      drawModel() { drawOrders.push(this.mvp.slice()); }
      release() {}
    };
    load('input-state.js');
    load('viewer.js');
    const send = data => hostMessage({ data });
    const param = id => coreModel.parameters.values[coreModel.parameters.ids.indexOf(id)];
    const stats = () => { send({ type: 'stats' }); return messages[messages.length - 1]; };
    const harness = {
      send, param, stats, nextFrame: () => nextFrame(), messages, drawOrders,
      overlays: () => overlays,
      background: () => background,
      backgroundImage: () => backgroundImage,
      coreModel: () => coreModel, bundle,
      dispose: () => { send({ type: 'unload' }); bundle.CubismFramework.dispose(); }
    };
    send(Object.assign({ type: 'load', loadId: 1, baseUrl: 'https://keystats-pet.invalid/model/' }, payload));
    return harness;
}

async function waitUntilReady(messages) {
  const deadline = Date.now() + 5000;
  while (!messages.some(m => m.type === 'ready') && Date.now() < deadline) {
    await new Promise(resolve => setTimeout(resolve, 20));
  }
  assert.ok(messages.some(m => m.type === 'ready'), JSON.stringify(messages));
}

test('viewer changes real Core parameters and hides the base hand while an overlay is active', async () => {
  const harness = await createHarness(modelRoot, {
    moc: 'img/standard/cat_model/deepseek.moc3',
    textures: ['img/standard/cat_model/deepseek.4096/texture_00.png'],
    hands: [{ virtualKeys: [65], image: 'img/standard/hand/0.png' }]
  });
  const { send, param, stats, nextFrame, messages, coreModel, bundle } = harness;
  try {
    await waitUntilReady(messages);
    send({ type: 'input', keys: [], mouseX: 0.5, mouseY: 0.5 }); nextFrame();
    const baseOpacity = Array.from(coreModel().drawables.opacities);
    send({ type: 'input', keys: [65], mouseX: 1, mouseY: 0, leftDown: true, rightDown: true }); nextFrame();
    assert.equal(param('CatParamLeftHandDown'), 1, 'must write the actual Core slot, not a virtual Framework parameter');
    assert.ok(baseOpacity.some((opacity, i) => opacity > coreModel().drawables.opacities[i]), 'base hand drawable must become hidden');
    assert.equal(param('ParamMouseLeftDown'), 1);
    assert.equal(param('ParamMouseRightDown'), 1);
    assert.equal(param('ParamMouseX'), 30, 'desktop edge must reach the real model parameter endpoint');
    assert.equal(param('ParamMouseY'), 30);
    assert.equal(stats().params.CatParamLeftHandDown, param('CatParamLeftHandDown'));
    send({ type: 'input', keys: [], mouseX: 0, mouseY: 1 }); nextFrame();
    assert.equal(param('ParamMouseX'), -30);
    assert.equal(param('ParamMouseY'), -30);
    send({ type: 'input', keys: [], mouseX: 0.5, mouseY: 0.5 }); nextFrame();
    assert.equal(param('ParamMouseX'), 0);
    assert.equal(param('ParamMouseY'), 0);
    assert.equal(param('CatParamLeftHandDown'), 0);
    assert.deepEqual(Array.from(coreModel().drawables.opacities), baseOpacity);
  } finally { harness.dispose(); }
});

test('nailin keyboard model drives left and right overlays and fits its wide canvas', async () => {
  const harness = await createHarness(nailinRoot, {
    moc: 'cat.moc3',
    textures: ['cat.4096/texture_00.png'],
    background: 'resources/background.png',
    hands: [
      { virtualKeys: [65], image: 'resources/left-keys/KeyA.png' },
      { virtualKeys: [13], image: 'resources/right-keys/Return.png', isRightHand: true }
    ]
  });
  const { send, param, stats, nextFrame, messages, coreModel, drawOrders, overlays, background, backgroundImage, bundle } = harness;
  try {
    await waitUntilReady(messages);

    // The static keyboard background shipped with the model is displayed under the canvas.
    assert.equal(background().hidden, false, 'nailin background layer is visible');
    assert.ok(backgroundImage().src && backgroundImage().src.indexOf('resources/background.png') >= 0,
      'background element shows the packaged keyboard, got ' + backgroundImage().src);

    send({ type: 'input', keys: [], mouseX: 0.5, mouseY: 0.5 }); nextFrame();
    const baseOpacity = Array.from(coreModel().drawables.opacities);

    send({ type: 'input', keys: [65, 13], mouseX: 0.5, mouseY: 0.5 }); nextFrame();
    assert.equal(param('CatParamLeftHandDown'), 1, 'left overlay drives the left parameter');
    assert.equal(param('CatParamRightHandDown'), 1, 'right overlay drives the right parameter');
    const bothStats = stats();
    assert.equal(bothStats.params.CatParamRightHandDown, 1, 'stats report the right-hand parameter');
    assert.equal(bothStats.rightHandImage, 'resources/right-keys/Return.png');
    assert.equal(bothStats.handImage, 'resources/left-keys/KeyA.png');
    const visibleLayers = overlays().children.filter(layer => !layer.hidden);
    assert.equal(visibleLayers.length, 2, 'both overlay images must be visible at once');
    const visibleSources = visibleLayers.map(layer => layer.src).sort();
    assert.ok(visibleSources[0].indexOf('left-keys/KeyA.png') >= 0, 'left overlay layer is shown');
    assert.ok(visibleSources[1].indexOf('right-keys/Return.png') >= 0, 'right overlay layer is shown');

    send({ type: 'input', keys: [65], mouseX: 0.5, mouseY: 0.5 }); nextFrame();
    assert.equal(param('CatParamLeftHandDown'), 1, 'left stays while A is held');
    assert.equal(param('CatParamRightHandDown'), 0, 'right side recovers on its own release');
    const afterRightRelease = stats();
    assert.equal(afterRightRelease.rightHandImage, null);
    assert.equal(afterRightRelease.handImage, 'resources/left-keys/KeyA.png');
    assert.equal(overlays().children.filter(layer => !layer.hidden).length, 1, 'only the left overlay remains');

    send({ type: 'input', keys: [], mouseX: 0.5, mouseY: 0.5 }); nextFrame();
    assert.equal(param('CatParamLeftHandDown'), 0);
    assert.equal(param('CatParamRightHandDown'), 0);
    assert.equal(overlays().children.filter(layer => !layer.hidden).length, 0, 'both sides return to the base hand');
    assert.deepEqual(Array.from(coreModel().drawables.opacities), baseOpacity);

    // The nailin canvas is 1.728813559 x 1; fitting must scale it into [-1,1] on the wide axis
    // (MVP x-scale about 1.156862745 = 2 / (w/h) via setWidth), not 2 which crops both sides.
    assert.equal(drawOrders.length > 0, true, 'renderer must draw at least once');
    const mvp = drawOrders[drawOrders.length - 1];
    assert.ok(Math.abs(mvp[0] - 1.156862745) < 0.0001,
      'wide model must fit by width (MVP x-scale ' + mvp[0] + ')');

    // Unloading must clear the background so no nailin keyboard survives a model switch.
    send({ type: 'unload' });
    assert.equal(background().hidden, true, 'unload hides the background');
    assert.equal(backgroundImage().src, null, 'unload removes the background src');
  } finally { bundle.CubismFramework.dispose(); }
});
