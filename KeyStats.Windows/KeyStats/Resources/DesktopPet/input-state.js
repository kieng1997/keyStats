// Pure input-state mapping for the desktop pet viewer.
// Browser-agnostic on purpose: the same file runs in the WPF WebView2 page and in Node tests.
//
// Rules:
//  - Mouse position drives ParamMouseX/Y, kept inside -1..1.
//  - Left/right mouse buttons drive ParamMouseLeftDown/RightDown.
//  - The hand overlay shows while ANY configured key group is held, and CatParamLeftHandDown tracks it.
//  - Overlays are pre-registered, so a key press only toggles visibility (no fetch at input time).
//  - Releasing every key always returns to the base hand, including after key combinations.
(function (root) {
  'use strict';

  var PARAM_MOUSE_X = 'ParamMouseX';
  var PARAM_MOUSE_Y = 'ParamMouseY';
  var PARAM_MOUSE_LEFT_DOWN = 'ParamMouseLeftDown';
  var PARAM_MOUSE_RIGHT_DOWN = 'ParamMouseRightDown';
  var PARAM_LEFT_HAND_DOWN = 'CatParamLeftHandDown';

  function clamp(value, min, max) {
    if (typeof value !== 'number' || !isFinite(value)) return min;
    return value < min ? min : value > max ? max : value;
  }

  /**
   * @param {{hands: Array<{virtualKeys: number[], imageRelativePath: string}>}} model
   */
  function createInputState(model, options) {
    var opts = options || {};
    var hands = (model && model.hands) || [];
    var loaded = opts.loadedHandImages || null;

    // Pre-compute key -> hand indexes so input handling never touches the filesystem.
    var handIndexesByKey = {};
    for (var handIndex = 0; handIndex < hands.length; handIndex++) {
      var virtualKeys = hands[handIndex].virtualKeys || [];
      for (var keyIndex = 0; keyIndex < virtualKeys.length; keyIndex++) {
        var virtualKey = virtualKeys[keyIndex];
        if (typeof virtualKey !== 'number') continue;
        if (!handIndexesByKey[virtualKey]) handIndexesByKey[virtualKey] = [];
        handIndexesByKey[virtualKey].push(handIndex);
      }
    }

    var state = {
      activeHandIndex: -1,
      mouseX: 0,
      mouseY: 0,
      leftDown: false,
      rightDown: false,
      lastPressedKey: null
    };

    // Held keys in press order (oldest first). A new press always wins; when it is released the
    // previous still-held key becomes visible again, and the last release returns to the base hand.
    var pressOrder = [];

    function updatePressOrder(downKeys) {
      var downLookup = {};
      for (var i = 0; i < downKeys.length; i++) {
        downLookup[downKeys[i]] = true;
      }

      // Keep only keys that are still held, preserving their original order.
      var kept = [];
      for (var orderIndex = 0; orderIndex < pressOrder.length; orderIndex++) {
        var held = pressOrder[orderIndex];
        if (downLookup[held]) kept.push(held);
      }

      // Append keys that are newly down, in the order the host reported them.
      for (var keyIndex = 0; keyIndex < downKeys.length; keyIndex++) {
        if (kept.indexOf(downKeys[keyIndex]) < 0) kept.push(downKeys[keyIndex]);
      }

      pressOrder = kept;
    }

    function resolveActiveHand() {
      // Newest still-held key that has an overlay wins.
      for (var i = pressOrder.length - 1; i >= 0; i--) {
        var key = pressOrder[i];
        var candidates = handIndexesByKey[key];
        if (!candidates) continue;
        return { handIndex: candidates[0], key: key };
      }

      return { handIndex: -1, key: null };
    }

    /**
     * Applies one sampled input frame.
     * @param {{keys?: number[], mouseX?: number, mouseY?: number, leftDown?: boolean, rightDown?: boolean}} frame
     *        mouseX/mouseY are fractions of the pet window (0..1) from its top-left corner.
     * @returns {{changed: boolean, handIndex: number, handImage: string|null, params: Object}}
     */
    function applyInput(frame) {
      var input = frame || {};
      var keys = input.keys || [];

      var fractionX = clamp(input.mouseX, 0, 1);
      var fractionY = clamp(input.mouseY, 0, 1);
      // Normalised -1..1 with the window centre as origin, Y flipped for Live2D's up axis.
      var normalisedX = clamp(fractionX * 2 - 1, -1, 1);
      var normalisedY = clamp(1 - fractionY * 2, -1, 1);

      updatePressOrder(keys);
      var resolved = resolveActiveHand();
      var handIndex = resolved.handIndex;

      if (handIndex >= 0 && handIndex !== state.activeHandIndex) {
        state.lastPressedKey = resolved.key;
      }

      var changed =
        handIndex !== state.activeHandIndex ||
        Math.abs(normalisedX - state.mouseX) > 0.0001 ||
        Math.abs(normalisedY - state.mouseY) > 0.0001 ||
        !!input.leftDown !== state.leftDown ||
        !!input.rightDown !== state.rightDown;

      state.activeHandIndex = handIndex;
      state.mouseX = normalisedX;
      state.mouseY = normalisedY;
      state.leftDown = !!input.leftDown;
      state.rightDown = !!input.rightDown;

      var handImage = handIndex >= 0 ? hands[handIndex].imageRelativePath : null;
      if (handImage && loaded && loaded[handImage] === false) {
        handImage = null;
      }

      var params = {};
      params[PARAM_MOUSE_X] = normalisedX;
      params[PARAM_MOUSE_Y] = normalisedY;
      params[PARAM_MOUSE_LEFT_DOWN] = state.leftDown ? 1 : 0;
      params[PARAM_MOUSE_RIGHT_DOWN] = state.rightDown ? 1 : 0;
      params[PARAM_LEFT_HAND_DOWN] = handImage ? 1 : 0;

      return { changed: changed, handIndex: handIndex, handImage: handImage, params: params };
    }

    function getState() {
      return {
        activeHandIndex: state.activeHandIndex,
        lastPressedKey: state.lastPressedKey,
        mouseX: state.mouseX,
        mouseY: state.mouseY,
        leftDown: state.leftDown,
        rightDown: state.rightDown
      };
    }

    return {
      applyInput: applyInput,
      getState: getState,
      handCount: hands.length,
      keysWithOverlay: Object.keys(handIndexesByKey).map(Number)
    };
  }

  var api = {
    createInputState: createInputState,
    params: {
      mouseX: PARAM_MOUSE_X,
      mouseY: PARAM_MOUSE_Y,
      mouseLeftDown: PARAM_MOUSE_LEFT_DOWN,
      mouseRightDown: PARAM_MOUSE_RIGHT_DOWN,
      leftHandDown: PARAM_LEFT_HAND_DOWN
    }
  };

  if (typeof module !== 'undefined' && module.exports) {
    module.exports = api;
  } else {
    root.DesktopPetInput = api;
  }
})(typeof globalThis !== 'undefined' ? globalThis : this);
