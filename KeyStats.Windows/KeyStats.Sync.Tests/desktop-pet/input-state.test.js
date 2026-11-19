// Node's built-in test runner (node --test); no extra test dependencies.
const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');

const input = require(path.join(
  __dirname,
  '..',
  '..',
  'KeyStats',
  'Resources',
  'DesktopPet',
  'input-state.js'
));

// Mirrors the real Deepseek standard package: 17 hand groups, one VK each.
const KEYS = [65, 68, 69, 81, 82, 83, 87, 16, 32, 49, 50, 51, 52, 53, 54, 55, 17];
const MODEL = {
  hands: KEYS.map((key, index) => ({
    virtualKeys: [key],
    imageRelativePath: 'img/standard/hand/' + index + '.png'
  }))
};

const OPTS = {};

test('pressing a configured key shows its overlay and raises CatParamLeftHandDown', () => {
  const state = input.createInputState(MODEL, OPTS);

  const result = state.applyInput({ keys: [65], mouseX: 0.5, mouseY: 0.5 });

  assert.strictEqual(result.handImage, 'img/standard/hand/0.png');
  assert.strictEqual(result.params[input.params.leftHandDown], 1);
  assert.strictEqual(result.changed, true);
});

test('releasing every key returns to the base hand', () => {
  const state = input.createInputState(MODEL, OPTS);
  state.applyInput({ keys: [65], mouseX: 0.5, mouseY: 0.5 });

  const result = state.applyInput({ keys: [], mouseX: 0.5, mouseY: 0.5 });

  assert.strictEqual(result.handImage, null);
  assert.strictEqual(result.params[input.params.leftHandDown], 0);
  assert.strictEqual(state.getState().activeHandIndex, -1);
});

test('holding two keys keeps one overlay stable until both are released', () => {
  const state = input.createInputState(MODEL, OPTS);
  state.applyInput({ keys: [65, 68], mouseX: 0.5, mouseY: 0.5 });
  const both = state.getState().activeHandIndex;

  const oneReleased = state.applyInput({ keys: [68], mouseX: 0.5, mouseY: 0.5 });
  assert.notStrictEqual(oneReleased.handImage, null);

  const allReleased = state.applyInput({ keys: [], mouseX: 0.5, mouseY: 0.5 });
  assert.strictEqual(allReleased.handImage, null);
  assert.strictEqual(state.getState().activeHandIndex, -1);
  assert.notStrictEqual(both, -1);
});

test('the newly pressed key wins while it is held, then falls back to the still-held key', () => {
  const state = input.createInputState(MODEL, OPTS);

  const a = state.applyInput({ keys: [65], mouseX: 0.5, mouseY: 0.5 });
  assert.strictEqual(a.handImage, 'img/standard/hand/0.png');

  // D is pressed while A is still held: the newest press must be the one shown.
  const aThenD = state.applyInput({ keys: [65, 68], mouseX: 0.5, mouseY: 0.5 });
  assert.strictEqual(aThenD.handImage, 'img/standard/hand/1.png');

  // Releasing D returns to A, which is still held.
  const backToA = state.applyInput({ keys: [65], mouseX: 0.5, mouseY: 0.5 });
  assert.strictEqual(backToA.handImage, 'img/standard/hand/0.png');

  const base = state.applyInput({ keys: [], mouseX: 0.5, mouseY: 0.5 });
  assert.strictEqual(base.handImage, null);
  assert.strictEqual(base.params[input.params.leftHandDown], 0);
});

test('a newly pressed key wins regardless of its hand index order', () => {
  const state = input.createInputState(MODEL, OPTS);

  // 17 is the last configured hand; pressing it second must still win over 65.
  state.applyInput({ keys: [65], mouseX: 0.5, mouseY: 0.5 });
  const newest = state.applyInput({ keys: [65, 17], mouseX: 0.5, mouseY: 0.5 });
  assert.strictEqual(newest.handImage, 'img/standard/hand/16.png');

  // Pressing an earlier-indexed key afterwards makes it the newest.
  const older = state.applyInput({ keys: [65, 17, 68], mouseX: 0.5, mouseY: 0.5 });
  assert.strictEqual(older.handImage, 'img/standard/hand/1.png');
});

