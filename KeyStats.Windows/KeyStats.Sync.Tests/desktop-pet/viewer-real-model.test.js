const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const modelRoot = process.env.KEYSTATS_REAL_PET_MODEL;

test('viewer changes real Core parameters and hides the base hand while an overlay is active',
  { skip: !modelRoot }, async () => {
    const assets = path.resolve(__dirname, '../../KeyStats/Resources/DesktopPet');
    let hostMessage, nextFrame, coreModel;
    const messages = [];
    const element = () => ({ hidden: true, textContent: '', appendChild() {} });
    const canvas = { clientWidth: 340, clientHeight: 340, getContext: () => gl };
    const gl = { createTexture: () => ({}), bindTexture() {}, pixelStorei() {}, texImage2D() {},
      texParameteri() {}, generateMipmap() {}, deleteTexture() {}, viewport() {}, clearColor() {}, clear() {} };
    const context = { console, setTimeout, clearTimeout, TextEncoder, TextDecoder, WebAssembly, atob, btoa,
      URL, Map, document: { getElementById: id => id === 'stage' ? canvas : element(), createElement: element },
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
      setMvpMatrix() {} setRenderState() {} drawModel() {} release() {}
    };
    load('input-state.js');
    load('viewer.js');
    const send = data => hostMessage({ data });
    const param = id => coreModel.parameters.values[coreModel.parameters.ids.indexOf(id)];
    const stats = () => { send({ type: 'stats' }); return messages[messages.length - 1]; };
    try {
      send({ type: 'load', loadId: 1, baseUrl: 'https://keystats-pet.invalid/model/',
        moc: 'img/standard/cat_model/deepseek.moc3',
        textures: ['img/standard/cat_model/deepseek.4096/texture_00.png'],
        hands: [{ virtualKeys: [65], image: 'img/standard/hand/0.png' }] });
      const deadline = Date.now() + 5000;
      while (!messages.some(m => m.type === 'ready') && Date.now() < deadline) {
        await new Promise(resolve => setTimeout(resolve, 20));
      }
      assert.ok(messages.some(m => m.type === 'ready'), JSON.stringify(messages));
      send({ type: 'input', keys: [], mouseX: 0.5, mouseY: 0.5 }); nextFrame();
      const baseOpacity = Array.from(coreModel.drawables.opacities);
      send({ type: 'input', keys: [65], mouseX: 1, mouseY: 0, leftDown: true, rightDown: true }); nextFrame();
      assert.equal(param('CatParamLeftHandDown'), 1, 'must write the actual Core slot, not a virtual Framework parameter');
      assert.ok(baseOpacity.some((opacity, i) => opacity > coreModel.drawables.opacities[i]), 'base hand drawable must become hidden');
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
      assert.deepEqual(Array.from(coreModel.drawables.opacities), baseOpacity);
    } finally { send({ type: 'unload' }); bundle.CubismFramework.dispose(); }
  });
