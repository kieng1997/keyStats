// Entry point for the vendored Cubism Framework bundle.
// Bundled by scripts/build-desktop-pet-vendor.ps1 using the SDK passed on the command line.
import { CubismFramework, Option, LogLevel } from '__SDK__/Framework/src/live2dcubismframework';
import { CubismMoc } from '__SDK__/Framework/src/model/cubismmoc';
import { CubismRenderer_WebGL } from '__SDK__/Framework/src/rendering/cubismrenderer_webgl';
import { CubismMatrix44 } from '__SDK__/Framework/src/math/cubismmatrix44';
import { CubismModelMatrix } from '__SDK__/Framework/src/math/cubismmodelmatrix';

(window as any).CubismBundle = {
  CubismFramework,
  Option,
  LogLevel,
  CubismMoc,
  CubismRenderer_WebGL,
  CubismMatrix44,
  CubismModelMatrix
};