test('rapid key switching never leaves a stale overlay after release', () => {
  const state = input.createInputState(MODEL, OPTS);

  state.applyInput({ keys: [65], mouseX: 0.5, mouseY: 0.5 });
  state.applyInput({ keys: [68], mouseX: 0.5, mouseY: 0.5 });
  state.applyInput({ keys: [69], mouseX: 0.5, mouseY: 0.5 });
  const final = state.applyInput({ keys: [], mouseX: 0.5, mouseY: 0.5 });

  assert.strictEqual(final.handImage, null);
  assert.strictEqual(final.params[input.params.leftHandDown], 0);
});

test('unconfigured keys do not change the overlay', () => {
  const state = input.createInputState(MODEL, OPTS);

  const result = state.applyInput({ keys: [1, 2, 3], mouseX: 0.5, mouseY: 0.5 });

  assert.strictEqual(result.handImage, null);
  assert.strictEqual(result.params[input.params.leftHandDown], 0);
});

test('window fractions map to -1..1 with the centre as origin', () => {
  const state = input.createInputState(MODEL, OPTS);

  const centre = state.applyInput({ keys: [], mouseX: 0.5, mouseY: 0.5 });
  assert.strictEqual(centre.params[input.params.mouseX], 0);
  assert.strictEqual(centre.params[input.params.mouseY], 0);

  const topLeft = state.applyInput({ keys: [], mouseX: 0, mouseY: 0 });
  assert.strictEqual(topLeft.params[input.params.mouseX], -1);
  assert.strictEqual(topLeft.params[input.params.mouseY], 1);

  const bottomRight = state.applyInput({ keys: [], mouseX: 1, mouseY: 1 });
  assert.strictEqual(bottomRight.params[input.params.mouseX], 1);
  assert.strictEqual(bottomRight.params[input.params.mouseY], -1);
});

test('cursor far outside the window is clamped instead of extrapolated', () => {
  const state = input.createInputState(MODEL, OPTS);

  const result = state.applyInput({ keys: [], mouseX: 5.5, mouseY: -4.5 });

  assert.strictEqual(result.params[input.params.mouseX], 1);
  assert.strictEqual(result.params[input.params.mouseY], 1);
});

test('mouse buttons drive their own parameters independently of keys', () => {
  const state = input.createInputState(MODEL, OPTS);

  const left = state.applyInput({ keys: [], mouseX: 0.5, mouseY: 0.5, leftDown: true });
  assert.strictEqual(left.params[input.params.mouseLeftDown], 1);
  assert.strictEqual(left.params[input.params.mouseRightDown], 0);
  assert.strictEqual(left.params[input.params.leftHandDown], 0);

  const both = state.applyInput({ keys: [65], mouseX: 0.5, mouseY: 0.5, leftDown: true, rightDown: true });
  assert.strictEqual(both.params[input.params.mouseLeftDown], 1);
  assert.strictEqual(both.params[input.params.mouseRightDown], 1);
  assert.strictEqual(both.params[input.params.leftHandDown], 1);

  const released = state.applyInput({ keys: [], mouseX: 0.5, mouseY: 0.5 });
  assert.strictEqual(released.params[input.params.mouseLeftDown], 0);
  assert.strictEqual(released.params[input.params.mouseRightDown], 0);
});

test('an overlay whose image failed to load falls back to the base hand', () => {
  const state = input.createInputState(MODEL, {
    loadedHandImages: { 'img/standard/hand/0.png': false }
  });

  const result = state.applyInput({ keys: [65], mouseX: 0.5, mouseY: 0.5 });

  assert.strictEqual(result.handImage, null);
  assert.strictEqual(result.params[input.params.leftHandDown], 0);
});

test('repeating an identical frame reports no change', () => {
  const state = input.createInputState(MODEL, OPTS);
  state.applyInput({ keys: [65], mouseX: 0.3, mouseY: 0.7, leftDown: true });

  const repeated = state.applyInput({ keys: [65], mouseX: 0.3, mouseY: 0.7, leftDown: true });

  assert.strictEqual(repeated.changed, false);
});

// Mirrors the real nailin keyboard package: left and right overlays are separate images.
const KEYBOARD_MODEL = {
  hands: [
    { virtualKeys: [65], imageRelativePath: 'resources/left-keys/KeyA.png' },
    { virtualKeys: [68], imageRelativePath: 'resources/left-keys/KeyD.png' },
    { virtualKeys: [13], imageRelativePath: 'resources/right-keys/Return.png', isRightHand: true }
  ]
};

test('left and right overlays resolve independently via isRightHand', () => {
  const state = input.createInputState(KEYBOARD_MODEL, OPTS);

  const both = state.applyInput({ keys: [65, 13], mouseX: 0.5, mouseY: 0.5 });
  assert.strictEqual(both.handImage, 'resources/left-keys/KeyA.png');
  assert.strictEqual(both.rightHandImage, 'resources/right-keys/Return.png');
  assert.strictEqual(both.params[input.params.leftHandDown], 1);
  assert.strictEqual(both.params[input.params.rightHandDown], 1);
  assert.strictEqual(both.changed, true);

  // D is pressed while A and Return are held: the newest left key wins, the right side is untouched.
  const dPressed = state.applyInput({ keys: [65, 68, 13], mouseX: 0.5, mouseY: 0.5 });
  assert.strictEqual(dPressed.handImage, 'resources/left-keys/KeyD.png');
  assert.strictEqual(dPressed.rightHandImage, 'resources/right-keys/Return.png');
  assert.strictEqual(dPressed.params[input.params.leftHandDown], 1);
  assert.strictEqual(dPressed.params[input.params.rightHandDown], 1);

  // Releasing D returns the left hand to A, which is still held.
  const backToA = state.applyInput({ keys: [65, 13], mouseX: 0.5, mouseY: 0.5 });
  assert.strictEqual(backToA.handImage, 'resources/left-keys/KeyA.png');
  assert.strictEqual(backToA.rightHandImage, 'resources/right-keys/Return.png');

  // Releasing A hides only the left overlay; the right one stays up.
  const leftReleased = state.applyInput({ keys: [13], mouseX: 0.5, mouseY: 0.5 });
  assert.strictEqual(leftReleased.handImage, null);
  assert.strictEqual(leftReleased.params[input.params.leftHandDown], 0);
  assert.strictEqual(leftReleased.rightHandImage, 'resources/right-keys/Return.png');
  assert.strictEqual(leftReleased.params[input.params.rightHandDown], 1);

  // Releasing the last key clears both sides.
  const allReleased = state.applyInput({ keys: [], mouseX: 0.5, mouseY: 0.5 });
  assert.strictEqual(allReleased.handImage, null);
  assert.strictEqual(allReleased.rightHandImage, null);
  assert.strictEqual(allReleased.params[input.params.leftHandDown], 0);
  assert.strictEqual(allReleased.params[input.params.rightHandDown], 0);
  assert.strictEqual(state.getState().activeHandIndex, -1);
});

test('a right-hand change reports changed even when the left hand is stable', () => {
  const state = input.createInputState(KEYBOARD_MODEL, OPTS);
  state.applyInput({ keys: [65], mouseX: 0.5, mouseY: 0.5 });

  const withRight = state.applyInput({ keys: [65, 13], mouseX: 0.5, mouseY: 0.5 });

  assert.strictEqual(withRight.changed, true);
});

test('a failed right-hand image falls back to the base hand on that side only', () => {
  const state = input.createInputState(KEYBOARD_MODEL, {
    loadedHandImages: { 'resources/right-keys/Return.png': false }
  });

  const result = state.applyInput({ keys: [65, 13], mouseX: 0.5, mouseY: 0.5 });

  assert.strictEqual(result.handImage, 'resources/left-keys/KeyA.png');
  assert.strictEqual(result.rightHandImage, null);
  assert.strictEqual(result.params[input.params.leftHandDown], 1);
  assert.strictEqual(result.params[input.params.rightHandDown], 0);
});
